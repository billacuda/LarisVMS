namespace LarisVMS.Web.Helpers;

public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long? bytes)
    {
        if (bytes is not { } b || b < 0) return "—";

        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        return unit == 0 ? $"{value.ToString("0", culture)} {Units[unit]}" : $"{value.ToString("0.0", culture)} {Units[unit]}";
    }
}
