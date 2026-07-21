using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Кандидат LLM-подбора: навык реестра + обоснование, почему он подходит контексту.
public record SkillSuggestion(RegistrySkill Skill, string Reason);

// LLM-подбор навыков (три контекста: персона / проект / свободный запрос). Каталог берётся
// из курируемых репозиториев реестра (Skills:CatalogRepos) через SkillsCliService.ListRepoAsync
// (навыки С ОПИСАНИЯМИ), кэшируется. Модель — Skills:AiModel (дефолт haiku), one-shot через
// общий OneShotClaudeRunner. Подбор работает только по агрегированному каталогу (в промпт);
// по безлимитному реестру остаётся обычный текстовый поиск (SkillsCliService.FindAsync).
public class SkillSuggestService(
    SkillsCliService cli,
    ICheapTextRunner cheap,
    SkillTranslationService translation,
    PersonaManager personas,
    ProjectManager projects,
    IConfiguration config,
    ILogger<SkillSuggestService> log)
{
    private static readonly string[] DefaultCatalogRepos = ["anthropics/skills", "vercel-labs/agent-skills"];

    private string[] CatalogRepos =>
        config.GetSection("Skills:CatalogRepos").Get<string[]>() is { Length: > 0 } r ? r : DefaultCatalogRepos;

    private string? AiModel => config["Skills:AiModel"] is { Length: > 0 } m ? m : "haiku";

    // Кэш агрегированного каталога (TTL). Клонирование репозиториев в ListRepoAsync дорогое —
    // держим общий снимок на всех пользователей (каталог публичный).
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private IReadOnlyList<RegistrySkill>? _catalog;
    private DateTime _catalogAt;
    private TimeSpan CatalogTtl =>
        TimeSpan.FromMinutes(int.TryParse(config["Skills:CatalogTtlMinutes"], out var m) ? m : 30);

    // --- Каталог ---

    public async Task<IReadOnlyList<RegistrySkill>> GetCatalogAsync(CancellationToken ct = default)
    {
        if (_catalog is not null && DateTime.UtcNow - _catalogAt < CatalogTtl) return _catalog;
        await _catalogLock.WaitAsync(ct);
        try
        {
            if (_catalog is not null && DateTime.UtcNow - _catalogAt < CatalogTtl) return _catalog;
            var acc = new List<RegistrySkill>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in CatalogRepos)
            {
                try
                {
                    foreach (var s in await cli.ListRepoAsync(repo, ct))
                        if (seen.Add(s.Source + "@" + s.Skill)) acc.Add(s);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Каталог навыков: репозиторий {Repo} пропущен", repo);
                }
            }
            _catalog = acc;
            _catalogAt = DateTime.UtcNow;
            return acc;
        }
        finally { _catalogLock.Release(); }
    }

    // --- Подбор ---

    // Подбор под персону: роль/характер + существующие skill-привязки (их исключаем из кандидатов).
    public async Task<IReadOnlyList<SkillSuggestion>> SuggestForPersonaAsync(string ownerId, string personaId,
        CancellationToken ct = default)
    {
        var persona = personas.Get(personaId, ownerId)
            ?? throw new KeyNotFoundException("Персона не найдена");
        var already = persona.Bindings?
            .Where(b => b.Type == PersonaBindingType.Skill)
            .Select(b => b.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return await SuggestAsync(BuildPersonaContext(persona), already, ct);
    }

    // Подбор под проект: имя + системный промпт проекта.
    public async Task<IReadOnlyList<SkillSuggestion>> SuggestForProjectAsync(string projectId,
        CancellationToken ct = default)
    {
        var project = projects.GetById(projectId)
            ?? throw new KeyNotFoundException("Проект не найден");
        var sb = new StringBuilder();
        sb.AppendLine($"Проект: {project.Name}");
        if (!string.IsNullOrWhiteSpace(project.SystemPrompt))
            sb.AppendLine($"Описание/правила проекта: {project.SystemPrompt}");
        return await SuggestAsync(sb.ToString(), null, ct);
    }

    // Подбор по свободному запросу пользователя («нужен навык для работы с PDF и таблицами»).
    public async Task<IReadOnlyList<SkillSuggestion>> SuggestForQueryAsync(string query,
        CancellationToken ct = default) =>
        await SuggestAsync($"Задача/запрос пользователя: {query}", null, ct);

    private static string BuildPersonaContext(Persona p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Персона: {p.Role ?? p.Name} ({p.Name})");
        if (!string.IsNullOrWhiteSpace(p.Description)) sb.AppendLine($"Кто это: {p.Description}");
        var character = p.Contract?.Character ?? p.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(character)) sb.AppendLine($"Характер и обязанности: {character}");
        if (p.Contract?.MustDo is { Count: > 0 } must)
            sb.AppendLine("Всегда делает: " + string.Join("; ", must.Where(x => !string.IsNullOrWhiteSpace(x))));
        return sb.ToString();
    }

    // Ядро: каталог + контекст → промпт → JSON-массив выбранных → матч с каталогом.
    // exclude — источники@навыки (по имени навыка), которые уже привязаны и не предлагаются.
    private async Task<IReadOnlyList<SkillSuggestion>> SuggestAsync(string context,
        ISet<string>? excludeBySkillName, CancellationToken ct)
    {
        var catalog = (await GetCatalogAsync(ct))
            .Where(s => excludeBySkillName is null || !excludeBySkillName.Contains(s.Skill))
            .ToList();
        if (catalog.Count == 0) return [];

        var prompt = BuildPrompt(context, catalog);
        string answer;
        try
        {
            answer = await cheap.RunAsync(LocalActionCatalog.SkillSuggest, prompt, AiModel, ct: ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LLM-подбор навыков не удался");
            return [];
        }

        var picks = ParsePicks(answer);
        var byKey = catalog.ToDictionary(s => s.Skill, s => s, StringComparer.OrdinalIgnoreCase);
        var result = new List<SkillSuggestion>();
        foreach (var (skill, reason) in picks)
            if (byKey.TryGetValue(skill, out var s))
                result.Add(new SkillSuggestion(s, reason));

        return await LocalizeDescriptionsAsync(result, ct);
    }

    // Переводит описания кандидатов на русский (реестр — на английском). Ошибка перевода
    // молча оставляет оригинал (перевод — украшение, не критичен для установки).
    private async Task<IReadOnlyList<SkillSuggestion>> LocalizeDescriptionsAsync(
        IReadOnlyList<SkillSuggestion> items, CancellationToken ct)
    {
        var toTranslate = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Skill.Description))
            .Select(i => ($"{i.Skill.Source}@{i.Skill.Skill}", i.Skill.Description!))
            .ToList();
        if (toTranslate.Count == 0) return items;

        var ru = await translation.TranslateDescriptionsAsync(toTranslate, ct: ct);
        if (ru.Count == 0) return items;

        return items.Select(i =>
        {
            var key = $"{i.Skill.Source}@{i.Skill.Skill}";
            return ru.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t)
                ? new SkillSuggestion(i.Skill with { Description = t }, i.Reason)
                : i;
        }).ToList();
    }

    private static string BuildPrompt(string context, IReadOnlyList<RegistrySkill> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты подбираешь навыки (skills) агента под контекст. Навык — набор инструкций,");
        sb.AppendLine("расширяющий возможности агента в конкретной области.");
        sb.AppendLine();
        sb.AppendLine("КОНТЕКСТ:");
        sb.AppendLine(context.Trim());
        sb.AppendLine();
        sb.AppendLine("КАТАЛОГ НАВЫКОВ (имя — описание):");
        foreach (var s in catalog)
            sb.AppendLine($"- {s.Skill} — {Shorten(s.Description, 300)}");
        sb.AppendLine();
        sb.AppendLine("Выбери от 0 до 5 наиболее релевантных навыков строго из каталога выше.");
        sb.AppendLine("Не выдумывай имена. Если ничего не подходит — верни пустой массив.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-массивом без markdown, формат:");
        sb.AppendLine("[{\"skill\":\"<имя из каталога>\",\"reason\":\"<кратко по-русски, почему подходит>\"}]");
        return sb.ToString();
    }

    private static string Shorten(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    // Достаёт массив объектов {skill, reason} из ответа LLM (терпимо к обрамлению markdown/текстом).
    internal static IReadOnlyList<(string Skill, string Reason)> ParsePicks(string answer)
    {
        var json = ExtractJsonArray(answer);
        if (json is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<(string, string)>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var skill = el.TryGetProperty("skill", out var s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(skill)) continue;
                var reason = el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                result.Add((skill.Trim(), reason.Trim()));
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    private static string? ExtractJsonArray(string s)
    {
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }
}
