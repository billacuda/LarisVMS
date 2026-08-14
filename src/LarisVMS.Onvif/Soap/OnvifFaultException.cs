namespace LarisVMS.Onvif.Soap;

public class OnvifFaultException(string reason, string? code) : Exception(reason)
{
    public string? Code { get; } = code;
}
