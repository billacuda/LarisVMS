namespace Rcordr.Onvif.Soap;

/// <summary>Plaintext credentials for one SOAP call. Callers (Rcordr.Infrastructure) are
/// responsible for decrypting Camera.Username/Password via SecretProtection before constructing
/// this — the ONVIF client layer never touches the encryption converter.</summary>
public record OnvifCredentials(string Username, string Password);
