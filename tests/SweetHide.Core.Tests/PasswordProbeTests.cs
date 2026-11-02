using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class PasswordProbeTests
{
    private static readonly string[] Wordlist = ["123456", "password", "qwerty", "", "letmein", "ร้านกาแฟ", "dragon", "monkey"];

    [Fact]
    public void Finds_the_password_in_a_wordlist_with_progress()
    {
        var engine = TestImages.Engine();
        var stego = engine.Embed(TestImages.Photo(80, 60), Payload.FromText("audit me"), "ร้านกาแฟ");
        var probe = engine.CreatePasswordProbe(stego);
        stego.Pixels.Clear(); // the probe keeps its own copy

        Assert.True(probe.CanHoldPasswordData);
        Assert.Equal(KdfProfile.Normal, probe.Profile);
        Assert.False(probe.TryPassword("dragon"));
        Assert.True(probe.TryPassword("ร้านกาแฟ"));
        Assert.True(probe.TryPassword("ร้านกาแฟ".Normalize(System.Text.NormalizationForm.FormD)));
        Assert.False(probe.TryPassword("broken \uD800 surrogate"));

        var reports = new List<long>();
        var found = probe.FindPassword(Wordlist, progress: new SyncProgress(reports), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ร้านกาแฟ", found);
        Assert.Equal([1, 2, 3, 4, 5, 6], reports);
    }

    [Fact]
    public void Returns_null_when_nothing_matches_and_honours_cancellation()
    {
        var engine = TestImages.Engine();
        var probe = engine.CreatePasswordProbe(engine.Embed(TestImages.Photo(80, 60), Payload.FromText("x"), "not in the list"));
        Assert.Null(probe.FindPassword(Wordlist, cancellationToken: TestContext.Current.CancellationToken));

        using var cancel = new CancellationTokenSource();
        var tested = 0L;
        var progress = new SyncProgress(_ => { }, n =>
        {
            tested = n;
            if (n == 2)
            {
                cancel.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => probe.FindPassword(Wordlist, progress: progress, cancellationToken: cancel.Token));
        Assert.Equal(2, tested);
    }

    [Fact]
    public void Works_for_key_files_v1_images_and_deniable_halves()
    {
        var engine = TestImages.Engine();
        var keyFile = KeyFile.FromBytes("probe key file"u8);
        var withKeyFile = engine.CreatePasswordProbe(engine.Embed(TestImages.Photo(80, 60), Payload.FromText("x"), "kf password", keyFile));
        Assert.False(withKeyFile.TryPassword("kf password"));
        Assert.True(withKeyFile.TryPassword("kf password", keyFile));
        Assert.Equal("kf password", withKeyFile.FindPassword(["a", "kf password"], keyFile, cancellationToken: TestContext.Current.CancellationToken));

        var v1 = engine.CreatePasswordProbe(V1Writer.Embed(TestImages.Photo(80, 60), Payload.FromText("old"), "v1 password", 2, TestImages.FastKdf));
        Assert.True(v1.TryPassword("v1 password"));
        Assert.False(v1.TryPassword("v2 password"));

        var deniable = engine.CreatePasswordProbe(engine.EmbedDeniable(
            TestImages.Photo(80, 60), new DeniablePart(Payload.FromText("a"), "first"), new DeniablePart(Payload.FromText("b"), "second")));
        Assert.True(deniable.TryPassword("first"));
        Assert.True(deniable.TryPassword("second"));
        Assert.False(deniable.TryPassword("third"));
    }

    [Fact]
    public void Tiny_images_cannot_hold_anything()
    {
        var probe = TestImages.Engine().CreatePasswordProbe(TestImages.Photo(5, 5));
        Assert.False(probe.CanHoldPasswordData);
        Assert.False(probe.TryPassword("anything"));
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool, which makes assertions racy).</summary>
    private sealed class SyncProgress(Action<long> report, Action<long>? also = null) : IProgress<long>
    {
        public SyncProgress(List<long> into)
            : this(into.Add)
        {
        }

        public void Report(long value)
        {
            report(value);
            also?.Invoke(value);
        }
    }
}
