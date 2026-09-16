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

    /** Что лежало в отправленном zip — инструмент удаляет его сразу после отправки, поэтому смотрим здесь. */
    public List<string> ZipEntries { get; } = new();

    public Task<string> StartImport(string filePath, string importId, bool ai, string? nameHint,
        string? descriptionHint, string accessToken)
    {
        if (FailStart != null) throw FailStart;
        Starts.Add((filePath, importId, ai, nameHint, descriptionHint));
        if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
            using (var zip = System.IO.Compression.ZipFile.OpenRead(filePath))
                foreach (var e in zip.Entries) ZipEntries.Add(e.FullName);
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

    /** Что «опубликовано» в каталоге и что «своё» — для поиска дублей; что было загружено — для замены архива. */
    public List<ProductInfo> Published { get; } = new();
    public List<ProductInfo> Mine { get; } = new();
    public List<(string? Query, string? Controller, string? Maker)> Searches { get; } = new();
    public List<string> Uploads { get; } = new();

    public Task<IReadOnlyList<ProductInfo>> SearchPublished(string? query, string? controllerManufacturer,
        string? machineManufacturer, string? accessToken)
    {
        Searches.Add((query, controllerManufacturer, machineManufacturer));
        return Task.FromResult<IReadOnlyList<ProductInfo>>(Published);
    }

    public Task<IReadOnlyList<ProductInfo>> MyProducts(string accessToken) =>
        Task.FromResult<IReadOnlyList<ProductInfo>>(Mine);

    public Task<string> UploadFile(string filePath, string accessToken)
    {
        Uploads.Add(Path.GetFileName(filePath));
        return Task.FromResult($"tmp/u{Uploads.Count}/{Path.GetFileName(filePath)}");
    }

    // ---- связи, удаление, ИИ-помощники

    public List<ProductInfo> Schemas { get; } = new();
    public Dictionary<string, List<LinkInfo>> Links { get; } = new();
    public List<(string Id, string LinkType, IReadOnlyList<string> Targets)> AddedLinks { get; } = new();
    public List<string> Deleted { get; } = new();
    public DmcHttpException? FailDelete { get; set; }
    public string DescriptionText { get; set; } = "Generated description";
    /** null — «в архиве нет картинки», как 404 от бэкенда. */
    public string? ArchiveImage { get; set; } = "tmp/u9/preview.png";
    public List<string> AiCalls { get; } = new();

    public Task<IReadOnlyList<ProductInfo>> Search(string? contentType, string? query, string? controllerManufacturer,
        string? machineManufacturer, string? accessToken)
    {
        Searches.Add((query, controllerManufacturer, machineManufacturer));
        return Task.FromResult<IReadOnlyList<ProductInfo>>(contentType == "MACHINE_SCHEMA" ? Schemas : Published);
    }

    public Task<IReadOnlyList<LinkInfo>> GetLinks(string id, string? accessToken) =>
        Task.FromResult<IReadOnlyList<LinkInfo>>(Links.GetValueOrDefault(id) ?? new List<LinkInfo>());

    public Task AddLinks(string id, string linkType, IReadOnlyList<string> targetIds, string accessToken)
    {
        AddedLinks.Add((id, linkType, targetIds));
        if (!Links.TryGetValue(id, out var list)) Links[id] = list = new List<LinkInfo>();
        foreach (var tid in targetIds)
            if (!list.Any(l => l.Product?.Id == tid))
                list.Add(new LinkInfo("l" + (list.Count + 1), linkType, "OUT",
                    Products.GetValueOrDefault(tid) ?? Schemas.FirstOrDefault(s => s.Id == tid)));
        return Task.CompletedTask;
    }

    public Task DeleteProduct(string id, string accessToken)
    {
        if (FailDelete != null) throw FailDelete;
        Deleted.Add(id);
        Products.Remove(id);
        return Task.CompletedTask;
    }

    public Task<string> GenerateDescription(IDictionary<string, object?> productData, string accessToken)
    { AiCalls.Add("description"); return Task.FromResult(DescriptionText); }
    public Task<string> GenerateImage(IDictionary<string, object?> productData, string accessToken)
    { AiCalls.Add("image"); return Task.FromResult("tmp/u9/ai-cover.png"); }
    public Task<string?> ArchivePreview(string productFile, string accessToken)
    { AiCalls.Add("archive-preview"); return Task.FromResult(ArchiveImage); }
    public Task<string> GenerateSampleCode(IDictionary<string, object?> productData, string accessToken)
    { AiCalls.Add("sample-code"); return Task.FromResult("tmp/u9/sample.nc"); }
    public Task<string> GenerateCodesList(IDictionary<string, object?> productData, string accessToken)
    { AiCalls.Add("codes-list"); return Task.FromResult("tmp/u9/codes.txt"); }

    /** Карточка с полным Raw — из него update_post собирает PUT. По умолчанию пост; contentType — для схем. */
    public static ProductInfo Post(string id, string name, string status = "DRAFT", string? slug = null,
        string? controller = "Fanuc", string? machineMaker = "Haas", string? machineType = "MILLING",
        string contentType = "POST_PROCESSOR")
    {
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = id, ["slug"] = slug, ["name"] = name, ["contentType"] = contentType,
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
