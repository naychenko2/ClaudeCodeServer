using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Git;

// LLM-помощь в git-UI: сообщение коммита по staged-диффу и название стэша по правкам.
// Идёт через «дешёвый» раннер: локальная модель Ollama (если действие заведено на неё)
// или claude (модель Git:AiModel, дефолт haiku) как фолбэк/по умолчанию.
public sealed class GitAiService(ICheapTextRunner cheap, GitService git, IConfiguration config)
{
    private const int DiffBudget = 12_000; // символов диффа в промпт — хвост обрезаем

    private string Model => config["Git:AiModel"] ?? "haiku";

    // Дефолтные правила стиля сообщения — используются, когда пользователь/проект не задал свой промпт
    public const string DefaultStyleRules = """
        Формат проекта: Conventional Commits НА РУССКОМ — `тип(область): описание`
        (типы: feat/fix/refactor/docs/chore/style/test), описание в повелительном
        наклонении («добавить», «исправить»), с маленькой буквы, без точки в конце,
        summary не длиннее 72 символов. Если изменения не похожи на код (документы,
        заметки) — обычная человеческая фраза без типа, например «обновить план поездки».
        """;

    public sealed record CommitSuggestion(string Summary, string Description);

    // customPrompt — правила стиля из настроек (проектный override / глобальный); null — дефолт.
    public async Task<CommitSuggestion?> SuggestCommitMessageAsync(string? ownerId, string root, string? customPrompt = null, CancellationToken ct = default)
    {
        // Дифф индекса; пусто — предлагать нечего
        var diff = (await git.RunAsync(ownerId, root, ["diff", "--cached"], ct: ct)).Stdout;
        if (string.IsNullOrWhiteSpace(diff)) return null;
        if (diff.Length > DiffBudget) diff = diff[..DiffBudget] + "\n…(дифф обрезан)";

        var hasCustom = !string.IsNullOrWhiteSpace(customPrompt);
        var rules = hasCustom ? customPrompt!.Trim() : DefaultStyleRules;
        var prompt = $$"""
            Ты помощник, придумывающий сообщение git-коммита по диффу.
            {{rules}}

            Ответь СТРОГО одним JSON-объектом без пояснений:
            {"summary": "...", "description": "..."}
            description — 1-3 предложения сути изменений; если summary достаточно, пустая строка.

            Дифф:
            {{diff}}
            """;
        // И дефолтный, и кастомный стиль идут единой дешёвой цепочкой действия git-commit-msg
        // (выбранное → локаль → claude), чтобы исполнителем полностью управлял админ через
        // «Фоновые задачи». Кастомные правила требовательнее к модели: нужно точное следование —
        // админ ставит действию Claude или сильную модель; дефолтный Conventional-стиль локаль
        // держит и на рекомендованной настройке.
        var raw = await cheap.RunAsync(LocalActionCatalog.GitCommitMsg, prompt, Model, ct: ct);
        return ParseSuggestion(raw);
    }

    // Определить стиль коммитов по истории репозитория → готовая промпт-инструкция стиля.
    // Не сохраняет: фронт вставляет результат в поле настройки, пользователь правит и сохраняет сам.
    public async Task<string?> DetectCommitStyleAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        // Последние коммиты (subject + тело) как образцы стиля
        var log = (await git.RunAsync(ownerId, root,
            ["log", "-n", "40", "--pretty=format:%s%n%b%x1e"], ct: ct)).Stdout;
        if (string.IsNullOrWhiteSpace(log)) return null;
        var samples = string.Join("\n---\n",
            log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
               .Select(s => s.Trim()).Where(s => s.Length > 0).Take(40));
        if (samples.Length > DiffBudget) samples = samples[..DiffBudget];

        var prompt = $"""
            Проанализируй стиль сообщений git-коммитов этого репозитория по образцам ниже.
            Определи: язык, формат (Conventional Commits или свободный), наклонение,
            заглавные/строчные, наличие типов/областей, длину, эмодзи, трейлеры.
            Верни КОРОТКУЮ инструкцию (2-5 предложений) для генератора будущих сообщений,
            чтобы они совпадали по стилю. Ответь только инструкцией, без преамбул и списков.

            Образцы коммитов:
            {samples}
            """;
        var raw = (await cheap.RunAsync(LocalActionCatalog.GitCommitMsg, prompt, Model, ct: ct)).Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    public async Task<string?> SuggestStashNameAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        var status = await git.StatusAsync(ownerId, root, ct);
        var files = status.Unstaged.Concat(status.Staged).Concat(status.Untracked)
            .Select(f => $"{f.Status} {f.Path}").Distinct().Take(40).ToList();
        if (files.Count == 0) return null;
        var diff = (await git.RunAsync(ownerId, root, ["diff"], ct: ct)).Stdout;
        if (diff.Length > 6_000) diff = diff[..6_000] + "\n…";

        var prompt = $"""
            Придумай КОРОТКОЕ название (3-6 слов, по-русски, без кавычек и точки)
            для отложенных изменений (git stash) — по списку файлов и диффу.
            Ответь только названием, одной строкой.

            Файлы:
            {string.Join('\n', files)}

            Дифф:
            {diff}
            """;
        var raw = (await cheap.RunAsync(LocalActionCatalog.GitStashName, prompt, Model, ct: ct)).Trim().Trim('"', '«', '»');
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : (line.Length > 80 ? line[..80] : line);
    }

    private static CommitSuggestion? ParseSuggestion(string raw)
    {
        // LLM мог обернуть JSON в текст/код-блок — берём первую { … } скобочную группу
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return Fallback(raw);
        try
        {
            var json = JsonDocument.Parse(raw[start..(end + 1)]);
            var summary = json.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var description = json.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(summary)) return Fallback(raw);
            return new CommitSuggestion(
                summary.Length > 100 ? summary[..100] : summary,
                description ?? "");
        }
        catch (JsonException) { return Fallback(raw); }
    }

    private static CommitSuggestion? Fallback(string raw)
    {
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : new CommitSuggestion(line.Length > 100 ? line[..100] : line, "");
    }
}
