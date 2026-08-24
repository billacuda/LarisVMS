using SkiaSharp;

namespace LarisVMS.Vision.Capture;

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
/// Ported near-verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Capture\LatestFrameSlot.cs).
/// </summary>
public sealed class LatestFrameSlot : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);

    // Two buffers swapped under the lock, so publishing a frame allocates nothing.
    private SKBitmap _back;
    private SKBitmap _front;

    private bool _hasFrame;
    private bool _disposed;

    private long _published;
    private long _consumed;

    public LatestFrameSlot(int width, int height)
    {
        Width = width;
        Height = height;

        _back = NewBitmap(width, height);
        _front = NewBitmap(width, height);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Frames handed to the slot by the reader.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>Frames actually taken by the inference stage. The gap is dropped frames.</summary>
    public long ConsumedCount => Interlocked.Read(ref _consumed);

    /// <summary>
    /// Changes the slot's frame dimensions, reallocating its buffers. Needed because a camera's
    /// stream can be reconnected at a different resolution and the slot's SKBitmaps are
    /// fixed-size once allocated.
    ///
    /// Any frame currently buffered but not yet consumed is discarded: it was decoded at the old
    /// size and would be meaningless at the new one. This is a deliberate, momentary frame drop,
    /// not a bug.
    /// </summary>
    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (width == Width && height == Height)
            {
                return;
            }

            _back.Dispose();
            _front.Dispose();

            _back = NewBitmap(width, height);
            _front = NewBitmap(width, height);

            Width = width;
            Height = height;
            _hasFrame = false;
        }
    }

    /// <summary>
    /// Gives the writer the buffer to fill, then publishes it. The buffer is owned by the slot and
    /// is only valid inside the callback.
    /// </summary>
    public void Publish(Action<SKBitmap> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fill(_back);

            (_back, _front) = (_front, _back);
            _hasFrame = true;
        }

        Interlocked.Increment(ref _published);

        // Release only if nothing is already pending; the slot holds at most one frame.
        if (_available.CurrentCount == 0)
        {
            try
            {
                _available.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another Publish. The frame is in _front either way.
            }
        }
    }

    /// <summary>
    /// Waits for a frame and copies it out. Returns null only if cancelled or disposed -- never
    /// as a side effect of a concurrent <see cref="Resize"/>, which callers must not mistake for
    /// shutdown (a typical consumer loop treats a null return as "stop").
    /// The copy is necessary: the reader will overwrite the slot's buffers while inference runs.
    /// </summary>
    public async Task<SKBitmap?> TakeAsync(CancellationToken cancellationToken)
    {
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
                if (_disposed)
                {
                    return null;
                }

                if (!_hasFrame)
                {
                    // A concurrent Resize discarded the frame that woke us up -- not a shutdown,
                    // just nothing to hand back yet. Wait for the next real Publish.
                    continue;
                }

                var copy = _front.Copy();
                Interlocked.Increment(ref _consumed);
                return copy;
            }
        }
    }

    private static SKBitmap NewBitmap(int width, int height)
        => new(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _back.Dispose();
            _front.Dispose();
        }

        _available.Dispose();
    }
}
