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
// Discussion — путь конспекта discussions/… чата-источника: null, когда конспект ещё не
// снят либо чат не едет в этой выгрузке. TaskId nullable по природе паспорта.
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
    private const string ReadmePath = "README.md";
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
    private readonly DossierDiscussionService _discussions;
    private readonly ILogger<DossierGitExporter>? _log;

    public DossierGitExporter(SessionManager sessions, DossierStore store, GitService git,
        InstanceSecretsProvider secrets, DossierDiscussionService discussions,
        ILogger<DossierGitExporter>? log = null)
    {
        _sessions = sessions;
        _store = store;
        _git = git;
        _secrets = secrets;
        _discussions = discussions;
        _log = log;
    }

    // Выгрузка: полный снапшот паспорта проекта в ветку ccs/dossiers/v1. Первый запуск
    // увозит всё накопленное, дальше git-дедуп по дереву — без новых паспортов коммита нет.
    // ensureDigests — снятие недостающих конспектов обсуждений (вызов модели на каждый
    // чат): только по явной команде человека — ручная выгрузка (POST /dossiers/export)
    // и отправка. Автовыгрузка (DossierAutoExporter) зовёт с false: фон не вправе молча
    // тратить модель (разбор 23.08). Уже снятые конспекты из стора едут в дерево в обоих
    // режимах — BuildFiles читает стор, а не генерирует.
    public async Task<DossiersExportResult> ExportAsync(string ownerId, Project project,
        bool ensureDigests = true, CancellationToken ct = default)
    {
        if (ensureDigests)
            await _discussions.EnsureAsync(ownerId, project, ct);
        var files = BuildFiles(ownerId, project);

        // Паспорты — файлы dossiers/**; index.json, README.md и конспекты discussions/**
        // в это число не входят (в сообщении коммита — число записей истории)
        var dossiers = files.Count(f => f.Path.StartsWith("dossiers/", StringComparison.Ordinal));
        var digests = files.Count(f => f.Path.StartsWith("discussions/", StringComparison.Ordinal));

        // Паспортов и конспектов нет и ветка ещё не создавалась — пустой корневой коммит
        // не нужен, сразу «нечего выгружать». Если ветка есть, а выгружаемого не осталось
        // (чаты ушли в opt-out или удалены) — наоборот, пишем опустевший снапшот: полный
        // снапшот обязан вычистить из ветки то, что больше не должно там жить.
        if (files.Count == 2 && !await HasBranchAsync(ownerId, project.RootPath, ct))
            return new DossiersExportResult(0, Committed: false, CommitSha: null);

        var message = dossiers > 0
            ? $"docs(dossiers): выгрузить историю решений ({dossiers} шт.)"
            : digests > 0
                ? "docs(dossiers): выгрузить конспекты обсуждений"
                : "docs(dossiers): очистить историю решений";
        var result = await _git.WriteDossiersBranchAsync(ownerId, project.RootPath, files, message, ct);
        if (result.Created)
            _log?.LogInformation(
                "dossiers: экспорт проекта {Project} в {Ref}: {Count} паспортов, {Digests} конспектов, коммит {Sha}",
                project.Id, GitService.DossiersRef, dossiers, digests, result.CommitSha);
        return new DossiersExportResult(dossiers, result.Created, result.CommitSha);
    }

    // Полное дерево ветки из паспорта проекта: файлы dossiers/{yyyy}/{mm}/{sha7}-{slug}.md,
    // конспекты discussions/{yyyy}/{sess7}-{slug}.md, плюс index.json и README.md в корне.
    // Порядок детерминирован (CommittedAt/GeneratedAt, идентификаторы) — одинаковый стор
    // обязан давать побайтово одинаковое дерево, иначе каждый повторный экспорт плодил бы
    // коммит.
    public IReadOnlyList<GitDossierFile> BuildFiles(string ownerId, Project project)
    {
        var secrets = _secrets.GetExactSecrets();
        var files = new List<GitDossierFile>();
        var entries = new List<DossierIndexEntry>();
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);

        // Конспекты обсуждений (ADR-004 §6): файл на чат со снятым конспектом; те же
        // предохранители, что у паспортов (живой чат этого владельца, opt-out), slug —
        // транслитерация темы, зафиксированной при снятии (не живого имени чата). Контент
        // и slug проходят SecretRedactor повторно — конспект мог родиться до появления
        // секрета. sessionId → путь: его получат записи index.json этого чата.
        var discussionPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var digest in _discussions.List(ownerId, project.Id).OrderBy(d => d.GeneratedAt))
        {
            var session = _sessions.GetById(digest.SessionId);
            if (session is null
                || !ShouldExportDossier(session, _sessions.ResolveOwnerId(session), ownerId, project.Id))
                continue;

            var topic = SecretRedactor.Redact(
                string.IsNullOrWhiteSpace(digest.Topic) ? "Обсуждение" : digest.Topic, secrets);
            var sessChars = 7;
            var dpath = DiscussionPath(session.CreatedAt.Year, digest.SessionId, topic, sessChars);
            while (!usedPaths.Add(dpath) && sessChars < digest.SessionId.Length)
                dpath = DiscussionPath(session.CreatedAt.Year, digest.SessionId, topic, ++sessChars);

            files.Add(new GitDossierFile(dpath, FormatDigest(digest, secrets)));
            discussionPaths[digest.SessionId] = dpath;
        }

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
            // Конспект чата-источника, если снят и едет в этой же выгрузке
            var discussion = d.SessionId is not null
                && discussionPaths.TryGetValue(d.SessionId, out var dp) ? dp : null;
            entries.Add(new DossierIndexEntry(d.CommitSha, path, subject, d.CommittedAt,
                Discussion: discussion, TaskId: d.TaskId, SupersededSha: d.SupersededSha));
        }

        files.Add(new GitDossierFile(IndexPath, SerializeIndex(entries)));
        // README — самодостаточное описание ветки для того, кто открыл её без приложения
        files.Add(new GitDossierFile(ReadmePath, ReadmeText));
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

    // Путь конспекта в ветке: discussions/{yyyy}/{sess7}-{slug}.md. Год — год СОЗДАНИЯ
    // чата (обсуждение относится к нему, а не к моменту снятия конспекта), sess-префикс
    // расширяется вызывающим при коллизии, slug — транслитерация темы со снятия конспекта.
    internal static string DiscussionPath(int year, string sessionId, string topic, int sessChars = 7)
    {
        var prefix = sessionId.Length <= sessChars ? sessionId : sessionId[..sessChars];
        return $"discussions/{year}/{prefix}-{Slugify(topic)}.md";
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

    // Markdown конспекта обсуждения: якорь (тема, чат, дата снятия) + тело конспекта от
    // модели. Всё, кроме sessionId, — свободный текст и проходит редакцию повторно:
    // конспект мог родиться раньше появления нового секрета инстанса.
    internal static string FormatDigest(DossierDiscussionRecord d, IReadOnlyList<string> secrets)
    {
        var topic = SecretRedactor.Redact(
            string.IsNullOrWhiteSpace(d.Topic) ? "Обсуждение" : d.Topic, secrets);
        var sb = new StringBuilder();
        sb.AppendLine("# " + topic);
        sb.AppendLine();
        sb.AppendLine($"- Чат: {d.SessionId}");
        sb.AppendLine($"- Конспект снят: {d.GeneratedAt:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine(SecretRedactor.Redact(d.Content, secrets).Trim());
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

    // Текст README.md в корне ветки — окончательная формулировка продакт-аналитика
    // (docs/features/decision-history-import-texts.md §1): без подстановок и плейсхолдеров,
    // побайтово одинаковый при каждой выгрузке, иначе экспорт плодил бы коммиты на пустом
    // месте. internal для теста; правки текста — только зеркально с docs. Raw string
    // нормализует переводы строк к LF — блоб одинаков на любой платформе.
    internal const string ReadmeText =
        """
        # История решений

        Эта ветка — память о том, **зачем** менялся код. Её ведёт AI Home: когда код меняют из чата
        или задачи, приложение сохраняет цель изменения, принятые решения,
        отвергнутые варианты и грабли. По команде человека накопленное выгружается сюда — в отдельную
        ветку рядом с кодом, чтобы знания уезжали вместе с репозиторием, а не оставались в одном
        приложении.

        `git blame` отвечает «кто и что». Эта ветка отвечает «зачем».

        ## Что здесь лежит

        ```
        dossiers/{yyyy}/{mm}/{sha7}-{slug}.md   одна запись — один коммит
        discussions/{yyyy}/{sess7}-{slug}.md    конспект обсуждения — не переписка
        index.json                              оглавление: коммит → файл записи
        README.md                               этот файл
        ```

        Запись лежит в папке года и месяца коммита, к которому относится. Имя файла — первые 7
        символов SHA коммита и короткая транслитерация его заголовка; если двум коммитам достался
        один и тот же префикс, он удлиняется до различимого. Внутри — заголовок коммита, якоря
        (SHA, дата, изменённые файлы и затронутые типы), а дальше разделы «Зачем», «Решения», «Отвергнуто»,
        «Грабли», «Инварианты». Пустых разделов не бывает: чего не было — того и нет.

        Рядом с записями лежат конспекты обсуждений — `discussions/{yyyy}/{sess7}-{slug}.md`,
        один файл на чат: решения и их аргументы, отвергнутые варианты, прозвучавшие требования.
        Это протокол сути разговора, снятый по его ленте, — а не сама переписка.

        `index.json` — оглавление ветки, по нему ищут запись, не перебирая папки:

        ```json
        {
          "version": 1,
          "entries": [
            {
              "sha": "полный SHA коммита",
              "file": "путь к файлу записи в этой ветке",
              "subject": "заголовок коммита",
              "committedAt": "дата коммита",
              "discussion": "путь к конспекту обсуждения, если чат его имеет",
              "taskId": "идентификатор задачи, если изменение делалось по задаче",
              "supersededSha": ["SHA, которые этот коммит заменил при squash или rebase"]
            }
          ]
        }
        ```

        ## Как найти запись по SHA коммита

        Ветку не нужно выкладывать в рабочую папку — читайте прямо из git.

        Через оглавление (надёжный путь: находит и коммиты, переписанные squash'ем — они остаются
        в `supersededSha`):

        ```
        git show ccs/dossiers/v1:index.json
        ```

        Напрямую по имени файла — быстрее, когда SHA не переписывали:

        ```
        git ls-tree -r --name-only ccs/dossiers/v1 | grep 1a2b3c4
        git show ccs/dossiers/v1:dossiers/2026/08/1a2b3c4-dobavit-import.md
        ```

        Если ветка пришла с `git fetch`, но локальной ещё нет — подставьте
        `origin/ccs/dossiers/v1`.

        ## Ветку не редактируют руками

        Каждая выгрузка перезаписывает содержимое ветки целиком, поэтому ручные правки исчезнут при
        следующей — и незаметно. Ветка обычная во всём остальном: `git pull` и `git push` работают
        как всегда, рабочую папку выгрузка не трогает, в историю кода эти коммиты не попадают.

        У кого проект открыт в AI Home — загружает записи отсюда к себе одной командой в панели
        «История решений»; чужие записи приложение не перезаписывает своими.

        ## Чего здесь нет

        Дословной переписки. В запись попадает выжимка — цель, решения, отказы, грабли, —
        а обсуждение едет конспектом: решения, аргументы, отвергнутое. Разговоры, помеченные
        в приложении как «не сохранять решения», не выгружаются вовсе — ни записью, ни
        конспектом. Секреты вычищаются перед записью в ветку.
        """;

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
