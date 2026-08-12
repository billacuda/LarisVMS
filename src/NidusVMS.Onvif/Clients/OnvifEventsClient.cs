using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NidusVMS.Onvif.Soap;

namespace NidusVMS.Onvif.Clients;

/// <summary>
/// ONVIF ver10 events service — WS-BaseNotification PullPoint subscription and polling. M8 pass 6:
/// the first ONVIF client in this codebase driven from a long-lived polling loop rather than a
/// one-shot request/response — see NidusVMS.Node's CameraEventSession for how it's actually used.
/// </summary>
public class OnvifEventsClient(OnvifSoapClient soap)
{
    private const string EventsNs = "http://www.onvif.org/ver10/events/wsdl";

    /// <summary>Opens a new PullPoint subscription against the camera's Events service, returning
    /// the subscription-specific address PullMessagesAsync must be sent to — NOT necessarily the
    /// same as eventsServiceUri; ONVIF hands back a per-subscription endpoint that can differ in
    /// path (sometimes port) from the Events service it was reached on. Null if the device doesn't
    /// actually support PullPoint despite advertising an Events XAddr, or the call otherwise fails —
    /// callers treat that as "retry later," not fatal (some real firmware advertises the capability
    /// but faults on this specific call).</summary>
    public async Task<Uri?> CreatePullPointSubscriptionAsync(
        Uri eventsServiceUri, OnvifCredentials? credentials, TimeSpan initialTerminationTime, CancellationToken ct = default)
    {
        var body = $"""
            <CreatePullPointSubscription xmlns="{EventsNs}">
              <InitialTerminationTime>{XmlConvert.ToString(initialTerminationTime)}</InitialTerminationTime>
            </CreatePullPointSubscription>
            """;
        var response = await soap.PostAsync(eventsServiceUri, $"{EventsNs}/CreatePullPointSubscriptionRequest", body, credentials, ct);

        var addressText = response.Elements().FirstOrDefault(e => e.Name.LocalName == "SubscriptionReference")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value;
        return addressText is not null && Uri.TryCreate(addressText.Trim(), UriKind.Absolute, out var u) ? u : null;
    }

    /// <summary>Long-polls the subscription for up to <paramref name="timeout"/> for new
    /// notifications (up to <paramref name="messageLimit"/> per call) — the device holds the HTTP
    /// response open until either a message arrives or the timeout elapses, so a caller looping this
    /// gets a steady stream of near-real-time events without needing its own polling interval.</summary>
    public async Task<IReadOnlyList<OnvifNotificationMessage>> PullMessagesAsync(
        Uri pullPointUri, TimeSpan timeout, int messageLimit, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <PullMessages xmlns="{EventsNs}">
              <Timeout>{XmlConvert.ToString(timeout)}</Timeout>
              <MessageLimit>{messageLimit}</MessageLimit>
            </PullMessages>
            """;
        var response = await soap.PostAsync(pullPointUri, $"{EventsNs}/PullMessagesRequest", body, credentials, ct);

        var result = new List<OnvifNotificationMessage>();
        foreach (var notification in response.Elements().Where(e => e.Name.LocalName == "NotificationMessage"))
        {
            var topic = notification.Elements().FirstOrDefault(e => e.Name.LocalName == "Topic")?.Value?.Trim();
            if (string.IsNullOrEmpty(topic)) continue;

            // <Message><tt:Message UtcTime="..."><tt:Data><tt:SimpleItem .../></tt:Data></tt:Message></Message> —
            // an outer wsnt:Message wrapping an inner tt:Message that actually carries the payload;
            // both elements share the local name "Message", so this steps in twice.
            var ttMessage = notification.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Message");

            DateTime? utcTime = DateTime.TryParse(
                ttMessage?.Attribute("UtcTime")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

            var items = new Dictionary<string, string>();
            var data = ttMessage?.Elements().FirstOrDefault(e => e.Name.LocalName == "Data");
            foreach (var item in (data?.Elements() ?? []).Where(e => e.Name.LocalName == "SimpleItem"))
            {
                var name = item.Attribute("Name")?.Value;
                var value = item.Attribute("Value")?.Value;
                if (name is not null && value is not null) items[name] = value;
            }

            result.Add(new OnvifNotificationMessage(topic, utcTime, items));
        }
        return result;
    }
}
