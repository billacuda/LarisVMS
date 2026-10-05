using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LarisVMS.Vision.Capture;

/// <summary>
/// When each frame on the vision ffmpeg's stdout arrived over RTSP, for one ffmpeg connection.
///
/// The vision ffmpeg runs with <c>-use_wallclock_as_timestamps 1</c>, so every packet's pts is the
/// wall-clock time it was read off the network, and a trailing <c>showinfo</c> filter prints that
/// pts on stderr for each frame that reaches stdout ("n:" counts exactly those frames). Stamping a
/// frame with this instead of the moment it is read off the pipe keeps the decode/scale/download/
/// pipe delay (and any read stall in this process) out of the stamp. The live video copies the
/// same stream without decoding, so its timeline effectively starts at arrival too; a read-time
/// stamp made every live-view box trail its object by that whole decode path.
///
/// stderr and stdout are separate pipes read by separate tasks, so a frame can be read before its
/// showinfo line has been parsed. <see cref="TakeAsync"/> waits briefly for it. Until the first
/// showinfo line has been seen (an ffmpeg that does not print one), it returns null immediately so
/// a missing stamp never delays frames.
/// </summary>
internal sealed class FrameArrivalStamps
{
    // "[Parsed_showinfo_5 @ 0000...] config in time_base: 1/90000, frame_rate: 25/1"
    private static readonly Regex TimeBaseLine = new(
        @"Parsed_showinfo.*config in time_base:\s*(\d+)/(\d+)", RegexOptions.Compiled);

    // "[Parsed_showinfo_5 @ 0000...] n:   0 pts:153041234567890 pts_time:1.70046e+09 ..."
    // pts (an integer in time_base units) rather than pts_time: pts_time is printed with only ~6
    // significant digits, nowhere near enough for an epoch-seconds value.
    private static readonly Regex FrameLine = new(
        @"Parsed_showinfo.*\bn:\s*(\d+)\s+pts:\s*(-?\d+)", RegexOptions.Compiled);

    private readonly Channel<(long N, DateTime ArrivalUtc)> _channel =
        Channel.CreateUnbounded<(long N, DateTime ArrivalUtc)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private (long N, DateTime ArrivalUtc)? _pending;
    private long _timeBaseNum;
    private long _timeBaseDen;
    private volatile bool _seen;

    /// <summary>True for any showinfo line (so the caller can keep them out of its log). Frame
    /// lines are queued for <see cref="TakeAsync"/>.</summary>
    public bool TryParseLine(string line)
    {
        if (!line.Contains("Parsed_showinfo", StringComparison.Ordinal)) return false;

        if (TimeBaseLine.Match(line) is { Success: true } tb)
        {
            _timeBaseNum = long.Parse(tb.Groups[1].Value, CultureInfo.InvariantCulture);
            _timeBaseDen = long.Parse(tb.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (_timeBaseDen > 0 && FrameLine.Match(line) is { Success: true } frame)
        {
            var n = long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture);
            var pts = long.Parse(frame.Groups[2].Value, CultureInfo.InvariantCulture);
            var ticks = (Int128)pts * _timeBaseNum * TimeSpan.TicksPerSecond / _timeBaseDen;
            if (ticks > 0 && ticks < DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks)
            {
                _seen = true;
                _channel.Writer.TryWrite((n, DateTime.UnixEpoch.AddTicks((long)ticks)));
            }
        }
        return true;
    }

    /// <summary>Arrival time of output frame <paramref name="n"/> (0-based), or null if it isn't
    /// known within <paramref name="wait"/>. Must be called with increasing n.</summary>
    public async ValueTask<DateTime?> TakeAsync(long n, TimeSpan wait, CancellationToken ct)
    {
        if (!_seen) return null;

        CancellationTokenSource? timeout = null;
        try
        {
            while (true)
            {
                (long N, DateTime ArrivalUtc) entry;
                if (_pending is { } pending)
                {
                    _pending = null;
                    entry = pending;
                }
                else if (!_channel.Reader.TryRead(out entry))
                {
                    if (timeout is null)
                    {
                        timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(wait);
                    }
                    entry = await _channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                }

                // Older entries belong to frames whose stamp was already given up on. A newer one
                // means this frame's line never came: hold it for its own frame, no stamp for this one.
                if (entry.N < n) continue;
                if (entry.N > n)
                {
                    _pending = entry;
                    return null;
                }
                return entry.ArrivalUtc;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            timeout?.Dispose();
        }
    }
}
