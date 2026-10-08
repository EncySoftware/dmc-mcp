using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;

namespace DmcMcp;

/// <summary>
/// What `dmc-mcp serve` runs with. Tokens null — the key is the only way in, and TokensOff says why (for the startup
/// line). PublicUrl is the site as clients see it (DMC_MCP_PUBLIC_URL): behind nginx the request itself says
/// http://127.0.0.1.
/// </summary>
public sealed record ServeSettings(string Key, string DataDir, TokenSettings? Tokens, string PublicUrl, string? TokensOff = null)
{
    public string ResourceUrl => PublicUrl + "/mcp";
    public string MetadataUrl => PublicUrl + Door.McpMetadataPath;

    /** Never the key: a record's generated ToString would print it into whatever logs the settings. */
    public override string ToString() => $"serve settings (data {DataDir}, {Tokens?.Describe() ?? "tokens off"}, public {PublicUrl})";
}

/// <summary>
/// `dmc-mcp serve`: the same tools over MCP Streamable HTTP for agents that cannot start a local process — Hermes
/// on the DMC server. At the door (Door) the key — the server's one DMC account, signed in once inside the
/// container (SignInKeepAlive) — or a person's own access token, so the call runs as that person; files by upload
/// or link (FileInputs), each upload its sender's. Configuration: DMC_MCP_KEY (required, 32+ chars), DMC_MCP_DATA
/// (default /data), DMC_MCP_ISSUER / DMC_MCP_CLIENTS / DMC_MCP_AUDIENCE for tokens (TokenSettings),
/// DMC_MCP_PUBLIC_URL, ASPNETCORE_HTTP_PORTS / --urls for the port.
/// </summary>
public static class ServeCommand
{
    internal const string Instructions =
        "Hosted Digital Machine Center server. It cannot read file paths. To pass a file (a post, schema, kit, " +
        "cover or a zipped folder for publish_folder), first upload it to this server: POST <this server's MCP " +
        "URL>/upload as multipart/form-data with the field \"file\" and the same credentials as for MCP (your " +
        "access token or the key as Authorization: Bearer, or the key in the URL path). The answer's \"file\" " +
        "value, upload:<id>, goes into the tool's file argument; an upload is usable by whoever sent it and lives " +
        "24 hours. An https:// link to the file works too.";

    /** An upload's request: the file and the multipart framing around it. */
    private const long UploadBodyLimit = UploadStore.MaxBytes + 1024 * 1024;

    public static async Task<int> Run(string[] args)
    {
        var (settings, error) = Read(Environment.GetEnvironmentVariable);
        if (settings == null)
        {
            Console.Error.WriteLine("ERROR: " + error);
            return 1;
        }
        var app = Build(args, settings);
        if (settings.Tokens != null) app.Lifetime.ApplicationStarted.Register(() => _ = WarmUp(app));
        return await RunUntilStopped(app);
    }

    /** The settings the environment gives, or why the server cannot start with them. No message holds the key. */
    internal static (ServeSettings? Settings, string? Error) Read(Func<string, string?> env)
    {
        var key = env("DMC_MCP_KEY")?.Trim();
        if (key is null || key.Length < 32)
            return (null, "set DMC_MCP_KEY to a random key of at least 32 characters (openssl rand -hex 32).");
        var data = env("DMC_MCP_DATA") is { Length: > 0 } d ? d : "/data";
        var issuer = env("DMC_MCP_ISSUER");
        TokenSettings? tokens;
        try { tokens = TokenSettings.Parse(issuer, env("DMC_MCP_CLIENTS"), env("DMC_MCP_AUDIENCE")); }
        catch (ArgumentException e) { return (null, e.Message); }
        var publicUrl = (env("DMC_MCP_PUBLIC_URL") is { Length: > 0 } p ? p.Trim() : Brand.Site).TrimEnd('/');
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) || !TokenSettings.IsSecure(uri) || uri.AbsolutePath != "/")
            return (null, $"DMC_MCP_PUBLIC_URL must be the site's https address, like {Brand.Site}, not \"{publicUrl}\".");
        return (new ServeSettings(key, data, tokens, publicUrl, tokens == null ? TokenSettings.WhyOff(issuer) : null), null);
    }

    /** The issuer's keys fetched ahead of the first token, and a line saying whether Keycloak answered. */
    private static async Task WarmUp(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DmcMcp.Serve");
        var tokens = app.Services.GetRequiredService<UserTokens>();
        try
        {
            var keys = await tokens.WarmUp(app.Lifetime.ApplicationStopping);
            log.LogInformation("Keys of {Issuer}: {Count} loaded — tokens can be checked", tokens.Settings.Issuer, keys.Count);
        }
        catch (Exception e)
        {
            log.LogWarning("Keys of {Issuer} could not be fetched ({Error}); tokens are answered 503 until they can",
                tokens.Settings.Issuer, e.Message);
        }
    }

    /**
     * 0 for an orderly stop; 1 when a background service crashed — .NET then stops the host, and an exit code of 0
     * would tell a restart policy (on-failure) that nothing went wrong.
     */
    internal static async Task<int> RunUntilStopped(WebApplication app)
    {
        // Taken before the run: RunAsync disposes the services when it returns.
        var background = app.Services.GetServices<IHostedService>().OfType<BackgroundService>().ToList();
        try
        {
            await app.RunAsync();
        }
        catch (Exception e) when (background.Any(s => s.ExecuteTask is { IsFaulted: true }))
        {
            // A service that faults before the host has finished starting can fail the start itself, and RunAsync
            // then throws instead of returning: the same crash, the same exit code.
            Console.Error.WriteLine("ERROR: a background service failed while the server started: " + e.Message);
            return 1;
        }
        return background.Any(s => s.ExecuteTask is { IsFaulted: true }) ? 1 : 0;
    }

    /** The startup line: which ways in are open. Never the key. */
    internal static string Modes(ServeSettings settings) =>
        "Ways in: the key (the server's own DMC account); "
        + (settings.Tokens == null
            ? settings.TokensOff ?? "tokens off"
            : settings.Tokens.Describe() + $" (each call runs as the token's person); resource metadata at {settings.MetadataUrl}");

    /** 0.8.0's shape, for tests that need no tokens: the key alone (as an env file from 0.8.0 gives), the public site. */
    internal static WebApplication Build(string[] args, string key, string dataDir, Action<IServiceCollection>? configure = null) =>
        Build(args, new ServeSettings(key, dataDir, null, Brand.Site, TokenSettings.WhyOff(null)), configure);

    internal static WebApplication Build(string[] args, ServeSettings settings, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The key may travel in the path, a token in a header: keep ASP.NET from logging request lines.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Kestrel's default body limit (30 MB) stays for /mcp — the SDK reads a JSON-RPC body whole into memory —
        // and only the upload endpoint raises it for its own request.
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadBodyLimit);

        var tmp = Path.Combine(settings.DataDir, "tmp");
        UploadJanitor.ClearTemp(tmp); // left by a container stopped mid-call; no call is alive yet
        builder.Services.AddSingleton(new UploadStore(Path.Combine(settings.DataDir, "uploads")));
        builder.Services.AddSingleton(new Downloader());
        // Who a person is in DMC, asked once a minute per token: by the disk gate before a file of theirs is kept, and
        // by the tools' errors that name the account.
        builder.Services.AddSingleton<Who>();
        builder.Services.AddSingleton<DiskGate>();
        builder.Services.AddSingleton(sp => new FileInputs(true, sp.GetRequiredService<UploadStore>(),
            sp.GetRequiredService<Downloader>(), tmp, sp.GetRequiredService<DiskGate>()));
        builder.Services.AddSingleton<IDmcClient, DmcClient>();
        builder.Services.AddSingleton<DmcTokenProvider>();
        builder.Services.AddSingleton<DmcTools>();
        builder.Services.AddHostedService<SignInKeepAlive>();
        builder.Services.AddHostedService(sp => new UploadJanitor(sp.GetRequiredService<UploadStore>(), tmp));
        if (settings.Tokens is { } tokenSettings)
        {
            builder.Services.AddSingleton(new IssuerKeys(tokenSettings.Issuer));
            builder.Services.AddSingleton(sp => new UserTokens(tokenSettings, sp.GetRequiredService<IssuerKeys>()));
        }
        configure?.Invoke(builder.Services); // tests replace DMC, the sign-in, the store or the issuer's keys
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = Instructions)
            // Stateless: nothing to lose on a restart, an update or two idle hours — a session would end with each, and
            // a client that does not re-initialize on 404 stays broken. The tools ask the client nothing (no sampling,
            // elicitation or roots); progress goes on the call's own response stream. Each request runs in its own
            // execution context, which is what carries the caller the door let in.
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<DmcTools>();

        var app = builder.Build();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DmcMcp.Serve");
        var door = new Door(settings.Key, settings.Tokens == null ? null : app.Services.GetRequiredService<UserTokens>(),
            settings.MetadataUrl, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Door>());
        app.Use((ctx, next) => door.Handle(ctx, next));
        // Routing after the door: the endpoint is matched on the path with the key already stripped.
        app.UseRouting();
        if (settings.Tokens != null)
        {
            // RFC 9728, as the MCP authorization spec asks: where to get a token for this server. The SDK's own
            // McpAuthenticationHandler is not used — it derives these addresses from the request (http behind nginx).
            var metadata = new ProtectedResourceMetadata
            {
                Resource = settings.ResourceUrl,
                AuthorizationServers = [settings.Tokens.Issuer],
                BearerMethodsSupported = ["header"],
                ScopesSupported = ["openid"],
            };
            var typeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ProtectedResourceMetadata));
            app.MapGet(Door.McpMetadataPath, () => Results.Json(metadata, typeInfo));
            app.MapGet(Door.MetadataPath, () => Results.Json(metadata, typeInfo));
        }
        app.MapPost("/mcp/upload", async (HttpRequest req, UploadStore store, DiskGate gate, CancellationToken ct) =>
        {
            // Whoever the door let in: the key's account, or the person whose token sent it — it owns the upload.
            var caller = Caller.Current ?? Caller.Server;
            // All before the body is read: ReadFormAsync buffers the file on the container's disk, then Save copies it.
            // A person must be someone DMC lets publish — a licsys account alone buys no room here.
            if (await gate.Check(caller) is { } refused)
            {
                if (refused.Status == StatusCodes.Status503ServiceUnavailable) req.HttpContext.Response.Headers.RetryAfter = "60";
                return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
            }
            using var slot = store.TryBegin(caller.Owner, out var busy);
            if (slot == null) return Results.Json(new { error = busy }, statusCode: StatusCodes.Status429TooManyRequests);
            if (store.NoRoomFor(caller.Owner) is { } full)
                return Results.Json(new { error = full }, statusCode: StatusCodes.Status507InsufficientStorage);
            if (req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = UploadBodyLimit;
            var noFile = Results.BadRequest(new { error = "send the file as multipart/form-data, field \"file\"" });
            if (!req.HasFormContentType) return noFile;
            IFormCollection form;
            try { form = await req.ReadFormAsync(ct); }
            catch (InvalidDataException) { return noFile; } // an empty or malformed multipart body
            var file = form.Files["file"];
            if (file == null) return noFile;
            try
            {
                await using var stream = file.OpenReadStream();
                var saved = await store.Save(stream, file.FileName, caller.Owner, ct);
                return Results.Ok(new { file = saved.Ref, name = saved.Name, size = saved.Size, expiresAt = saved.ExpiresAt });
            }
            catch (InvalidDataException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (UploadStore.FullException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status507InsufficientStorage);
            }
        });
        app.MapMcp("/mcp");
        app.Lifetime.ApplicationStarted.Register(() => log.LogInformation("{Modes}", Modes(settings)));
        return app;
    }
}
