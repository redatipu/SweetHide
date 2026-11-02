using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Robust;

namespace SweetHide.Core.Tests.Robust;

public class RobustPasswordModeTests
{
    private const string Password = "นัดกันที่เดิม 19:00";

    private static readonly PixelBuffer Cover = RobustTestImages.Photo(1600, 1200);
    private static readonly StegoEngine Engine = new(TestImages.FastKdf);
    private static readonly Payload Message = Payload.FromText("เจอกันหน้าร้านกาแฟ หกโมงครึ่ง — bring the blue USB stick.");

    [Theory]
    [InlineData("line", 75)]
    [InlineData("telegram", 80)]
    public void Text_survives_the_apps_recompression(string profileName, int appQuality)
    {
        var profile = RobustProfile.All.Single(p => p.Name == profileName);
        var marked = Engine.EmbedRobust(Cover, Message, Password, profile);
        var received = RobustTestImages.Recompress(RobustStegoTests.SendAsJpeg(marked, profile), appQuality);

        var result = Engine.RevealRobust(received, Password);

        Assert.Equal(RobustStatus.Found, result.Status);
        Assert.Equal(profile, result.Profile);
        Assert.Equal(Message.Entries[0].GetText(), result.Payload!.Entries[0].GetText());
    }

    [Fact]
    public void Wrong_password_or_plain_photo_finds_nothing()
    {
        var marked = Engine.EmbedRobust(Cover, Message, Password, RobustProfile.Telegram);

        Assert.Equal(RobustStatus.NotFound, Engine.RevealRobust(marked, "not the password").Status);
        Assert.Equal(RobustStatus.NotFound, Engine.RevealRobust(RobustStego.PrepareCover(Cover, RobustProfile.Telegram), Password).Status);
    }

    [Fact]
    public void A_key_file_is_needed_when_one_was_used()
    {
        var keyFile = KeyFile.FromBytes("a key file only the two of us have"u8);
        var marked = Engine.EmbedRobust(Cover, Message, Password, RobustProfile.Line, keyFile);

        Assert.Equal(RobustStatus.NotFound, Engine.RevealRobust(marked, Password).Status);
        Assert.Equal(RobustStatus.Found, Engine.RevealRobust(marked, Password, keyFile).Status);
    }

    [Fact]
    public void Same_input_gives_a_different_image_each_time()
    {
        // The random salt in front of the ciphertext changes every bit of the sealed payload.
        var a = Engine.EmbedRobust(Cover, Message, Password, RobustProfile.Line);
        var b = Engine.EmbedRobust(Cover, Message, Password, RobustProfile.Line);

        Assert.False(a.Data.AsSpan().SequenceEqual(b.Data));
    }

    [Fact]
    public void Capacity_accounts_for_the_encryption_overhead()
    {
        var raw = RobustStego.MaxPayloadBytes(Cover.Width, Cover.Height, RobustProfile.Telegram);
        var capacity = StegoEngine.GetRobustCapacity(Cover.Width, Cover.Height, RobustProfile.Telegram);
        Assert.Equal(raw - StegoEngine.RobustOverhead, capacity);
        Assert.Equal(0, StegoEngine.GetRobustCapacity(100, 80, RobustProfile.Telegram));

        var tooBig = new Payload([PayloadEntry.FromFile("noise.bin", RobustTestImages.Payload(capacity + 50))]);
        var ex = Assert.Throws<StegoException>(() => Engine.EmbedRobust(Cover, tooBig, Password, RobustProfile.Telegram));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
    }
}
