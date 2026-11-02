// Fuzz targets for SweetHide.Core. Each target treats the input as hostile and only tolerates the exceptions the
// API documents; anything else (or a hang, or a crash) is a finding.
//
// Local use with AFL++ (Linux):
//   dotnet publish tests/SweetHide.Fuzz -c Release -o fuzz-out
//   dotnet tool install --global SharpFuzz.CommandLine
//   sharpfuzz fuzz-out/SweetHide.Core.dll          # instruments the library in place
//   mkdir -p corpus && head -c 4096 /dev/urandom > corpus/seed
//   afl-fuzz -i corpus -o findings -t 10000 -m none -- fuzz-out/SweetHide.Fuzz extract
//
// libFuzzer: build https://github.com/Metalnem/libfuzzer-dotnet and run it with
//   --target_path=fuzz-out/SweetHide.Fuzz --target_arg=<target>
// (pass "libfuzzer" as the second argument so the harness uses the libFuzzer entry point).
//
// Targets: extract (raw bytes as an RGBA image, password and identity reveal), payload (PayloadSerializer),
// share (ShamirShare decoding and combining), identity (private key file import), recipient (public key parsing).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SharpFuzz;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

var target = args.Length > 0 ? args[0] : "extract";
var engine = new StegoEngine(new KdfParameters(64, 1, 1));
using var identity = Identity.Generate();

Action<ReadOnlySpan<byte>> run = target switch
{
    "extract" => input => Extract(engine, identity, input),
    "payload" => Payload,
    "share" => Share,
    "identity" => ImportIdentity,
    "recipient" => ParseRecipient,
    _ => throw new ArgumentException($"Unknown target {target}."),
};

if (args.Length > 1 && args[1] == "libfuzzer")
{
    Fuzzer.LibFuzzer.Run(span => run(span));
}
else
{
    Fuzzer.OutOfProcess.Run(stream =>
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        run(buffer.ToArray());
    });
}

static void Extract(StegoEngine engine, Identity identity, ReadOnlySpan<byte> input)
{
    // First two bytes pick the width; the rest are RGBA pixels (truncated to whole rows).
    if (input.Length < 6)
    {
        return;
    }

    var width = 1 + (BinaryPrimitives.ReadUInt16LittleEndian(input) % 256);
    var pixels = input[2..];
    var height = pixels.Length / (width * 4);
    if (height == 0)
    {
        return;
    }

    var image = new PixelBuffer(width, height, pixels[..(width * height * 4)].ToArray());
    ExpectStego(() => engine.Extract(image, "fuzz password"));
    ExpectStego(() => engine.Extract(image, identity));
    _ = engine.CreatePasswordProbe(image).TryPassword("fuzz password");
}

static void Payload(ReadOnlySpan<byte> input)
{
    try
    {
        PayloadSerializer.Deserialize(input);
    }
    catch (InvalidDataException)
    {
    }
}

static void Share(ReadOnlySpan<byte> input)
{
    // Two shares cut from the input: decode both, then combine them.
    var first = input[..(input.Length / 2)].ToArray();
    var second = input[(input.Length / 2)..].ToArray();
    ExpectStego(() => StegoEngine.CombineShares([ShamirShare.Decode(first), ShamirShare.Decode(second)]));
}

static void ImportIdentity(ReadOnlySpan<byte> input)
{
    var text = Encoding.UTF8.GetString(input);
    Expect<FormatException, CryptographicException>(() => Identity.Import(text, "pass").Dispose());
}

static void ParseRecipient(ReadOnlySpan<byte> input) => _ = Recipient.TryParse(Encoding.UTF8.GetString(input), out _);

static void ExpectStego(Action action)
{
    try
    {
        action();
    }
    catch (StegoException ex) when (ex.Error is StegoError.NotFoundOrWrongPassword or StegoError.Corrupted or StegoError.UnsupportedFormat
        or StegoError.SplitShare or StegoError.NotEnoughShares or StegoError.SharesDoNotMatch)
    {
    }
}

static void Expect<T1, T2>(Action action)
    where T1 : Exception
    where T2 : Exception
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is T1 or T2)
    {
    }
}
