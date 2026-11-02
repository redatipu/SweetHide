using System.Globalization;

using SweetHide.Core;

namespace SweetHide.Cli;

internal static class Format
{
    public static string Bytes(long bytes) => ByteSize.Format(bytes);

    public static string Percent(double ratio) => string.Create(CultureInfo.InvariantCulture, $"{ratio * 100:0.##}%");

    public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
