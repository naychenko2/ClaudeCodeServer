using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Git;

// LLM-помощь в git-UI: сообщение коммита по staged-диффу и название стэша по правкам.
// One-shot через общий OneShotClaudeRunner (модель Git:AiModel, дефолт haiku).
public sealed class GitAiService(OneShotClaudeRunner runner, GitService git, IConfiguration config)
{
    private const int DiffBudget = 12_000; // символов диффа в промпт — хвост обрезаем
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private string Model => runner.NormalizeModel(config["Git:AiModel"] ?? "haiku") ?? "haiku";

    public sealed record CommitSuggestion(string Summary, string Description);

    public async Task<CommitSuggestion?> SuggestCommitMessageAsync(string? ownerId, string root, CancellationToken ct = default)
    {
        // Дифф индекса; пусто — предлагать нечего
        var diff = (await git.RunAsync(ownerId, root, ["diff", "--cached"], ct: ct)).Stdout;
        if (string.IsNullOrWhiteSpace(diff)) return null;
        if (diff.Length > DiffBudget) diff = diff[..DiffBudget] + "\n…(дифф обрезан)";

        var prompt = $$"""
            Ты помощник, придумывающий сообщение git-коммита по диффу.
            Формат проекта: Conventional Commits НА РУССКОМ — `тип(область): описание`
            (типы: feat/fix/refactor/docs/chore/style/test), описание в повелительном
            наклонении («добавить», «исправить»), с маленькой буквы, без точки в конце,
            summary не длиннее 72 символов. Если изменения не похожи на код (документы,
            заметки) — обычная человеческая фраза без типа, например «обновить план поездки».

            Ответь СТРОГО одним JSON-объектом без пояснений:
            {"summary": "...", "description": "..."}
            description — 1-3 предложения сути изменений; если summary достаточно, пустая строка.

            Дифф:
            {{diff}}
            """;
        var raw = await runner.RunAsync(prompt, Model, Timeout, ct);
        return ParseSuggestion(raw);
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
        var raw = (await runner.RunAsync(prompt, Model, Timeout, ct)).Trim().Trim('"', '«', '»');
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
