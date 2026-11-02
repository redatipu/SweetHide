using System.Globalization;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Robust;

namespace SweetHide.Presentation.ViewModels;

/// <summary>A label and value in a details panel ("Protection: Password").</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>Localized wording for the engine's enums and errors.</summary>
internal static class StegoText
{
    private static CultureInfo Culture => Strings.Culture ?? CultureInfo.CurrentUICulture;

    /// <summary>The status banner for an engine error: every <see cref="StegoError"/> gets its own explanation.</summary>
    public static (StatusSeverity Severity, string Title, string Message) ForError(StegoException error) => error.Error switch
    {
        StegoError.NotFoundOrWrongPassword => (StatusSeverity.Warning, Strings.RevealNotFoundTitle, Strings.RevealNotFound),
        StegoError.Corrupted => (StatusSeverity.Error, Strings.RevealCorruptedTitle, Strings.RevealCorrupted),
        StegoError.UnsupportedFormat => (StatusSeverity.Error, Strings.RevealNewerVersionTitle, Strings.RevealNewerVersion),
        StegoError.SplitShare => (StatusSeverity.Informational, Strings.ShareFoundTitle, Strings.SplitShareHint),
        StegoError.NotEnoughShares => (StatusSeverity.Warning, Strings.ShareFoundTitle, Strings.NotEnoughShares),
        StegoError.SharesDoNotMatch => (StatusSeverity.Error, Strings.SharesDoNotMatchTitle, Strings.SharesDoNotMatch),
        StegoError.InsufficientCapacity => (StatusSeverity.Error, Strings.ErrorTitle, Strings.CapacityOver),
        _ => (StatusSeverity.Error, Strings.ErrorTitle, error.Message),
    };

    public static string KdfName(KdfProfile profile) => profile switch
    {
        KdfProfile.Fast => Strings.KdfFast,
        KdfProfile.Paranoid => Strings.KdfParanoid,
        _ => Strings.KdfNormal,
    };

    public static string ModeName(HidingMode mode) => mode switch
    {
        HidingMode.Deniable => Strings.ModeDeniable,
        HidingMode.Recipients => Strings.ModeRecipients,
        _ => Strings.ModePassword,
    };

    /// <summary>The app a robust profile is made for (brand names are not translated).</summary>
    public static string ProfileName(RobustProfile? profile) => profile?.Name switch
    {
        "line" => "LINE",
        "telegram" => "Telegram",
        null => "LINE / Telegram",
        var name => name,
    };

    public static string Percent(double ratio) => ratio.ToString("0.#%", Culture);

    public static string YesNo(bool value) => value ? Strings.Yes : Strings.No;

    /// <summary>Format version, protection, password hardening, bits and adaptive embedding.</summary>
    public static IEnumerable<DetailRow> InfoRows(HiddenDataInfo info, string? protection = null)
    {
        yield return new DetailRow(Strings.DetailFormat, Strings.Format(Strings.DetailFormatValue, info.FormatVersion));
        yield return new DetailRow(Strings.DetailProtection, protection ?? ModeName(info.Mode));
        if (info.KdfProfile is { } kdf)
        {
            yield return new DetailRow(Strings.DetailKdf, KdfName(kdf));
        }

        yield return new DetailRow(Strings.DetailBits, info.BitsPerChannel.ToString(Culture));
        yield return new DetailRow(Strings.DetailAdaptive, YesNo(info.Adaptive));
    }

    /// <summary>Which app the photo was made for and how much the app's recompression damaged the hidden bits.</summary>
    public static IEnumerable<DetailRow> RobustRows(RobustRevealResult robust)
    {
        yield return new DetailRow(Strings.DetailProtection, Strings.Format(Strings.ModeRobust, ProfileName(robust.Profile)));
        yield return new DetailRow(Strings.DetailBitErrors, Strings.Format(Strings.DetailBitErrorsValue, Percent(robust.RawBitErrorRate), DamageWord(robust.RawBitErrorRate)));
        yield return new DetailRow(Strings.DetailRepaired, robust.CorrectedSymbols.ToString(Culture));
    }

    private static string DamageWord(double rate) => rate switch
    {
        < 0.02 => Strings.DamageNone,
        < 0.10 => Strings.DamageSome,
        _ => Strings.DamageHeavy,
    };
}
