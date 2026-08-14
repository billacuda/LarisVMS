namespace LarisVMS.Core.Dtos;

/// <summary>Body for both create and update — an update simply supplies the Id-bearing route
/// segment separately, same shape either way.</summary>
public record SaveZoneRequest(string Name, string Kind, string PolygonJson, double Sensitivity, bool IsEnabled = true);
