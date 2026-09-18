namespace LarisVMS.Web.Helpers;

/// <summary>Settings keys shared between Admin/Settings/Events (where it's edited) and
/// Views/Play + PlayViewModelBuilder (where it's read to position live-tile badges) — previously
/// lived as a constant on Admin/Settings/EventsModel, which no longer exists now that page is a
/// Blazor component.</summary>
public static class EventSettingsKeys
{
    public const string EventBadgeCornerKey = "Events.BadgeCorner";
}
