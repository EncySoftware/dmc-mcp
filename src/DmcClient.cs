using System.Net;
using System.Text;
using System.Text.Json;

namespace DmcMcp;

/** Ответ DMC не 2xx. Код нужен инструментам, чтобы отличить «войдите» (401) от «нет роли» (403) и «занято» (409). */
public class DmcHttpException(int status, string body) : Exception($"{status} {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}

public record ImportComponent(string Name, string ContentType, string ProductId, bool AiEnriched);

/** Состояние фонового импорта, как его отдаёт GET /products/bulk-zip/{id}/progress. */
public record ImportProgress(string Status, string? StatusReason, string? CurrentName, int Done, int Total,
    IReadOnlyList<ImportComponent> Components, IReadOnlyList<string> Errors)
{
    public bool Finished => Status is "done" or "cancelled" or "error";
}

/** Карточка: поля, которые инструменты показывают и правят. Raw — весь JSON, из него собирается полный PUT. */
public record ProductInfo(string Id, string? Slug, string Name, string ContentType, string PublicationStatus,
    string? Description, string? ControllerManufacturer, string? ControllerSeries, string? ControllerModel,
    string? MachineManufacturer, string? MachineSeries, string? MachineModel, string? MachineType,
    int? NumberOfAxes, bool HasCover, JsonElement Raw)
{
    public string Url(string site) => $"{site}/product/{Uri.EscapeDataString(Slug ?? Id)}";
    public string Controller => Join(ControllerManufacturer, ControllerSeries, ControllerModel);
    public string Machine => Join(MachineManufacturer, MachineSeries, MachineModel);

    private static string Join(params string?[] parts)
    {
        var s = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length == 0 ? "—" : s;
    }
}

public interface IDmcClient
{
    string Site { get; }
    /** null — нет такого или чужой черновик: бэкенд отвечает 404 в обоих случаях. */
    Task<ProductInfo?> GetProduct(string idOrSlug, string? accessToken);
    /** Запускает фоновый импорт; возвращает importId, который назвал сервер (обычно тот же). */
    Task<string> StartImport(string filePath, string importId, bool ai, string? nameHint, string? descriptionHint, string accessToken);
    Task<ImportProgress> GetImportProgress(string importId, string accessToken);
    /** Полный PUT: body — все поля ProductCreateRequest, частичного у DMC нет. */
    Task<ProductInfo> UpdateProduct(string id, IDictionary<string, object?> body, string accessToken);
    Task<ProductInfo> SetStatus(string id, string status, string accessToken);
}

public static class DmcJson
{
    public static ProductInfo Product(JsonElement r) => new(
        Str(r, "id") ?? "", Str(r, "slug"), Str(r, "name") ?? "", Str(r, "contentType") ?? "",
        Str(r, "publicationStatus") ?? "DRAFT", Str(r, "description"),
        Str(r, "controllerManufacturer"), Str(r, "controllerSeries"), Str(r, "controllerModel"),
        Str(r, "machineManufacturer"), Str(r, "machineSeries"), Str(r, "machineModel"), Str(r, "machineType"),
        Int(r, "numberOfAxes"), !string.IsNullOrEmpty(Str(r, "imageUrl")), r.Clone());

    public static ImportProgress Progress(JsonElement r)
    {
        var comps = new List<ImportComponent>();
        var errs = new List<string>();
        if (r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
        {
            if (res.TryGetProperty("components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    comps.Add(new ImportComponent(Str(c, "name") ?? "", Str(c, "contentType") ?? "", Str(c, "productId") ?? "",
                        c.TryGetProperty("aiEnriched", out var a) && a.ValueKind == JsonValueKind.True));
            if (res.TryGetProperty("errors", out var es) && es.ValueKind == JsonValueKind.Array)
                foreach (var e in es.EnumerateArray())
                    errs.Add($"{Str(e, "component")}: {Str(e, "message")}");
        }
        return new ImportProgress(Str(r, "status") ?? "running", Str(r, "statusReason"), Str(r, "currentName"),
            Int(r, "done") ?? 0, Int(r, "total") ?? 0, comps, errs);
    }

    /** Поля ProductCreateRequest — ровно те, что бэкенд принимает в PUT. Остальное из карточки не берётся. */
    public static readonly string[] RequestFields =
    {
        "name", "contentType", "category", "description", "kitContents", "equipment", "minSoftwareVersion",
        "machineManufacturer", "machineSeries", "machineModel", "machineType", "numberOfAxes",
        "travelXMm", "travelYMm", "travelZMm", "controllerManufacturer", "controllerSeries", "controllerModel",
        "units", "unitsList", "priceEur", "productOwner", "authorName", "trialDays",
        "supportedCodes", "sampleOutputCode", "supportedCodesFile", "sampleOutputCodeFile", "productFile",
        "imageUrl", "images", "visibility", "publicationStatus", "experienceStatus", "encyTestStatus",
    };

    public static Dictionary<string, object?> RequestFrom(JsonElement product)
    {
        var d = new Dictionary<string, object?>();
        foreach (var f in RequestFields)
            if (product.TryGetProperty(f, out var v) && v.ValueKind != JsonValueKind.Null) d[f] = v.Clone();
        return d;
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

public class DmcClient : IDmcClient
{
    // Архив поста может весить десятки мегабайт, а ответ на загрузку приходит после сохранения — не 15 с.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _api = (Environment.GetEnvironmentVariable("DMC_API") ?? Brand.Api).TrimEnd('/');
    public string Site { get; } = (Environment.GetEnvironmentVariable("DMC_SITE") ?? Brand.Site).TrimEnd('/');

    public async Task<ProductInfo?> GetProduct(string idOrSlug, string? accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_api}/products/{Uri.EscapeDataString(idOrSlug)}");
        Auth(req, accessToken);
        var resp = await Http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        return DmcJson.Product(JsonDocument.Parse(await Read(resp)).RootElement);
    }

    public async Task<string> StartImport(string filePath, string importId, bool ai, string? nameHint,
        string? descriptionHint, string accessToken)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(filePath));
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(bytes, "file", Path.GetFileName(filePath));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/products/bulk-zip-async") { Content = form };
        Auth(req, accessToken);
        req.Headers.Add("X-Import-Id", importId);
        var flag = ai ? "true" : "false";
        req.Headers.Add("X-AI-Description", flag);
        req.Headers.Add("X-AI-Image", flag);
        req.Headers.Add("X-AI-Metadata", flag);
        // Заголовки HTTP — ISO-8859-1: подсказки уходят URL-encoded, как у веб-клиента; бэкенд декодирует UTF-8.
        if (!string.IsNullOrWhiteSpace(nameHint)) req.Headers.Add("X-AI-Name-Hint", Uri.EscapeDataString(nameHint));
        if (!string.IsNullOrWhiteSpace(descriptionHint)) req.Headers.Add("X-AI-Description-Hint", Uri.EscapeDataString(descriptionHint));
        var r = JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement;
        return r.TryGetProperty("importId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : importId;
    }

    public async Task<ImportProgress> GetImportProgress(string importId, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_api}/products/bulk-zip/{Uri.EscapeDataString(importId)}/progress");
        Auth(req, accessToken);
        return DmcJson.Progress(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    public async Task<ProductInfo> UpdateProduct(string id, IDictionary<string, object?> body, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_api}/products/{Uri.EscapeDataString(id)}")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        Auth(req, accessToken);
        return DmcJson.Product(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    public async Task<ProductInfo> SetStatus(string id, string status, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{_api}/products/{Uri.EscapeDataString(id)}/status?status={Uri.EscapeDataString(status)}");
        Auth(req, accessToken);
        return DmcJson.Product(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    private static void Auth(HttpRequestMessage req, string? token)
    {
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> Read(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new DmcHttpException((int)resp.StatusCode, body);
        return body;
    }
}
