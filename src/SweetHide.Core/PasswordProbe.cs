using SweetHide.Core.Crypto;
using SweetHide.Core.Stego;

namespace SweetHide.Core;

/// <summary>
/// Tests candidate passwords against one of your own images, for example to check that a wordlist does not contain
/// the password you used. Create it once per image with <see cref="StegoEngine.CreatePasswordProbe"/>: the slot
/// layout, the salt and the KDF profile are worked out once, so each candidate costs one Argon2id run (two for an
/// image whose profile differs from the v1 settings, because v1 images are tried too).
/// </summary>
/// <remarks>
/// Only the encrypted header is checked, never the body, so a match is cheap to confirm and nothing is decrypted.
/// Thread-safe: callers may test candidates from several threads, though each Argon2id run already uses several
/// cores. This is exactly what an attacker holding the image would do, so the time per candidate is also what
/// protects you: see <see cref="Profile"/>.
/// </remarks>
public sealed class PasswordProbe
{
    private readonly StegoImageReader _reader;

    internal PasswordProbe(StegoImageReader reader) => _reader = reader;

    /// <summary>False when the image is too small to hold password-protected data at all.</summary>
    public bool CanHoldPasswordData => _reader.CanHoldPasswordData;

    /// <summary>The Argon2id profile each candidate is tested with (what the image's salt selects).</summary>
    public KdfProfile Profile => _reader.Profile;

    /// <summary>True if <paramref name="password"/> opens the image (also if the data behind it is damaged).</summary>
    public bool TryPassword(ReadOnlySpan<char> password) => TryPassword(password, keyFile: null);

    /// <summary>True if <paramref name="password"/> together with <paramref name="keyFile"/> opens the image.</summary>
    public bool TryPassword(ReadOnlySpan<char> password, KeyFile? keyFile)
    {
        if (password.IsEmpty)
        {
            return false;
        }

        try
        {
            using var match = _reader.TryPassword(password, keyFile);
            return match is not null;
        }
        catch (StegoException)
        {
            return true; // a header authenticated: the password is right even if what follows is damaged
        }
    }

    /// <summary>Tests candidates in order until one matches.</summary>
    /// <param name="candidates">Passwords to try, e.g. lines of a wordlist. Empty entries are skipped.</param>
    /// <param name="keyFile">Key file to combine with every candidate, if the image used one.</param>
    /// <param name="progress">Receives the number of candidates tested so far, after each one.</param>
    /// <param name="cancellationToken">Stops between candidates.</param>
    /// <returns>The matching password, or null when none matched.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public string? FindPassword(
        IEnumerable<string> candidates, KeyFile? keyFile = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        long tested = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = !string.IsNullOrEmpty(candidate) && TryPassword(candidate, keyFile);
            progress?.Report(++tested);
            if (match)
            {
                return candidate;
            }
        }

        return null;
    }
}
