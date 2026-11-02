using System.CommandLine;
using System.Text;

using SweetHide.Cli;

try
{
    // Thai text and file names must survive the Windows console.
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached; nothing to configure.
}

var context = new CliContext
{
    Out = Console.Out,
    Error = Console.Error,
    Passwords = new ConsolePasswordProvider(),
};

return CliApp.Create(context).Parse(args).Invoke(new InvocationConfiguration { Output = context.Out, Error = context.Error });
