namespace LarisVMS.Web.Pages.Views;

/// <summary>LiveView.PathLingerSeconds: how long a live travel path stays on screen after its object
/// stops moving or leaves, before it dissolves from its start to its end.</summary>
public static class PathLinger
{
    public const string SettingKey = "LiveView.PathLingerSeconds";
    public const int Default = 5;
    public const int Max = 30;

    public static int Clamp(int seconds) => Math.Clamp(seconds, 0, Max);
}
