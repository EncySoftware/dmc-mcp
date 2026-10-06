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

    internal static WebApplication Build(string[] args, string key, string dataDir)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The key may travel in the path: keep ASP.NET from logging request lines.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadStore.MaxBytes + 1024 * 1024);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadStore.MaxBytes + 1024 * 1024);

        var uploads = new UploadStore(Path.Combine(dataDir, "uploads"));
        builder.Services.AddSingleton(uploads);
        builder.Services.AddSingleton(new FileInputs(true, uploads, new Downloader(), Path.Combine(dataDir, "tmp")));
        builder.Services.AddSingleton<IDmcClient, DmcClient>();
        builder.Services.AddSingleton<DmcTokenProvider>();
        builder.Services.AddSingleton<DmcTools>();
        builder.Services.AddHostedService<SignInKeepAlive>();
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = Instructions)
            .WithHttpTransport()
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
        });
        app.MapMcp("/mcp");
        return app;
    }
}
