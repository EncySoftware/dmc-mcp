using System.Text.Json;
using DmcMcp;

/** DMC без сети: карточки лежат в словаре, прогресс импорта — очередь ответов, последний повторяется. */
public class FakeDmcClient : IDmcClient
{
    public string Site => "https://dmc.test";
    public Dictionary<string, ProductInfo> Products { get; } = new();
    public Queue<ImportProgress> Progress { get; } = new();
    public DmcHttpException? FailStart { get; set; }
    public DmcHttpException? FailStatus { get; set; }
    public List<(string File, string ImportId, bool Ai, string? Name, string? Desc)> Starts { get; } = new();
    public List<(string Id, IDictionary<string, object?> Body)> Puts { get; } = new();
    public List<(string Id, string Status)> StatusCalls { get; } = new();

    public Task<ProductInfo?> GetProduct(string idOrSlug, string? accessToken) =>
        Task.FromResult(Products.GetValueOrDefault(idOrSlug));

    public Task<string> StartImport(string filePath, string importId, bool ai, string? nameHint,
        string? descriptionHint, string accessToken)
    {
        if (FailStart != null) throw FailStart;
        Starts.Add((filePath, importId, ai, nameHint, descriptionHint));
        return Task.FromResult(importId);
    }

    public Task<ImportProgress> GetImportProgress(string importId, string accessToken) =>
        Task.FromResult(Progress.Count > 1 ? Progress.Dequeue() : Progress.Peek());

    public Task<ProductInfo> UpdateProduct(string id, IDictionary<string, object?> body, string accessToken)
    {
        Puts.Add((id, body));
        var p = Products[id];
        if (body.TryGetValue("name", out var n) && n is string s) p = p with { Name = s };
        Products[id] = p;
        return Task.FromResult(p);
    }

    public Task<ProductInfo> SetStatus(string id, string status, string accessToken)
    {
        if (FailStatus != null) throw FailStatus;
        StatusCalls.Add((id, status));
        var p = Products[id] with { PublicationStatus = status };
        Products[id] = p;
        return Task.FromResult(p);
    }

    /** Карточка поста с полным Raw — из него update_post собирает PUT. */
    public static ProductInfo Post(string id, string name, string status = "DRAFT", string? slug = null,
        string? controller = "Fanuc", string? machineMaker = "Haas", string? machineType = "MILLING")
    {
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = id, ["slug"] = slug, ["name"] = name, ["contentType"] = "POST_PROCESSOR",
            ["category"] = "CNC_MACHINES", ["publicationStatus"] = status,
            ["productFile"] = $"products/{id}/post.zip", ["hasProductFile"] = true, ["downloadCount"] = 7,
            ["controllerManufacturer"] = controller, ["machineManufacturer"] = machineMaker,
            ["machineType"] = machineType, ["numberOfAxes"] = 3,
        });
        return DmcJson.Product(raw);
    }

    public static ImportProgress Running(string? current = "post.sppx") =>
        new("running", null, current, 0, 1, Array.Empty<ImportComponent>(), Array.Empty<string>());
    public static ImportProgress Done(params ImportComponent[] comps) =>
        new("done", null, "", comps.Length, comps.Length, comps, Array.Empty<string>());
    public static ImportProgress DoneWithErrors(string error) =>
        new("done", null, "", 1, 1, Array.Empty<ImportComponent>(), new[] { error });
    public static ImportProgress Failed(string reason) =>
        new("error", reason, "", 0, 1, Array.Empty<ImportComponent>(), Array.Empty<string>());
}

/** Вход, которого в тестах нет по-настоящему. */
public class FakeTokens(string? token = "tok") : DmcTokenProvider
{
    public override Task<string?> GetAccessToken() => Task.FromResult(token);
}
