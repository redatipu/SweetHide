using SweetHide.Server;

if (args is [HealthProbe.Argument, ..])
{
    return await HealthProbe.RunAsync(args.Length > 1 ? args[1] : null);
}

await using var app = ServerApp.Build(args);
await app.RunAsync();
return 0;
