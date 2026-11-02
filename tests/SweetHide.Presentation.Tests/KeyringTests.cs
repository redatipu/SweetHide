using SweetHide.Core.Crypto;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.Tests;

public sealed class KeyringTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sweethide-keys-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Keeps_my_key_and_friends_encrypted_and_reloads_them()
    {
        var path = Path.Combine(_dir.FullName, "nested", "keys.dat");
        var keyring = new ProtectedKeyring(path, new XorProtector());
        var changes = 0;
        keyring.Changed += (_, _) => changes++;
        Assert.False(keyring.HasIdentity);
        Assert.Null(keyring.OpenIdentity());

        var publicKey = keyring.GenerateIdentity();
        Assert.StartsWith("sweethide1", publicKey, StringComparison.Ordinal);
        using var friend = Identity.Generate();
        keyring.SaveFriend("  Ploy ", friend.Recipient.ToString());
        keyring.SaveFriend("aom", publicKey);
        Assert.Equal(3, changes);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("sweethide1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);

        var reloaded = new ProtectedKeyring(path, new XorProtector());
        Assert.Equal(publicKey, reloaded.PublicKey);
        using (var identity = reloaded.OpenIdentity())
        {
            Assert.Equal(publicKey, identity!.Recipient.ToString());
        }

        Assert.Equal(["aom", "Ploy"], reloaded.Friends.Select(f => f.Name));
        reloaded.SaveFriend("PLOY", publicKey);
        reloaded.RemoveFriend("Aom");
        Assert.Equal([new FriendKey("PLOY", publicKey)], reloaded.Friends);
    }

    [Fact]
    public void Refuses_keys_that_are_not_sweethide_public_keys()
    {
        var keyring = new ProtectedKeyring(Path.Combine(_dir.FullName, "keys.dat"), new XorProtector());
        using var friend = Identity.Generate();
        var key = friend.Recipient.ToString();

        Assert.Throws<FormatException>(() => keyring.SaveFriend("typo", key[..^1] + (key[^1] == 'q' ? 'p' : 'q')));
        Assert.Throws<FormatException>(() => keyring.SaveFriend("empty", string.Empty));
        Assert.Empty(keyring.Friends);
    }

    [Fact]
    public void Generating_again_replaces_my_key()
    {
        var keyring = new ProtectedKeyring(Path.Combine(_dir.FullName, "keys.dat"), new XorProtector());
        var first = keyring.GenerateIdentity();
        var second = keyring.GenerateIdentity();
        Assert.NotEqual(first, second);
        Assert.Equal(second, keyring.PublicKey);
    }

    [Fact]
    public void Unreadable_keyring_starts_empty_and_is_kept_aside()
    {
        var path = Path.Combine(_dir.FullName, "keys.dat");
        File.WriteAllText(path, "not encrypted json");
        var keyring = new ProtectedKeyring(path, new XorProtector());
        Assert.False(keyring.HasIdentity);
        Assert.True(File.Exists(path + ".unreadable"));
    }
}
