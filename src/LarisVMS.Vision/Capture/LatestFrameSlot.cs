namespace LarisVMS.Vision.Capture;

/// <summary>A frame plus the wall-clock instant the reader pulled it off ffmpeg's stdout. That
/// instant — not <c>DateTime.UtcNow</c> read later, after inference — is what every downstream stage
/// treats as "when this was true": movement classification, best-frame tracking, the span's
/// <c>BestFrameAtUtc</c> that the Snapshots crop seeks to, and (pass D) the live-overlay timestamp.
/// Reading the clock after inference pushed that instant later than reality by the whole inference
/// duration plus however long the frame sat in the slot.</summary>
public readonly record struct CapturedFrame(byte[] Frame, DateTime CapturedUtc);

/// <summary>
/// A single-slot handoff between the frame reader and the inference stage.
///
/// The reader must drain the ffmpeg pipe continuously or the OS buffer fills, ffmpeg blocks, and
/// displayed latency grows without bound. Inference is slower than the stream, so it cannot be the
/// thing that paces the reader. This slot decouples them: the reader always writes the newest
/// frame, overwriting whatever the inference stage has not yet picked up.
///
/// The consequence is deliberate — frames are dropped under load, and latency stays bounded at
/// roughly one inference period instead of growing forever.
///
/// Ported from aitest (g:\Projects\aitest\src\Aitest.Vision\Capture\LatestFrameSlot.cs); pass 4a
/// swapped the buffered <c>SKBitmap</c> for a raw byte buffer — the frame is now either BGRA or
/// packed nv12 and never a decoded bitmap, so there is nothing to decode/copy through SkiaSharp.
/// </summary>
public sealed class LatestFrameSlot : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);

    // Two buffers swapped under the lock, so publishing a frame allocates nothing. The capture
    // instant rides alongside each buffer and is swapped with it.
    private byte[] _back;
    private byte[] _front;
    private DateTime _backCapturedUtc;
    private DateTime _frontCapturedUtc;

    private bool _hasFrame;
    private bool _disposed;

    private long _published;
    private long _consumed;

    public LatestFrameSlot(int frameBytes)
    {
        FrameBytes = frameBytes;
        _back = new byte[frameBytes];
        _front = new byte[frameBytes];
    }

    /// <summary>Size, in bytes, of one frame buffer.</summary>
    public int FrameBytes { get; private set; }

    /// <summary>Frames handed to the slot by the reader.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>Frames actually taken by the inference stage. The gap is dropped frames.</summary>
    public long ConsumedCount => Interlocked.Read(ref _consumed);

    /// <summary>
    /// Changes the frame size, reallocating the buffers. Needed because a camera's stream can be
    /// reconnected at a different resolution. Any frame currently buffered but not yet consumed is
    /// discarded (it was decoded at the old size). A deliberate momentary frame drop, not a bug.
    /// </summary>
    public void Resize(int frameBytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frameBytes == FrameBytes) return;

            _back = new byte[frameBytes];
            _front = new byte[frameBytes];
            FrameBytes = frameBytes;
            _hasFrame = false;
        }
    }

    /// <summary>
    /// Gives the writer the back buffer to fill, then publishes it together with the instant the
    /// reader captured it. The buffer is owned by the slot and is only valid inside the callback.
    /// </summary>
    public void Publish(DateTime capturedUtc, Action<byte[]> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fill(_back);
            _backCapturedUtc = capturedUtc;

            (_back, _front) = (_front, _back);
            (_backCapturedUtc, _frontCapturedUtc) = (_frontCapturedUtc, _backCapturedUtc);
            _hasFrame = true;
        }

        Interlocked.Increment(ref _published);

        if (_available.CurrentCount == 0)
        {
            try { _available.Release(); }
            catch (SemaphoreFullException) { /* raced with another Publish — frame is in _front either way */ }
        }
    }

    /// <summary>
    /// Waits for a frame and copies it out, with the instant the reader captured it. Returns null
    /// only if cancelled or disposed — never as a side effect of a concurrent <see cref="Resize"/>,
    /// which callers must not mistake for shutdown. The copy is necessary: the reader will overwrite
    /// the slot's buffers while inference runs.
    ///
    /// Allocates a fresh buffer per call. At a detection frame size that is a Large Object Heap
    /// allocation every frame (640x640 BGRA is 1.56 MB), so the hot path uses the
    /// <see cref="TakeAsync(byte[], CancellationToken)"/> overload with a reusable buffer instead —
    /// this one stays for callers that genuinely want an independently-owned frame.
    /// </summary>
    public async Task<CapturedFrame?> TakeAsync(CancellationToken cancellationToken)
        => await TakeAsync(new byte[FrameBytes], cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// As <see cref="TakeAsync(CancellationToken)"/>, but copies into <paramref name="destination"/>
    /// rather than allocating — the returned <see cref="CapturedFrame.Frame"/> *is* that buffer.
    ///
    /// The caller owns the buffer and must be finished with it before the next call: a single
    /// consumer loop that reads the frame synchronously within one iteration (which is what
    /// CameraDetectionPipeline's inference loop does — inference, then the eager snapshot crop, both
    /// synchronous) can safely reuse one buffer forever. Do not hand the frame to anything that
    /// outlives the iteration.
    ///
    /// <paramref name="destination"/> must be at least <see cref="FrameBytes"/> long. A buffer that
    /// has been outgrown by a concurrent <see cref="Resize"/> throws rather than silently copying a
    /// partial frame — the caller has to re-rent at the new size.
    /// </summary>
    public async Task<CapturedFrame?> TakeAsync(byte[] destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);

        while (true)
        {
            try
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            lock (_gate)
            {
                if (_disposed) return null;
                if (!_hasFrame) continue; // a concurrent Resize discarded the frame that woke us

                if (destination.Length < FrameBytes)
                {
                    throw new ArgumentException(
                        $"Destination buffer is {destination.Length} bytes, need at least {FrameBytes}.",
                        nameof(destination));
                }

                Array.Copy(_front, destination, FrameBytes);
                Interlocked.Increment(ref _consumed);
                return new CapturedFrame(destination, _frontCapturedUtc);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _available.Dispose();
    }
}
