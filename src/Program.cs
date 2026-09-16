using DmcMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// `dmc-mcp login` — разовый вход в DMC через браузер (MCP не участвует).
// `--password` — консольный запасной путь для машины без браузера.
if (args.Length > 0 && args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
{
    var provider = new DmcTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--console", StringComparer.OrdinalIgnoreCase);
    return console ? await provider.LoginInteractive() : await provider.LoginBrowser();
}

// `dmc-mcp setup [--no-login]` — прописать сервер в редакторе и войти.
if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    var tokenProvider = new DmcTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase);
    return await SetupCommand.Run(SetupCommand.DefaultCursorConfigPath, new ProcessRunner(),
        () => File.Exists(DmcTokenProvider.AuthFilePath),
        console ? tokenProvider.LoginInteractive : tokenProvider.LoginBrowser,
        args.Contains("--no-login", StringComparer.OrdinalIgnoreCase), Console.WriteLine);
}

// `dmc-mcp doctor` — что не так: вход, DMC и роль, регистрация в редакторах. Ответ на «не работает».
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    return await Doctor.Run(new DmcClient(), new DmcTokenProvider(), new ProcessRunner(),
        SetupCommand.DefaultCursorConfigPath, Console.WriteLine);
}

var builder = Host.CreateApplicationBuilder(args);

// stdout — это протокол MCP, логи только в stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Подсказка о новой версии — в stderr, не дожидаясь ответа nuget.org: сервер стартует сразу.
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
