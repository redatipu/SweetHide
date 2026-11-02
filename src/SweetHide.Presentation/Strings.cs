using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

using SweetHide.Presentation.Services;

namespace SweetHide.Presentation;

/// <summary>
/// UI text in English (neutral) and Thai, from Resources/Strings*.resx. XAML binds to these with
/// <c>{x:Bind p:Strings.Name}</c>; view models use them for status messages.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources = new("SweetHide.Presentation.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Culture used for lookups; <c>null</c> follows <see cref="CultureInfo.CurrentUICulture"/>.</summary>
    public static CultureInfo? Culture { get; set; }

    public static string AppTitle => Get();

    public static string AppSubtitle => Get();

    public static string NavHide => Get();

    public static string NavReveal => Get();

    public static string NavAnalyze => Get();

    public static string NavSettings => Get();

    public static string DropZoneTitle => Get();

    public static string DropZoneHint => Get();

    public static string BrowseImage => Get();

    public static string PasteImage => Get();

    public static string ChangeImage => Get();

    public static string ImageInfoFormat => Get();

    public static string DropToUse => Get();

    public static string DropToAttach => Get();

    public static string ClipboardImageName => Get();

    public static string HideHeader => Get();

    public static string CoverSection => Get();

    public static string MessageSection => Get();

    public static string MessagePlaceholder => Get();

    public static string AttachmentsSection => Get();

    public static string AddFiles => Get();

    public static string RemoveAttachment => Get();

    public static string NoAttachments => Get();

    public static string CapacitySection => Get();

    public static string CapacityNoImage => Get();

    public static string CapacityMeasuring => Get();

    public static string CapacityFormat => Get();

    public static string CapacityWarning => Get();

    public static string CapacityOver => Get();

    public static string PasswordSection => Get();

    public static string PasswordPlaceholder => Get();

    public static string ConfirmPasswordPlaceholder => Get();

    public static string PasswordMismatch => Get();

    public static string PasswordHint => Get();

    public static string StrengthFormat => Get();

    public static string StrengthWeak => Get();

    public static string StrengthFair => Get();

    public static string StrengthGood => Get();

    public static string StrengthStrong => Get();

    public static string AdvancedSection => Get();

    public static string BitsLabel => Get();

    public static string Bits1 => Get();

    public static string Bits2 => Get();

    public static string Bits3 => Get();

    public static string Bits4 => Get();

    public static string HideButton => Get();

    public static string SendAsFileTitle => Get();

    public static string SendAsFileMessage => Get();

    public static string HideSuccessTitle => Get();

    public static string HideSuccessMessage => Get();

    public static string ShowInFolder => Get();

    public static string ErrorTitle => Get();

    public static string ImageLoadFailed => Get();

    public static string FileTooLarge => Get();

    public static string ClipboardNoImage => Get();

    public static string InvalidText => Get();

    public static string RevealHeader => Get();

    public static string ImageSection => Get();

    public static string RevealButton => Get();

    public static string RevealNotFoundTitle => Get();

    public static string RevealNotFound => Get();

    public static string RevealCorrupted => Get();

    public static string RevealSuccessTitle => Get();

    public static string ResultsSection => Get();

    public static string MessageItemFormat => Get();

    public static string CopyText => Get();

    public static string Copied => Get();

    public static string SaveFile => Get();

    public static string SaveAllFiles => Get();

    public static string SavedFileMessage => Get();

    public static string SavedFilesMessage => Get();

    public static string FileSafetyNote => Get();

    public static string AnalyzeHeader => Get();

    public static string AnalyzeIntro => Get();

    public static string ColumnChannel => Get();

    public static string ColumnOnes => Get();

    public static string ColumnChiSquare => Get();

    public static string ColumnProbability => Get();

    public static string AnalyzeExplanation => Get();

    public static string BitPlaneLabel => Get();

    public static string BitPlaneHint => Get();

    public static string SettingsHeader => Get();

    public static string LanguageLabel => Get();

    public static string LanguageDefault => Get();

    public static string LanguageThai => Get();

    public static string LanguageEnglish => Get();

    public static string LanguageRestart => Get();

    public static string ThemeLabel => Get();

    public static string ThemeDefault => Get();

    public static string ThemeLight => Get();

    public static string ThemeDark => Get();

    public static string AboutLabel => Get();

    public static string AboutVersion => Get();

    public static string AboutLicense => Get();

    public static string AboutText => Get();

    /// <summary>Selects the UI language for the rest of the process (takes effect for pages created afterwards).</summary>
    public static void ApplyLanguage(AppLanguage language)
    {
        var culture = language switch
        {
            AppLanguage.Thai => CultureInfo.GetCultureInfo("th-TH"),
            AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => null,
        };

        if (culture is not null)
        {
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        Culture = culture;
    }

    public static string Format(string format, params object?[] args) =>
        string.Format(Culture ?? CultureInfo.CurrentUICulture, format, args);

    public static string StrengthName(PasswordStrength strength) => strength switch
    {
        PasswordStrength.Weak => StrengthWeak,
        PasswordStrength.Fair => StrengthFair,
        PasswordStrength.Good => StrengthGood,
        PasswordStrength.Strong => StrengthStrong,
        _ => string.Empty,
    };

    public static string BitsDescription(int bits) => bits switch
    {
        1 => Bits1,
        2 => Bits2,
        3 => Bits3,
        _ => Bits4,
    };

    internal static string Get([CallerMemberName] string key = "") => Resources.GetString(key, Culture) ?? key;
}
