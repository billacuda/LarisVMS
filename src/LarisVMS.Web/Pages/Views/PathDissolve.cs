namespace LarisVMS.Web.Pages.Views;

/// <summary>LiveView.PathDissolveSeconds: how long a live travel path takes to dissolve from its start
/// to its end once its linger time (<see cref="PathLinger"/>) is up.</summary>
public static class PathDissolve
{
    public const string SettingKey = "LiveView.PathDissolveSeconds";
    public const int Default = 3;
    public const int Min = 1;
    public const int Max = 15;

    public static int Clamp(int seconds) => Math.Clamp(seconds, Min, Max);
}
