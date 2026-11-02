namespace SweetHide.App;

/// <summary>What to open at start-up: <c>--hide &lt;file&gt;</c>, <c>--reveal &lt;file&gt;</c>, or just a file (reveal).</summary>
internal sealed record StartupRequest(string Page, string Path)
{
    public static StartupRequest? Parse(IReadOnlyList<string> args)
    {
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--hide" or "--reveal" && i + 1 < args.Count && File.Exists(args[i + 1]))
            {
                return new StartupRequest(arg[2..], System.IO.Path.GetFullPath(args[i + 1]));
            }

            if (arg.StartsWith('-'))
            {
                i += arg is "--smoke-test" or "--lang" or "--theme" ? 1 : 0; // skip their values
                continue;
            }

            if (File.Exists(arg))
            {
                return new StartupRequest("reveal", System.IO.Path.GetFullPath(arg));
            }
        }

        return null;
    }
}
