using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Dossiers;

// Итог экспорта: Exported — сколько паспортов вошло в снапшот после предохранителей;
// Committed=false — «нечего выгружать»: дерево снапшота совпало с tip ветки (или выгружать
// действительно нечего), новый коммит не создавался. CommitSha — tip ветки, если известен.
public sealed record DossiersExportResult(int Exported, bool Committed, string? CommitSha);

// Запись index.json — связь SHA ↔ паспорт (файл в ветке) ↔ обсуждение ↔ задача (ADR-004 §6).
// Discussion — путь конспекта discussions/…: заводится СРАЗУ nullable, этап конспектов
// заполнит его позже (пока всегда null). TaskId nullable по природе паспорта.
public sealed record DossierIndexEntry(
    string Sha,
    string File,
    string Subject,
    DateTimeOffset CommittedAt,
    string? Discussion,
    string? TaskId,
    IReadOnlyList<string> SupersededSha);

public sealed record DossierBranchIndex(int Version, IReadOnlyList<DossierIndexEntry> Entries);

// Сервис выгрузки паспортов изменений в ветку ccs/dossiers/v1 (ADR-004 §6, «Истории
// решений»). Формирует ПОЛНОЕ дерево ветки из DossierStore и пишет его методом GitService
// (чистый plumbing, рабочее дерево не трогается). Инкрементальность — дёшево: снапшот
// собирается целиком, а git-слой сравнивает дерево с tip и без изменений коммит не создаёт.
// Автопуша нет и не будет: публикация — PushDossiersBranchAsync по явной команде пользователя.
public sealed class DossierGitExporter
{
    private const string IndexPath = "index.json";
    private const int IndexVersion = 1;
    private const int MaxSlugChars = 48;

    private static readonly JsonSerializerOptions IndexJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SessionManager _sessions;
    private readonly DossierStore _store;
    private readonly GitService _git;
    private readonly InstanceSecretsProvider _secrets;
    private readonly ILogger<DossierGitExporter>? _log;

    public DossierGitExporter(SessionManager sessions, DossierStore store, GitService git,
        InstanceSecretsProvider secrets, ILogger<DossierGitExporter>? log = null)
    {
        _sessions = sessions;
        _store = store;
        _git = git;
        _secrets = secrets;
        _log = log;
    }

    // Выгрузка: полный снапшот паспорта проекта в ветку ccs/dossiers/v1. Первый запуск
    // увозит всё накопленное, дальше git-дедуп по дереву — без новых паспортов коммита нет.
    public async Task<DossiersExportResult> ExportAsync(string ownerId, Project project,
        CancellationToken ct = default)
    {
        var files = BuildFiles(ownerId, project);
        var dossiers = files.Count - 1;   // последний файл — index.json

        // Паспортов нет и ветка ещё не создавалась — пустой корневой коммит не нужен,
        // сразу «нечего выгружать». Если ветка есть, а подходящих паспортов не осталось
        // (чаты ушли в opt-out или удалены) — наоборот, пишем опустевший снапшот: полный
        // снапшот обязан вычистить из ветки то, что больше не должно там жить.
        if (dossiers == 0 && !await HasBranchAsync(ownerId, project.RootPath, ct))
            return new DossiersExportResult(0, Committed: false, CommitSha: null);

        var message = dossiers > 0
            ? $"docs(dossiers): выгрузить историю решений ({dossiers} шт.)"
            : "docs(dossiers): очистить историю решений";
        var result = await _git.WriteDossiersBranchAsync(ownerId, project.RootPath, files, message, ct);
        if (result.Created)
            _log?.LogInformation(
                "dossiers: экспорт проекта {Project} в {Ref}: {Count} паспортов, коммит {Sha}",
                project.Id, GitService.DossiersRef, dossiers, result.CommitSha);
        return new DossiersExportResult(dossiers, result.Created, result.CommitSha);
    }

    // Полное дерево ветки из паспортов проекта: файлы dossiers/{yyyy}/{mm}/{sha7}-{slug}.md
    // плюс index.json. Порядок детерминирован (CommittedAt, Sha) — одинаковый стор обязан
    // давать побайтово одинаковое дерево, иначе каждый повторный экспорт плодил бы коммит.
    public IReadOnlyList<GitDossierFile> BuildFiles(string ownerId, Project project)
    {
        var secrets = _secrets.GetExactSecrets();
        var files = new List<GitDossierFile>();
        var entries = new List<DossierIndexEntry>();
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in _store.List(ownerId, project.Id)
                     .OrderBy(d => d.CommittedAt)
                     .ThenBy(d => d.CommitSha, StringComparer.Ordinal))
        {
            var session = d.SessionId is null ? null : _sessions.GetById(d.SessionId);
            if (!ShouldExportDossier(session,
                    session is null ? null : _sessions.ResolveOwnerId(session), ownerId, project.Id))
                continue;

            // Редакция — непосредственно перед формированием содержимого (ADR-004 §6):
            // паспорт мог родиться более старой версией SecretRedactor. Slug строится из
            // ОТРЕДАКТИРОВАННОГО subject — имя файла тоже содержимое ветки, секрет не
            // должен просочиться хотя бы в путь.
            var subject = SecretRedactor.Redact(d.CommitSubject, secrets);
            var shaChars = 7;
            var path = DossierPath(d.CommittedAt, d.CommitSha, subject, shaChars);
            while (!usedPaths.Add(path) && shaChars < d.CommitSha.Length)
                path = DossierPath(d.CommittedAt, d.CommitSha, subject, ++shaChars);

            files.Add(new GitDossierFile(path, FormatDossier(d, secrets)));
            entries.Add(new DossierIndexEntry(d.CommitSha, path, subject, d.CommittedAt,
                Discussion: null, TaskId: d.TaskId, SupersededSha: d.SupersededSha));
        }

        files.Add(new GitDossierFile(IndexPath, SerializeIndex(entries)));
        return files;
    }

    // Предохранители выгрузки (ADR-004 §6): в ветку едет паспорт только чата ЭТОГО владельца
    // и проекта — личные и глобальные чаты отсекаются всегда, opt-out «не включать в
    // летопись» уважается и здесь, а не только при захвате (тумблер могли включить после
    // коммита). Чат-источник удалён (null) — принадлежность нечем подтвердить, fail-closed:
    // паспорт остаётся в сторе, но наружу не едет.
    internal static bool ShouldExportDossier(Session? session, string? sessionOwnerId,
        string ownerId, string projectId) =>
        session is not null
        && DossierCaptureService.ShouldCaptureSession(
            DossierCaptureService.SessionBelongsToProject(sessionOwnerId, session.ProjectId, ownerId, projectId),
            session.ExcludeFromDossiers);

    // Путь паспорта в ветке: dossiers/{yyyy}/{mm}/{sha7}-{slug}.md. Год/месяц — от даты
    // коммита, sha-префикс расширяется вызывающим при коллизии полного пути (крайне редко,
    // но имена файлов обязаны быть уникальными), slug — транслитерация subject без модели.
    internal static string DossierPath(DateTimeOffset committedAt, string sha, string subject, int shaChars = 7)
    {
        var prefix = sha.Length <= shaChars ? sha : sha[..shaChars];
        return $"dossiers/{committedAt.Year}/{committedAt.Month:00}/{prefix}-{Slugify(subject)}.md";
    }

    // Транслитерация кириллицы для slug: однозначная, без контекстных правил (й→y, х→kh,
    // щ→sch) — детерминизм важнее филологической точности. ъ/ь выбрасываются (объект→obekt).
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    // Детерминированный slug из (уже отредактированного) subject: кириллица → латиница,
    // строчные, прочие символы → дефисы, повторы и краевые дефисы схлопываются, потолок
    // длины. Пустой результат (subject из одних символов) — нейтральное "dossier".
    internal static string Slugify(string subject)
    {
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in subject.ToLowerInvariant())
        {
            if (Translit.TryGetValue(ch, out var piece))
            {
                if (piece.Length == 0) continue;
                sb.Append(piece);
                lastDash = false;
            }
            else if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugChars) slug = slug[..MaxSlugChars].Trim('-');
        return slug.Length == 0 ? "dossier" : slug;
    }

    // Markdown паспорта: якоря (sha, дата, файлы, символы, источник) + выжимка + ссылки.
    // Идентификаторы (sha, пути, sessionId/taskId) редакцией не проходят — это не свободный
    // текст; все содержательные поля (subject, why, списки) — через SecretRedactor.
    internal static string FormatDossier(ChangeDossier d, IReadOnlyList<string> secrets)
    {
        string R(string? s) => SecretRedactor.Redact(s, secrets);

        var sb = new StringBuilder();
        sb.AppendLine("# " + R(d.CommitSubject));
        sb.AppendLine();
        sb.AppendLine($"- Коммит: `{d.CommitSha}` ({d.CommittedAt:yyyy-MM-dd})");
        if (d.SupersededSha.Count > 0)
            sb.AppendLine("- Заменил коммиты: " + string.Join(", ", d.SupersededSha.Select(s => $"`{s}`")));
        if (d.Files.Count > 0)
            sb.AppendLine("- Файлы: " + string.Join(", ", d.Files.Select(f => $"`{f}`")));
        if (d.Symbols.Count > 0)
            sb.AppendLine("- Символы: " + string.Join(", ", d.Symbols.Select(s => $"`{s}`")));
        var links = new List<string>();
        if (!string.IsNullOrEmpty(d.SessionId)) links.Add("чат " + d.SessionId);
        if (!string.IsNullOrEmpty(d.TaskId)) links.Add("задача " + d.TaskId);
        if (links.Count > 0) sb.AppendLine("- Источник: " + string.Join("; ", links));

        AppendSection(sb, "Зачем", R(d.Why));
        AppendListSection(sb, "Решения", d.Decisions, secrets);
        AppendListSection(sb, "Отвергнуто", d.Rejected, secrets);
        AppendListSection(sb, "Грабли", d.Pitfalls, secrets);
        AppendListSection(sb, "Инварианты", d.Invariants, secrets);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        sb.AppendLine();
        sb.AppendLine("## " + title);
        sb.AppendLine();
        sb.AppendLine(body);
    }

    private static void AppendListSection(StringBuilder sb, string title,
        IEnumerable<string> items, IReadOnlyList<string> secrets)
    {
        var list = items
            .Select(i => SecretRedactor.Redact(i, secrets))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("## " + title);
        sb.AppendLine();
        foreach (var i in list) sb.AppendLine("- " + i);
    }

    private static string SerializeIndex(IReadOnlyList<DossierIndexEntry> entries) =>
        JsonSerializer.Serialize(new DossierBranchIndex(IndexVersion, entries), IndexJsonOpts);

    // Существует ли уже ветка паспортов в этом репозитории (тем же ключом, что и
    // WriteDossiersBranchAsync). Ошибка git → консервативно «нет»: пустой коммит не создаём.
    private async Task<bool> HasBranchAsync(string ownerId, string root, CancellationToken ct)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root,
                ["rev-parse", "--quiet", GitService.DossiersRef], ct: ct);
            return r.Ok;
        }
        catch { return false; }
    }
}
