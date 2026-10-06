using DmcMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// `dmc-mcp login` — a one-time sign-in to DMC through the browser (MCP is not involved).
// `--password` — the console fallback for a machine without a browser.
if (args.Length > 0 && args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
{
    var provider = new DmcTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--console", StringComparer.OrdinalIgnoreCase);
    return console ? await provider.LoginInteractive() : await provider.LoginBrowser();
}

// `dmc-mcp setup [--no-login]` — register the server in the editor and sign in.
if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    var tokenProvider = new DmcTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase);
    return await SetupCommand.Run(SetupCommand.DefaultCursorConfigPath, CodexConfig.DefaultHome, new ProcessRunner(),
        () => File.Exists(DmcTokenProvider.AuthFilePath),
        console ? tokenProvider.LoginInteractive : tokenProvider.LoginBrowser,
        args.Contains("--no-login", StringComparer.OrdinalIgnoreCase), Console.WriteLine);
}

// `dmc-mcp doctor` — what is wrong: sign-in, DMC and role, editor registration. The answer to "it doesn't work".
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    return await Doctor.Run(new DmcClient(), new DmcTokenProvider(), new ProcessRunner(),
        SetupCommand.DefaultCursorConfigPath, CodexConfig.DefaultHome, Console.WriteLine);
}

// `dmc-mcp serve` — the hosted server over HTTP (Hermes on the DMC server); see ServeCommand.
if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
    return await ServeCommand.Run(args[1..]);

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol; logs go to stderr only.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The newer-version hint goes to stderr without waiting for nuget.org: the server starts at once.
_ = VersionCheck.Hint().ContinueWith(t => { if (t.Result is { } h) Console.Error.WriteLine(h); },
    TaskContinuationOptions.OnlyOnRanToCompletion);

builder.Services.AddSingleton<IDmcClient, DmcClient>();
builder.Services.AddSingleton<DmcTokenProvider>();
builder.Services.AddSingleton<DmcTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DmcTools>();

await builder.Build().RunAsync();
return 0;
