using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DmcMcp;

/// <summary>
/// `dmc-mcp serve`: the same tools over MCP Streamable HTTP for agents that cannot start a local process — Hermes
/// on the DMC server. Key at the door (KeyAuth), files by upload or link (FileInputs), one DMC account signed in
/// once inside the container (SignInKeepAlive). Configuration: DMC_MCP_KEY (required, 32+ chars), DMC_MCP_DATA
/// (default /data), ASPNETCORE_HTTP_PORTS / --urls for the port.
/// </summary>
public static class ServeCommand
{
    internal const string Instructions =
        "Hosted Digital Machine Center server. It cannot read file paths. To pass a file (a post, schema, kit, " +
        "cover or a zipped folder for publish_folder), first upload it to this server: POST <this server's MCP " +
        "URL>/upload as multipart/form-data with the field \"file\" and the same key (Authorization: Bearer <key>, " +
        "or the key in the URL path as for MCP). The answer's \"file\" value, upload:<id>, goes into the tool's " +
        "file argument; uploads live 24 hours. An https:// link to the file works too.";

    /** An upload's request: the file and the multipart framing around it. */
    private const long UploadBodyLimit = UploadStore.MaxBytes + 1024 * 1024;

    public static async Task<int> Run(string[] args)
    {
        var key = Environment.GetEnvironmentVariable("DMC_MCP_KEY")?.Trim();
        if (key is null || key.Length < 32)
        {
            Console.Error.WriteLine("ERROR: set DMC_MCP_KEY to a random key of at least 32 characters (openssl rand -hex 32).");
            return 1;
        }
        var data = Environment.GetEnvironmentVariable("DMC_MCP_DATA") is { Length: > 0 } d ? d : "/data";
        await Build(args, key, data).RunAsync();
        return 0;
    }

    internal static WebApplication Build(string[] args, string key, string dataDir, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The key may travel in the path: keep ASP.NET from logging request lines.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Kestrel's default body limit (30 MB) stays for /mcp — the SDK reads a JSON-RPC body whole into memory —
        // and only the upload endpoint raises it for its own request.
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadBodyLimit);

        var uploads = new UploadStore(Path.Combine(dataDir, "uploads"));
        var tmp = Path.Combine(dataDir, "tmp");
        UploadJanitor.ClearTemp(tmp); // left by a container stopped mid-call; no call is alive yet
        builder.Services.AddSingleton(uploads);
        builder.Services.AddSingleton(new FileInputs(true, uploads, new Downloader(), tmp));
        builder.Services.AddSingleton<IDmcClient, DmcClient>();
        builder.Services.AddSingleton<DmcTokenProvider>();
        builder.Services.AddSingleton<DmcTools>();
        builder.Services.AddHostedService<SignInKeepAlive>();
        builder.Services.AddHostedService(sp => new UploadJanitor(sp.GetRequiredService<UploadStore>(), tmp));
        configure?.Invoke(builder.Services); // tests replace DMC, the sign-in or the store
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = Instructions)
            // Stateless: nothing to lose on a restart, an update or two idle hours — a session would end with each, and
            // a client that does not re-initialize on 404 stays broken. The tools ask the client nothing (no sampling,
            // elicitation or roots); progress goes on the call's own response stream.
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<DmcTools>();

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var (ok, path) = KeyAuth.Check(ctx.Request.Path.Value ?? "", ctx.Request.Headers.Authorization.ToString(), key);
            if (!ok)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            ctx.Request.Path = path;
            await next();
        });
        // Routing after the door: the endpoint is matched on the path with the key already stripped.
        app.UseRouting();
        app.MapPost("/mcp/upload", async (HttpRequest req, UploadStore store, CancellationToken ct) =>
        {
            // Before the body is read: ReadFormAsync buffers the file on the container's disk, then Save copies it.
            using var slot = store.TryBegin();
            if (slot == null)
                return Results.Json(new { error = $"{UploadStore.MaxConcurrent} uploads are already running — send this one when they finish" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            if (store.UsedBytes() >= store.MaxTotalBytes)
                return Results.Json(new { error = store.Full().Message }, statusCode: StatusCodes.Status507InsufficientStorage);
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
                var saved = await store.Save(stream, file.FileName, ct);
                return Results.Ok(new { file = saved.Ref, name = saved.Name, size = saved.Size, expiresAt = saved.ExpiresAt });
            }
            catch (InvalidDataException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (UploadStore.FullException e)
            {
                return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status507InsufficientStorage);
            }
        });
        app.MapMcp("/mcp");
        return app;
    }
}
