namespace NidusVMS.Core.Enums;

/// <summary>Rollup of an ExportJob's own ExportJobItems, recomputed by ExportService whenever an
/// item's status changes rather than edited independently. Rule, applied in this order: any item
/// still Queued -&gt; Queued; else any item Running -&gt; Running; else every item is terminal (Done or
/// Failed) and at least one is Done -&gt; Done (a partial success is still a job with something to
/// download — the Exports page shows each item's own status so it's obvious which camera(s) actually
/// failed); else every terminal item is Failed -&gt; Failed.</summary>
public enum ExportJobStatus
{
    Queued = 0,
    Running = 1,
    Done = 2,
    Failed = 3
}

/// <summary>One ExportJobItem's own state: Queued (created, not yet picked up by
/// ExportJobDispatcher) -&gt; Running (dispatched to its node, which accepted it and is running ffmpeg)
/// -&gt; Done | Failed.</summary>
public enum ExportItemStatus
{
    Queued = 0,
    Running = 1,
    Done = 2,
    Failed = 3
}
