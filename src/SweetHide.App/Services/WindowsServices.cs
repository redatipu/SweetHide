using System.Security.Cryptography;

using Microsoft.Win32;

using SweetHide.Presentation;
using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

/// <summary>DPAPI: only this Windows account on this PC can decrypt the vault.</summary>
internal sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = "SweetHide vault v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Classic Explorer context-menu verbs under HKCU (no admin needed). Windows 11 lists them under "Show more options".
/// </summary>
internal sealed class ExplorerIntegration : IExplorerIntegration
{
    private const string HideVerb = "SweetHide.Hide";
    private const string RevealVerb = "SweetHide.Reveal";
    private static readonly string[] CoverExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private static readonly string[] StegoExtensions = [".png"];

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SweetHide.exe");

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(VerbKey(".png", RevealVerb));
            return key is not null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var extension in CoverExtensions)
        {
            Apply(extension, HideVerb, Strings.ExplorerHide, "--hide", enabled);
        }

        foreach (var extension in StegoExtensions)
        {
            Apply(extension, RevealVerb, Strings.ExplorerReveal, "--reveal", enabled);
        }
    }

    /// <summary>Re-points the verbs at this executable, in case the app folder was moved.</summary>
    public void RefreshIfEnabled()
    {
        if (IsEnabled)
        {
            SetEnabled(true);
        }
    }

    private static string VerbKey(string extension, string verb) => $@"Software\Classes\SystemFileAssociations\{extension}\shell\{verb}";

    private static void Apply(string extension, string verb, string label, string flag, bool enabled)
    {
        var path = VerbKey(extension, verb);
        if (!enabled)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue("MUIVerb", label);
        key.SetValue("Icon", $"\"{Exe}\",0");
        using var command = key.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{Exe}\" {flag} \"%1\"");
    }
}
