using System.CommandLine;
using System.Globalization;
using System.Text;

namespace SweetHide.Cli;

/// <summary>
/// <c>sweethide completion bash|zsh|powershell</c>: prints a tab-completion script generated from the command tree,
/// so it never goes out of date. Install instructions are printed by <c>sweethide completion --help</c>.
/// </summary>
internal static class CompletionCommand
{
    private static readonly string[] Shells = ["bash", "zsh", "powershell"];

    public static Command Create(CliContext context, RootCommand root)
    {
        var shell = new Argument<string>("shell") { Description = "bash, zsh or powershell" };
        shell.AcceptOnlyFromAmong(Shells);
        var command = new Command(
            "completion",
            "Print a shell completion script.\n" +
            "  bash:        source <(sweethide completion bash)        (add to ~/.bashrc)\n" +
            "  zsh:         source <(sweethide completion zsh)         (add to ~/.zshrc)\n" +
            "  PowerShell:  sweethide completion powershell | Out-String | Invoke-Expression   (add to $PROFILE)")
        {
            shell,
        };
        command.SetAction(parse =>
        {
            context.Out.Write(Generate(root, parse.GetValue(shell)!));
            return ExitCodes.Success;
        });
        return command;
    }

    internal static string Generate(RootCommand root, string shell)
    {
        var commands = root.Subcommands.ToDictionary(c => c.Name, c => OptionNames(c).Concat(OptionNames(root)).Distinct().ToArray());
        return shell switch
        {
            "bash" => Bash(commands),
            "zsh" => "autoload -U +X bashcompinit && bashcompinit\n" + Bash(commands),
            _ => PowerShell(commands),
        };
    }

    private static IEnumerable<string> OptionNames(Command command) =>
        command.Options.SelectMany(o => new[] { o.Name }.Concat(o.Aliases)).Where(n => n.StartsWith('-'));

    private static string Bash(Dictionary<string, string[]> commands)
    {
        var script = new StringBuilder();
        script.AppendLine("_sweethide() {");
        script.AppendLine("  local cur=\"${COMP_WORDS[COMP_CWORD]}\" sub=\"${COMP_WORDS[1]}\"");
        script.AppendLine("  if [[ $COMP_CWORD -eq 1 ]]; then");
        script.AppendLine(CultureInfo.InvariantCulture, $"    COMPREPLY=($(compgen -W \"{string.Join(' ', commands.Keys)} --help --version\" -- \"$cur\")); return");
        script.AppendLine("  fi");
        script.AppendLine("  case \"$sub\" in");
        foreach (var (name, options) in commands)
        {
            var words = name == "completion" ? "bash zsh powershell" : string.Join(' ', options);
            script.AppendLine(CultureInfo.InvariantCulture, $"    {name}) if [[ $cur == -* || {(name == "completion" ? "1" : "0")} -eq 1 ]]; then COMPREPLY=($(compgen -W \"{words}\" -- \"$cur\")); return; fi ;;");
        }

        script.AppendLine("  esac");
        script.AppendLine("  COMPREPLY=($(compgen -f -- \"$cur\"))");
        script.AppendLine("}");
        script.AppendLine("complete -o filenames -F _sweethide sweethide");
        return script.ToString();
    }

    private static string PowerShell(Dictionary<string, string[]> commands)
    {
        var script = new StringBuilder();
        script.AppendLine("Register-ArgumentCompleter -Native -CommandName sweethide, sweethide.exe -ScriptBlock {");
        script.AppendLine("  param($wordToComplete, $commandAst, $cursorPosition)");
        script.AppendLine("  $words = $commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() }");
        script.AppendLine("  $options = @{");
        foreach (var (name, options) in commands)
        {
            var words = name == "completion" ? new[] { "bash", "zsh", "powershell" } : options;
            script.AppendLine(CultureInfo.InvariantCulture, $"    '{name}' = @({string.Join(", ", words.Select(w => $"'{w}'"))})");
        }

        script.AppendLine("  }");
        script.AppendLine("  if ($words.Count -eq 0 -or ($words.Count -eq 1 -and $wordToComplete)) { $candidates = $options.Keys }");
        script.AppendLine("  elseif ($wordToComplete -like '-*' -or $words[0] -eq 'completion') { $candidates = $options[$words[0]] }");
        script.AppendLine("  else { return }");
        script.AppendLine("  $candidates | Where-Object { $_ -like \"$wordToComplete*\" } | ForEach-Object {");
        script.AppendLine("    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)");
        script.AppendLine("  }");
        script.AppendLine("}");
        return script.ToString();
    }
}
