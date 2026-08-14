using System.Xml.Linq;

namespace LarisVMS.Onvif.Soap;

/// <summary>Posts a SOAP 1.1 request to an ONVIF service endpoint and returns the parsed response
/// body, translating a soap:Fault into <see cref="OnvifFaultException"/>.
///
/// The request envelope is always SOAP 1.1, but responses are matched by local element name rather
/// than a fixed namespace: real devices (confirmed against an Amcrest IP5M-B1276EW-AI) reply with a
/// SOAP 1.2 envelope (`http://www.w3.org/2003/05/soap-envelope`) regardless of the request's SOAP
/// version, so hardcoding the 1.1 namespace here silently failed to find the Body on every call.
/// </summary>
public class OnvifSoapClient(HttpClient httpClient)
{
    public async Task<XElement> PostAsync(Uri serviceUri, string action, string bodyXml,
        OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var envelope = OnvifSoapEnvelope.Build(bodyXml, credentials);

        using var request = new HttpRequestMessage(HttpMethod.Post, serviceUri)
        {
            Content = new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", $"\"{action}\"");

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        XDocument doc;
        try { doc = XDocument.Parse(responseBody); }
        catch (Exception ex)
        {
            response.EnsureSuccessStatusCode();
            throw new OnvifFaultException($"Response was not valid XML: {ex.Message}", null);
        }

        var body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new OnvifFaultException("Response had no SOAP Body.", null);

        var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            var code = fault.Element("faultcode")?.Value ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
            var reason = fault.Element("faultstring")?.Value
                ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                ?? "Unknown SOAP fault.";
            throw new OnvifFaultException(reason, code);
        }

        // ONVIF devices return a non-2xx status alongside a non-fault body only when something
        // outside the SOAP contract broke (auth proxy, gateway) — surface that too.
        response.EnsureSuccessStatusCode();

        return body.Elements().FirstOrDefault()
            ?? throw new OnvifFaultException("Response Body was empty.", null);
    }
}
