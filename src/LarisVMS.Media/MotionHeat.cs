namespace LarisVMS.Media;

/// <summary>
/// Grid-mode motion heat for one camera: per hour, how many analysed frames had motion in each grid
/// cell — a cell counts when its changed-pixel share reaches the camera's grid sensitivity, and masked
/// cells never count. The node reports the running totals every few minutes for the scene heatmap.
///
/// Fed from the motion session's frame loop and read from the node's report loop, so it locks.
/// </summary>
public sealed class MotionHeat(int gridSize, double threshold, IReadOnlySet<int> ignoredCells)
{
    private readonly Lock _gate = new();
    private DateTime _hourUtc;
    private int[] _counts = new int[gridSize * gridSize];
    private readonly List<(DateTime HourUtc, int[] Counts)> _finished = [];

    public int GridSize => gridSize;

    /// <summary>Counts one analysed frame's cell scores into the hour it was captured in.</summary>
    public void Add(DateTime capturedUtc, IReadOnlyList<double> cellScores)
    {
        var hour = new DateTime(capturedUtc.Year, capturedUtc.Month, capturedUtc.Day, capturedUtc.Hour, 0, 0, DateTimeKind.Utc);
        lock (_gate)
        {
            if (hour != _hourUtc)
            {
                if (_hourUtc != default && _counts.Any(c => c > 0)) _finished.Add((_hourUtc, _counts));
                _hourUtc = hour;
                _counts = new int[gridSize * gridSize];
            }
            var n = Math.Min(cellScores.Count, _counts.Length);
            for (var i = 0; i < n; i++)
                if (cellScores[i] >= threshold && !ignoredCells.Contains(i)) _counts[i]++;
        }
    }

    /// <summary>Every hour with motion not yet reported (finished hours, then the current one, as a
    /// copy). Finished hours are handed over; give them back with <see cref="Return"/> if the report
    /// fails. The current hour stays and keeps counting — its report is its running total.</summary>
    public List<(DateTime HourUtc, int[] Counts)> Take()
    {
        lock (_gate)
        {
            var taken = new List<(DateTime, int[])>(_finished);
            _finished.Clear();
            if (_hourUtc != default && _counts.Any(c => c > 0)) taken.Add((_hourUtc, (int[])_counts.Clone()));
            return taken;
        }
    }

    /// <summary>Puts back finished hours from a <see cref="Take"/> whose report didn't get through.</summary>
    public void Return(IEnumerable<(DateTime HourUtc, int[] Counts)> hours)
    {
        lock (_gate)
            foreach (var h in hours)
                if (h.HourUtc != _hourUtc && !_finished.Any(f => f.HourUtc == h.HourUtc)) _finished.Add(h);
    }
}
