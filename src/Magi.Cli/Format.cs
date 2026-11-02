using System.Globalization;

namespace Magi.Cli;

internal static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    public static string Percent(double ratio) => string.Create(CultureInfo.InvariantCulture, $"{ratio * 100:0.##}%");

    public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
