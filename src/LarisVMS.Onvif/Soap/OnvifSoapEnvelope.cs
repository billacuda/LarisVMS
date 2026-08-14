using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Onvif.Soap;

/// <summary>
/// Builds SOAP 1.1 envelopes with an optional WS-Security UsernameToken digest header. SOAP 1.1
/// (not 1.2) is used because the great majority of ONVIF camera firmware is built on the gSOAP
/// toolkit, which is most reliably interoperable over 1.1 — this matches what most working .NET
/// ONVIF client implementations do in practice.
///
/// PasswordDigest = Base64(SHA1(Nonce-bytes + Created-bytes + Password-bytes)), per the WS-Security
/// UsernameToken Profile 1.0. Nonce and Created are regenerated per call — reusing them would let a
/// captured request be replayed.
/// </summary>
public static class OnvifSoapEnvelope
{
    public static string Build(string bodyXml, OnvifCredentials? credentials)
    {
        var header = credentials is null ? string.Empty : BuildSecurityHeader(credentials);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Header>{header}</s:Header>
              <s:Body>{bodyXml}</s:Body>
            </s:Envelope>
            """;
    }

    private static string BuildSecurityHeader(OnvifCredentials credentials)
    {
        var nonceBytes = RandomNumberGenerator.GetBytes(20);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var digestInput = new byte[nonceBytes.Length + Encoding.UTF8.GetByteCount(created) + Encoding.UTF8.GetByteCount(credentials.Password)];
        var offset = 0;
        nonceBytes.CopyTo(digestInput, offset); offset += nonceBytes.Length;
        offset += Encoding.UTF8.GetBytes(created, 0, created.Length, digestInput, offset);
        Encoding.UTF8.GetBytes(credentials.Password, 0, credentials.Password.Length, digestInput, offset);

        var digest = Convert.ToBase64String(SHA1.HashData(digestInput));
        var nonceB64 = Convert.ToBase64String(nonceBytes);

        return $"""
            <Security xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                      xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
              <UsernameToken>
                <Username>{System.Security.SecurityElement.Escape(credentials.Username)}</Username>
                <Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</Password>
                <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonceB64}</Nonce>
                <wsu:Created>{created}</wsu:Created>
              </UsernameToken>
            </Security>
            """;
    }
}
