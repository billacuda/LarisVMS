namespace LarisVMS.Web.Helpers;

/// <summary>Model for Shared/_PaginationControls.cshtml. PageUrl builds a full href for a given page
/// number — kept as a delegate rather than a fixed query-string template because each caller's own
/// filter parameters differ (Snapshots has cameraId/from/to/kinds, Audit Logs has q/actor/from/to);
/// this partial only needs to know how to ask for "page N", not what else rides along with it.</summary>
public record PaginationViewModel(int CurrentPage, int TotalPages, Func<int, string> PageUrl);
