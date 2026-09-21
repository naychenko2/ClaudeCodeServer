using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Docs;

/// <summary>
/// Итог фазы 2: те же факты сканера, но с суждением модели у части из них.
/// ModelNote пишет СЕРВЕР и только при неполноте — «модель не настроена», «ответ не
/// разобрался», «не уложилась во время»: основной сценарий отказа тот, где ответа модели
/// нет вовсе, и достать оттуда текст невозможно. При успехе — null.
/// </summary>
public sealed record MapReviewResult(
    string? BaseSha, string? ModelNote, IReadOnlyList<MapSuggestion> Suggestions);

/// <summary>
/// Формулировки модели поверх готовых фактов сканера (фаза 2 уборки карты).
///
/// Инвариант всей фичи: в предложение попадает только то, что посчитал детерминированный
/// код. Модель диктует РОВНО три поля — <c>id</c>, <c>severity</c>, <c>modelSays</c>, —
/// и держится это не её послушанием, а схемой разбора: остальных полей в ней просто нет,
/// брать их неоткуда. Патч, якорь, вид находки и экономию строк сервер берёт из своего
/// отчёта; предложение с id, которого в фактах свежего скана нет, выбрасывается целиком.
///
/// Отказ модели не ошибка экрана: факты показываются полностью, с пустыми формулировками
/// (принцип «факт важнее формулировки»). Разбор — с тем же тихим фолбэком, что у подбора
/// значка проекта (<c>ProjectIconGlyphService.ParsePick</c>).
/// </summary>
public sealed class ProjectMapReviewService(
    ICheapTextRunner cheap, ILogger<ProjectMapReviewService> log, IConfiguration? config = null)
{
    // Предохранитель размера ВЫХОДА, и он не косметический: у профиля Large потолок
    // вывода локали 1024 токена, а пер-местного потолка у LocalAction нет вовсе.
    // Число просится в промпте, но принуждает его сервер — иначе расчёт потолка
    // был бы надеждой, а не пределом
    private readonly int _maxSuggestions =
        int.TryParse(config?["ProjectMap:MaxSuggestions"], out var m) && m >= 0 ? m : 8;

    // Потолок фразы модели. Усечение серверное по той же причине: просьба в промпте
    // соблюдается «обычно», а ломает раскладку карточки как раз необычный случай
    internal const int MaxModelSaysLength = 120;

    // Сколько фактов уходит в промпт. Больше предложений человеку всё равно не покажут
    // (MaxSuggestions), а вход у дешёвого профиля не резиновый
    private const int MaxFactsInPrompt = 20;

    // Тексты неполноты — человеку, а не в лог: по глухому «не удалось» он не починит
    // «модель для этого места не настроена»
    internal const string NoteNoModel = "Модель не ответила — показываю факты сканера без формулировок.";
    internal const string NoteBadJson = "Ответ модели не удалось разобрать — показываю факты сканера без формулировок.";

    /// <param name="report">Свежий отчёт сканера: источник всех фактов и id.</param>
    /// <param name="ownerId">Владелец проекта — по нему резолвится слот модели места.</param>
    public async Task<MapReviewResult> ReviewAsync(
        MapHygieneReport report, string? ownerId, CancellationToken ct = default)
    {
        var facts = report.Suggestions;
        // Находок нет — обращаться к модели не за чем: «карта в порядке» это готовый
        // ответ, а не повод потратить ход
        if (facts.Count == 0) return new MapReviewResult(report.BaseSha, null, facts);

        var turn = Stopwatch.StartNew();
        string raw;
        try
        {
            raw = await cheap.RunAsync(LocalActionCatalog.ProjectMapHygiene,
                BuildPrompt(report, _maxSuggestions), ownerId: ownerId, jsonFormat: "json", ct: ct);
        }
        catch (Exception ex)
        {
            turn.Stop();
            log.LogWarning(ex, "Уборка карты: модель не ответила ({Ms} мс), отдаю факты сканера",
                (long)turn.Elapsed.TotalMilliseconds);
            return new MapReviewResult(report.BaseSha, NoteNoModel, facts);
        }
        turn.Stop();

        var judgments = ParseJudgments(raw);
        if (judgments is null)
        {
            log.LogWarning("Уборка карты: ответ модели не разобрался как JSON ({Ms} мс), отдаю факты сканера",
                (long)turn.Elapsed.TotalMilliseconds);
            return new MapReviewResult(report.BaseSha, NoteBadJson, facts);
        }

        var merged = Merge(facts, judgments, _maxSuggestions);
        log.LogWarning("Уборка карты: {Facts} фактов, {Judged} с формулировкой ({Ms} мс)",
            facts.Count, merged.Count(s => s.ModelSays is not null), (long)turn.Elapsed.TotalMilliseconds);
        return new MapReviewResult(report.BaseSha, null, merged);
    }

    // Суждение модели в разобранном виде — ровно то, что ей позволено сказать
    internal sealed record Judgment(string Id, string? Severity, string? ModelSays);

    /// <summary>
    /// Сшивка суждений с фактами. Суждение приезжает поверх ПОЛНОГО списка фактов:
    /// модель промолчала по части находок или упёрлась в потолок — факты всё равно
    /// показываются с пустой формулировкой, иначе счётчик группы в шапке разошёлся бы
    /// с её содержимым, а при мёртвой модели группа исчезла бы при живых фактах.
    /// </summary>
    internal static IReadOnlyList<MapSuggestion> Merge(
        IReadOnlyList<MapSuggestion> facts, IReadOnlyList<Judgment> judgments, int maxSuggestions)
    {
        var byId = new Dictionary<string, Judgment>(StringComparer.OrdinalIgnoreCase);
        var taken = 0;
        foreach (var j in judgments)
        {
            // Id не из фактов свежего скана — предложение выброшено целиком, а не
            // показано с пустым фактом: модель, выдумавшая секцию, до экрана не доедет
            if (!facts.Any(f => string.Equals(f.Id, j.Id, StringComparison.OrdinalIgnoreCase))) continue;
            if (byId.ContainsKey(j.Id)) continue;               // повтор того же id — берём первый
            if (taken >= maxSuggestions) break;                 // потолок принуждает сервер
            byId[j.Id] = j;
            taken++;
        }

        var result = new List<MapSuggestion>(facts.Count);
        foreach (var fact in facts)
        {
            if (!byId.TryGetValue(fact.Id, out var j)) { result.Add(fact); continue; }
            result.Add(fact with
            {
                // Единственное, что проходит как есть, и то через белый список с дефолтом
                // по виду находки: цена ошибки — цвет плашки, а не правка файла
                Severity = j.Severity is { } s && MapSuggestions.Severities.Contains(s)
                    ? s
                    : MapSuggestions.DefaultSeverity(fact.Kind),
                ModelSays = Shorten(j.ModelSays),
            });
        }
        return result;
    }

    private static string? Shorten(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= MaxModelSaysLength ? t : t[..(MaxModelSaysLength - 1)] + "…";
    }

    /// <summary>
    /// Разбор ответа модели: null — ответа не разобрать (тихий фолбэк на факты сканера).
    /// Схема нарочно узкая: полей <c>apply</c>, <c>fact</c>, <c>anchor</c> и <c>kind</c>
    /// в ней нет, поэтому протащить их невозможно — это и есть механизм, а не обещание.
    /// </summary>
    internal static IReadOnlyList<Judgment>? ParseJudgments(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("suggestions", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<Judgment>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(id)) continue;

                var severity = el.TryGetProperty("severity", out var sevEl)
                    && sevEl.ValueKind == JsonValueKind.String
                    ? sevEl.GetString()?.Trim().ToLowerInvariant()
                    : null;
                var says = el.TryGetProperty("modelSays", out var saysEl)
                    && saysEl.ValueKind == JsonValueKind.String
                    ? saysEl.GetString()
                    : null;

                result.Add(new Judgment(id, severity, says));
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- промпт ----------

    // Модель получает пронумерованные факты и ничего больше: карты она не видела, значит
    // любое утверждение про файл — догадка. Просим суждение, а не находки
    internal static string BuildPrompt(MapHygieneReport report, int maxSuggestions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты помогаешь прибраться в карте проекта — файле CLAUDE.md, который грузится в контекст");
        sb.AppendLine("КАЖДОЙ сессии и потому оплачивается токенами снова и снова.");
        sb.AppendLine();
        sb.AppendLine($"Карта: {report.Path} · {report.Lines} строк · ≈{report.ApproxTokens} токенов " +
                      $"(ориентир — до {report.Budget.RecommendedLines} строк).");
        if (report.DeadLinkCount > 0 || report.DeadImportCount > 0)
            sb.AppendLine($"Мёртвых ссылок: {report.DeadLinkCount}, мёртвых @-импортов: {report.DeadImportCount}.");
        sb.AppendLine();
        sb.AppendLine("Ниже — факты, которые нашёл детерминированный сканер. Самого файла ты не видишь:");
        sb.AppendLine("новых находок добавлять нельзя, придумывать секции, пути и номера строк — тоже.");
        sb.AppendLine();

        foreach (var s in report.Suggestions.Take(MaxFactsInPrompt))
            sb.AppendLine($"[{s.Id}] {KindLabel(s.Kind)}{Where(s)}: {s.Fact}");

        sb.AppendLine();
        sb.AppendLine("По каждому факту, где тебе есть что сказать по делу, дай ОДНО короткое суждение:");
        sb.AppendLine("что с этим сделать и почему (например «похоже на пересказ ADR-014 — свернуть в ссылку»).");
        sb.AppendLine("Молчать про факт можно — общие слова хуже молчания.");
        sb.AppendLine();
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом такого вида, без пояснений и без markdown-обвязки:");
        sb.AppendLine("""
            {
              "suggestions": [
                { "id": "0a1b2c3d4e5f6071", "severity": "medium", "modelSays": "Пересказ ADR-014 — свернуть в ссылку" }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Правила:");
        sb.AppendLine("- id — строго один из перечисленных выше, с точностью до символа; выдуманный будет отброшен;");
        sb.AppendLine($"- severity — ровно одно из: {MapSuggestions.SeverityHigh}, " +
                      $"{MapSuggestions.SeverityMedium}, {MapSuggestions.SeverityLow};");
        sb.AppendLine($"- modelSays — по-русски, не длиннее {MaxModelSaysLength} символов, без вступлений;");
        sb.AppendLine($"- не больше {maxSuggestions} суждений, и лучше меньше — только там, где есть что сказать;");
        sb.AppendLine("- никаких других полей: путь, строку, вид находки и способ починки сервер знает сам.");
        return sb.ToString();
    }

    private static string KindLabel(string kind) => kind switch
    {
        MapSuggestions.KindDeadLink => "мёртвая ссылка",
        MapSuggestions.KindDeadImport => "мёртвый импорт",
        MapSuggestions.KindRootRelative => "ссылка от корня проекта",
        MapSuggestions.KindLongSection => "длинная секция",
        _ => kind,
    };

    // Где находка стоит: заголовок секции (у длинной секции — её собственный) и строка
    private static string Where(MapSuggestion s) =>
        s.Anchor.Heading is { Length: > 0 } h ? $" «{h}» (строка {s.Anchor.Line})" : $" (строка {s.Anchor.Line})";

    // Ответ может приехать в ```-заборе или с болтовнёй вокруг: берём объект от первой {
    // до парной ей } (приём DocumentAiService/ProjectIconGlyphService)
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }
}
