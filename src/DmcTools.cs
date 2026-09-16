using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DmcMcp;

/// <summary>
/// Инструменты публикации поста в DMC из редактора. Публикация идёт через bulk-zip бэкенда: он сам
/// разбирает архив и дописывает поля ИИ, поэтому агент не заполняет карточку руками. Все ответы —
/// текст для человека; ошибка начинается с «ОШИБКА:», исключения наружу не выходят.
/// </summary>
[McpServerToolType]
public class DmcTools(IDmcClient dmc, DmcTokenProvider tokens)
{
    /** Подменяются в тестах: настоящий сон в опросе там ни к чему. */
    internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;
    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    /** Значения MachineType бэкенда (model/MachineType.java, 2026-09-16). */
    internal static readonly string[] MachineTypes =
    {
        "MILLING", "TURNING", "MILL_TURN", "WIRE_EDM", "LASER", "PLASMA", "WATERJET", "GRINDING",
        "ROBOT", "EDM", "ROUTER", "SWISS", "GAS_PLASMA_LASER", "ADDITIVE", "OTHER",
    };

    internal const string NoLogin =
        "ОШИБКА: входа в DMC на этой машине нет — выполните `" + Brand.Cli + " login` один раз в терминале.";

    // ------------------------------------------------------------- check_post_status

    [McpServerTool(Name = "check_post_status"), Description(
        "Что DMC знает о посте: статус (черновик / на модерации / опубликован), ссылка на карточку, " +
        "стойка и станок. Принимает id или slug.")]
    public async Task<string> CheckPostStatus(
        [Description("id или slug поста")] string idOrSlug)
    {
        var (token, _) = await Token(); // без входа тоже работает: опубликованное видно всем
        ProductInfo? p;
        try { p = await dmc.GetProduct(idOrSlug, token); }
        catch (DmcHttpException e) { return Explain(e); }
        catch (Exception e) { return "ОШИБКА: DMC не ответил: " + e.Message; }
        if (p == null)
            return $"DMC не нашёл «{idOrSlug}» — нет такого, или это чужой черновик"
                   + (token == null ? $" (входа нет: выполните `{Brand.Cli} login`, чтобы видеть свои черновики)." : ".");
        return Describe(p);
    }

    // ------------------------------------------------------------------- общее

    internal string Describe(ProductInfo p)
    {
        var line = $"{p.Name} — {StatusWord(p.PublicationStatus)}\nСсылка: {p.Url(dmc.Site)}\n"
                 + $"Стойка: {p.Controller}\nСтанок: {p.Machine}";
        if (p.MachineType != null) line += $", тип {p.MachineType}";
        if (p.NumberOfAxes is int n) line += $", осей {n}";
        return line;
    }

    internal static string StatusWord(string s) => s switch
    {
        "DRAFT" => "черновик",
        "PENDING_REVIEW" => "на модерации",
        "PUBLISHED" => "опубликован",
        "REJECTED" => "отклонён",
        "DISABLED" => "отключён",
        "ARCHIVED" => "в архиве",
        _ => s,
    };

    internal static string Explain(DmcHttpException e) => e.Status switch
    {
        401 => NoLogin,
        403 => "ОШИБКА: нужна роль паблишера в DMC — попросите её у администратора.",
        404 => "ОШИБКА: DMC не нашёл такой компонент — или он не ваш.",
        409 => "ОШИБКА: у вас уже идёт импорт в DMC — дождитесь его и повторите. (" + ErrorText(e.Body) + ")",
        _ => $"ОШИБКА: DMC ответил {e.Status}: {ErrorText(e.Body)}",
    };

    /** Бэкенд шлёт ошибки как {"error":"..."} — показываем текст, а не JSON. */
    internal static string ErrorText(string body)
    {
        try
        {
            var r = JsonDocument.Parse(body).RootElement;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString()!;
        }
        catch (JsonException) { /* не JSON — отдаём как есть */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private async Task<(string? Token, string? Error)> Token()
    {
        try
        {
            var t = await tokens.GetAccessToken();
            return (t, t == null ? NoLogin : null);
        }
        catch (InvalidOperationException e) { return (null, "ОШИБКА: " + e.Message); }
    }
}
