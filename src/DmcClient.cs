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
    /** Опубликованные посты по запросу и фильтрам — для поиска дублей. Работает и без входа. */
    Task<IReadOnlyList<ProductInfo>> SearchPublished(string? query, string? controllerManufacturer,
        string? machineManufacturer, string? accessToken);
    /** Все свои компоненты любого статуса (GET /products/my). */
    Task<IReadOnlyList<ProductInfo>> MyProducts(string accessToken);
    /** Кладёт файл во временное хранилище; возвращает путь tmp/<uploadId>/<file>, который принимает PUT. */
    Task<string> UploadFile(string filePath, string accessToken);

    /** Опубликованные компоненты заданного типа по запросу и фильтрам (POST /products/search). */
    Task<IReadOnlyList<ProductInfo>> Search(string contentType, string? query, string? controllerManufacturer,
        string? machineManufacturer, string? accessToken);
    Task<IReadOnlyList<LinkInfo>> GetLinks(string id, string? accessToken);
    /** POST /products/{id}/links: linkType — MADE_FOR (пост → схемы), SUITABLE, KIT_CONTAINS. */
    Task AddLinks(string id, string linkType, IReadOnlyList<string> targetIds, string accessToken);
    /** DELETE /products/{id}; 409 при активных лицензиях. */
    Task DeleteProduct(string id, string accessToken);

    // ИИ-помощники формы. Сами ничего не сохраняют: текст или путь tmp/… затем уходит в PUT.
    Task<string> GenerateDescription(IDictionary<string, object?> productData, string accessToken);
    Task<string> GenerateImage(IDictionary<string, object?> productData, string accessToken);
    /** null — в архиве нет картинки (бэкенд отвечает 404). */
    Task<string?> ArchivePreview(string productFile, string accessToken);
    Task<string> GenerateSampleCode(IDictionary<string, object?> productData, string accessToken);
    Task<string> GenerateCodesList(IDictionary<string, object?> productData, string accessToken);
}

/** Связь с другой карточкой, как её отдаёт GET /products/{id}/links. */
public record LinkInfo(string Id, string LinkType, string Direction, ProductInfo? Product);

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

    /** Список карточек: голый массив или страница Spring ({"content":[…]}). */
    public static List<ProductInfo> Products(JsonElement r)
    {
        var arr = r;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("content", out var c)) arr = c;
        var list = new List<ProductInfo>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray()) list.Add(Product(e));
        return list;
    }

    public static List<LinkInfo> Links(JsonElement r)
    {
        var list = new List<LinkInfo>();
        if (r.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in r.EnumerateArray())
            list.Add(new LinkInfo(Str(e, "id") ?? "", Str(e, "linkType") ?? "", Str(e, "direction") ?? "",
                e.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? Product(p) : null));
        return list;
    }

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

    public async Task<IReadOnlyList<ProductInfo>> SearchPublished(string? query, string? controllerManufacturer,
        string? machineManufacturer, string? accessToken)
    {
        // Форма ProductSearchRequest: списки для фильтров, page/size для страницы. Только посты.
        var body = new Dictionary<string, object?>
        {
            ["contentTypes"] = new[] { "POST_PROCESSOR" },
            ["page"] = 0,
            ["size"] = 20,
        };
        if (!string.IsNullOrWhiteSpace(query)) body["query"] = query.Trim();
        if (!string.IsNullOrWhiteSpace(controllerManufacturer)) body["controllerManufacturers"] = new[] { controllerManufacturer.Trim() };
        if (!string.IsNullOrWhiteSpace(machineManufacturer)) body["machineManufacturers"] = new[] { machineManufacturer.Trim() };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/products/search")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        Auth(req, accessToken);
        return DmcJson.Products(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    public async Task<IReadOnlyList<ProductInfo>> MyProducts(string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_api}/products/my");
        Auth(req, accessToken);
        return DmcJson.Products(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    public async Task<string> UploadFile(string filePath, string accessToken)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(filePath));
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(bytes, "file", Path.GetFileName(filePath));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/storage/upload") { Content = form };
        Auth(req, accessToken);
        var r = JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement;
        return r.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!
            : throw new InvalidOperationException("хранилище не вернуло путь файла");
    }

    public async Task<IReadOnlyList<ProductInfo>> Search(string contentType, string? query,
        string? controllerManufacturer, string? machineManufacturer, string? accessToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["contentTypes"] = new[] { contentType },
            ["page"] = 0,
            ["size"] = 20,
        };
        if (!string.IsNullOrWhiteSpace(query)) body["query"] = query.Trim();
        if (!string.IsNullOrWhiteSpace(controllerManufacturer)) body["controllerManufacturers"] = new[] { controllerManufacturer.Trim() };
        if (!string.IsNullOrWhiteSpace(machineManufacturer)) body["machineManufacturers"] = new[] { machineManufacturer.Trim() };
        return DmcJson.Products(await PostJson($"{_api}/products/search", body, accessToken));
    }

    public async Task<IReadOnlyList<LinkInfo>> GetLinks(string id, string? accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_api}/products/{Uri.EscapeDataString(id)}/links");
        Auth(req, accessToken);
        return DmcJson.Links(JsonDocument.Parse(await Read(await Http.SendAsync(req))).RootElement);
    }

    public async Task AddLinks(string id, string linkType, IReadOnlyList<string> targetIds, string accessToken)
    {
        await PostJson($"{_api}/products/{Uri.EscapeDataString(id)}/links",
            new Dictionary<string, object?> { ["linkType"] = linkType, ["targetIds"] = targetIds }, accessToken);
    }

    public async Task DeleteProduct(string id, string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_api}/products/{Uri.EscapeDataString(id)}");
        Auth(req, accessToken);
        await Read(await Http.SendAsync(req));
    }

    public async Task<string> GenerateDescription(IDictionary<string, object?> productData, string accessToken) =>
        Field(await PostJson($"{_api}/products/generate-description", productData, accessToken), "description");

    public async Task<string> GenerateImage(IDictionary<string, object?> productData, string accessToken) =>
        Field(await PostJson($"{_api}/products/generate-image", productData, accessToken), "imageUrl");

    public async Task<string?> ArchivePreview(string productFile, string accessToken)
    {
        try
        {
            return Field(await PostJson($"{_api}/products/archive-preview",
                new Dictionary<string, object?> { ["productFile"] = productFile }, accessToken), "imageUrl");
        }
        catch (DmcHttpException e) when (e.Status == 404) { return null; }
    }

    public async Task<string> GenerateSampleCode(IDictionary<string, object?> productData, string accessToken) =>
        Field(await PostJson($"{_api}/products/generate-sample-code", productData, accessToken), "filename");

    public async Task<string> GenerateCodesList(IDictionary<string, object?> productData, string accessToken) =>
        Field(await PostJson($"{_api}/products/generate-codes-list", productData, accessToken), "filename");

    private static string Field(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : throw new InvalidOperationException($"DMC не вернул поле {name}");

    private static async Task<JsonElement> PostJson(string url, object body, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        Auth(req, token);
        var text = await Read(await Http.SendAsync(req));
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
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
