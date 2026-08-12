namespace NidusVMS.Onvif.Clients;

/// <summary>One WS-BaseNotification message from a PullMessages response — Topic plus whatever
/// tt:SimpleItem name/value pairs the device's tt:Message/tt:Data element carried (the item set and
/// names vary by topic and by vendor, so this stays a flat map rather than a typed-per-topic model).</summary>
public record OnvifNotificationMessage(string Topic, DateTime? UtcTime, IReadOnlyDictionary<string, string> SimpleItems);
