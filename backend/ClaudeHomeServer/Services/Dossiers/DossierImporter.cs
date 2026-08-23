using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Dossiers;

// Итог импорта: Added — реально добавленные записи, Skipped — остальные записи index.json
// (уже представленные импортированными, нечитаемые файлы, кривые sha). BranchFound=false —
// ветки ccs/dossiers/v1 нет ни локально, ни в origin.
public sealed record DossiersImportResult(int Added, int Skipped, bool BranchFound);

// Импорт «Историй решений» (этап 4, ADR-004 §6): читает ветку ccs/dossiers/v1 read-only
// plumbing-методами GitService (рабочее дерево не трогается) и раскладывает паспорта в
// DossierStore с пометкой происхождения. Идентичность записи (sha, дата, subject, задача,
// superseded) берётся из index.json — это машиночитаемый источник; markdown-файл даёт
// содержимое (выжимка, якоря, чат-источник). Коллизии и идемпотентность — в DossierStore
// .AddImportedRange: свой паспорт не перезаписывается никогда, повторный импорт — no-op.
//
// Границы доверия чужому index.json (разбор консилиума 23.08): партия одной выгрузки
// ограничена потолком (свежие по CommittedAt), неправдоподобные даты отсекаются наравне
// с кривыми sha — иначе ветка с тысячами записей и датой 2099 выдавливала бы собственные
// паспорта владельца из активного стора через EvictIfNeeded.
// Не sealed (паттерн DossierRecallService): ImportAsync переопределяется тестами для
// симуляции «выгрузка успела за время долгого импорта».
public class DossierImporter
{
    private const string IndexPath = "index.json";
    private const int IndexVersion = 1;

    // Дефолт потолка партии: легитимный обмен между машинами — десятки-сотни записей;
    // тысячи за один тик — уже атака на стор и Dify. Порядок потолка стора (5000) —
    // вытеснение остаётся запасным контуром, а не штатным путём.
    private const int DefaultMaxImportBatchEntries = 1000;

    // Дата коммита управляет вытеснением (DossierStore.EvictIfNeeded сортирует по
    // CommittedAt): всё до 2000 года и дальше суток в будущем — мусор во внешнем входе
    // (сутки — запас дрейфа часов между машинами общей папки).
    private static readonly DateTimeOffset MinPlausibleCommittedAt = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // index.json пишет экспортёр camelCase; Web-дефолты читают и camelCase, и PascalCase
    private static readonly JsonSerializerOptions IndexJsonOpts = new(JsonSerializerDefaults.Web);

    private readonly DossierStore _store;
    private readonly GitService _git;
    private readonly InstanceSecretsProvider _secrets;
    private readonly ILogger<DossierImporter>? _log;
    private readonly int _maxImportBatchEntries;

    public DossierImporter(DossierStore store, GitService git, InstanceSecretsProvider secrets,
        ILogger<DossierImporter>? log = null, int maxImportBatchEntries = DefaultMaxImportBatchEntries)
    {
        _store = store;
        _git = git;
        _secrets = secrets;
        _log = log;
        _maxImportBatchEntries = Math.Max(1, maxImportBatchEntries);
    }

    // stillForeign — предикат «tip всё ещё чужой», проверяется непосредственно перед
    // фиксацией партии в стор (после долгого чтения файлов ветки): так закрывается окно
    // гонки «update-ref → MarkOwnTip» параллельной выгрузки — автоимпорт сверяет tip с
    // DossierCaptureState. Ручной импорт идёт без предиката: его границы определяет
    // человек кнопкой. virtual для тестовой симуляции «выгрузка успела за время импорта».
    public virtual async Task<DossiersImportResult> ImportAsync(string ownerId, Project project,
        CancellationToken ct = default, Func<GitDossiersTip, bool>? stillForeign = null)
    {
        // Tip — автор и ветка-источник для пометки происхождения каждой записи
        var tip = await _git.GetDossiersTipAsync(ownerId, project.RootPath, ct);
        if (tip is null) return new DossiersImportResult(0, 0, BranchFound: false);

        var indexJson = await _git.ReadDossiersFileAsync(ownerId, project.RootPath, IndexPath, ct);
        if (indexJson is null)
        {
            _log?.LogWarning("dossiers: импорт проекта {Project}: ветка {Ref} без index.json",
                project.Id, GitService.DossiersRef);
            return new DossiersImportResult(0, 0, BranchFound: true);
        }

        DossierBranchIndex? index;
        try { index = JsonSerializer.Deserialize<DossierBranchIndex>(indexJson, IndexJsonOpts); }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "dossiers: импорт проекта {Project}: index.json не разобран", project.Id);
            return new DossiersImportResult(0, 0, BranchFound: true);
        }
        if (index is not { Entries.Count: > 0 }) return new DossiersImportResult(0, 0, BranchFound: true);
        if (index.Version != IndexVersion)
            _log?.LogWarning(
                "dossiers: импорт проекта {Project}: index.json версии {Version} (ожидается {Expected}) — парсим как есть",
                project.Id, index.Version, IndexVersion);

        var secrets = _secrets.GetExactSecrets();

        // Формальная валидация записей (sha и дата) — ДО чтения файлов: мусорные записи
        // не должны стоить git-вызовов.
        var valid = new List<DossierIndexEntry>();
        foreach (var e in index.Entries)
        {
            // Пустые sha/файл — тоже мусор во внешнем входе: десериализатор кривого JSON
            // молча даёт null конструкторным параметрам записи. Такая запись пропускается,
            // а не роняет импорт всей ветки.
            if (string.IsNullOrWhiteSpace(e.Sha) || string.IsNullOrWhiteSpace(e.File))
            {
                _log?.LogWarning("dossiers: импорт проекта {Project}: запись без sha или файла пропущена", project.Id);
                continue;
            }
            if (!IsValidSha(e.Sha))
            {
                _log?.LogWarning("dossiers: импорт проекта {Project}: запись с некорректным sha пропущена", project.Id);
                continue;
            }
            if (!IsPlausibleCommittedAt(e.CommittedAt))
            {
                _log?.LogWarning("dossiers: импорт проекта {Project}: запись {Sha} с неправдоподобной датой {At} пропущена",
                    project.Id, e.Sha[..7], e.CommittedAt);
                continue;
            }
            valid.Add(e);
        }

        // Потолок партии: чужой index.json без ограничения заваливал бы стор и Dify за
        // один тик. Берём свежие по CommittedAt — ценность для recall сконцентрирована в
        // недавних; хвост считается пропущенным и следующим тиком не догоняется (полный
        // снапшот ветки после дедупа упирается в тот же потолок).
        if (valid.Count > _maxImportBatchEntries)
        {
            _log?.LogWarning(
                "dossiers: импорт проекта {Project}: партия {Count} записей обрезана до {Cap} свежих по дате коммита",
                project.Id, valid.Count, _maxImportBatchEntries);
            valid = valid
                .OrderByDescending(e => e.CommittedAt)
                .Take(_maxImportBatchEntries)
                .ToList();
        }

        var candidates = new List<ChangeDossier>();
        foreach (var e in valid)
        {
            string? md;
            try
            {
                md = await _git.ReadDossiersFileAsync(ownerId, project.RootPath, e.File, ct);
            }
            // Путь записи — внешний вход: SafeJoin откажет на выходе за корень репо
            // (traversal), и этот отказ стоит пропустить одной записью, а не партии.
            catch (UnauthorizedAccessException ex)
            {
                _log?.LogWarning(ex, "dossiers: импорт проекта {Project}: путь {File} вне репозитория, запись пропущена",
                    project.Id, e.File);
                continue;
            }
            catch (InvalidOperationException ex)
            {
                _log?.LogWarning(ex, "dossiers: импорт проекта {Project}: путь {File} не читается, запись пропущена",
                    project.Id, e.File);
                continue;
            }
            if (md is null)
            {
                _log?.LogWarning("dossiers: импорт проекта {Project}: файл {File} ветки не читается, запись пропущена",
                    project.Id, e.File);
                continue;
            }
            candidates.Add(BuildDossier(ownerId, project.Id, e, md, tip, secrets));
        }

        // Окно гонки «update-ref → MarkOwnTip» параллельной выгрузки: чтение файлов ветки
        // идёт десятки секунд, и resolved tip мог стать НАШИМ. Это содержимое уже живёт в
        // сторе own-записями — Imported-копии завезли бы дубли, партию выбрасываем.
        if (stillForeign is not null && !stillForeign(tip))
        {
            _log?.LogInformation(
                "dossiers: импорт проекта {Project}: tip {Ref} стал своим за время чтения ветки — партия отброшена",
                project.Id, tip.Ref);
            return new DossiersImportResult(0, index.Entries.Count, BranchFound: true);
        }

        var added = _store.AddImportedRange(ownerId, project.Id, candidates);
        if (added > 0)
            _log?.LogInformation(
                "dossiers: импорт проекта {Project} из {Ref} @{Sha}: добавлено {Added}, пропущено {Skipped}",
                project.Id, tip.Ref, tip.CommitSha[..7], added, index.Entries.Count - added);
        return new DossiersImportResult(added, index.Entries.Count - added, BranchFound: true);
    }

    // sha из index.json — внешний вход: валидируем формально (hex 6-40), как это делает
    // GitService для своих аргументов, — мусорный sha ломал бы пути при реэкспорте
    // (dossiers/{yyyy}/{mm}/{sha7}-…) и спуфил бы поиск по коммиту. Null-толерантна:
    // валидацию проходят и элементы SupersededSha, где null возможен из кривого JSON.
    private static bool IsValidSha(string? sha) =>
        sha is { Length: >= 6 and <= 40 }
        && sha.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static bool IsPlausibleCommittedAt(DateTimeOffset at) =>
        at >= MinPlausibleCommittedAt && at <= DateTimeOffset.UtcNow.AddDays(1);

    private static ChangeDossier BuildDossier(string ownerId, string projectId, DossierIndexEntry e,
        string md, GitDossiersTip tip, IReadOnlyList<string> secrets)
    {
        // Редакция — над всем импортируемым содержимым (ветка может приехать с чужой машины
        // со старой версией редактора). Идентичность (sha, sessionId, taskId) — не свободный
        // текст, редакцией не проходит — тот же контракт, что у экспорта.
        string R(string? s) => SecretRedactor.Redact(s, secrets);
        var parsed = ParseMarkdown(md);

        return new ChangeDossier
        {
            OwnerId = ownerId,
            ProjectId = projectId,
            CommitSha = e.Sha,
            CommitSubject = R(e.Subject),
            CommittedAt = e.CommittedAt,
            SupersededSha = (e.SupersededSha ?? []).Where(IsValidSha).ToList(),
            SessionId = parsed.SessionId,
            TaskId = e.TaskId,
            Files = parsed.Files.Select(R).ToList(),
            Symbols = parsed.Symbols.Select(R).ToList(),
            Why = R(parsed.Why),
            Decisions = parsed.Decisions.Select(R).ToList(),
            Rejected = parsed.Rejected.Select(R).ToList(),
            Pitfalls = parsed.Pitfalls.Select(R).ToList(),
            Invariants = parsed.Invariants.Select(R).ToList(),
            Origin = DossierOrigin.Imported,
            // Автор tip-коммита — внешний вход той же природы, что subject: user.name —
            // свободная строка с чужой машины, редакция обязана покрывать её симметрично
            // экспорту (README ветки обещает «секреты вычищаются» и на входе)
            ImportedAuthor = R(tip.Author),
            ImportedFromBranch = NormalizeBranch(tip.Ref),
        };
    }

    // refs/heads/ccs/dossiers/v1 | refs/remotes/origin/ccs/dossiers/v1 → ccs/dossiers/v1
    private static string NormalizeBranch(string refName) =>
        refName.Replace("refs/remotes/origin/", "").Replace("refs/heads/", "");

    // --- Разбор markdown-паспорта (обратный парсер к DossierGitExporter.FormatDossier) ---

    // Содержимое, извлекаемое из markdown: якоря (файлы, символы), чат-источник и выжимка.
    // Идентичность (sha, дата, subject, задача, superseded) приходит из index.json и здесь
    // не дублируется. Записи с незнакомой структурой не роняют импорт: что не распознано —
    // остаётся пустым, скелет записи всё равно ценен (факт коммита восстановлен).
    internal sealed record ParsedDossier(
        string? SessionId,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Symbols,
        string Why,
        IReadOnlyList<string> Decisions,
        IReadOnlyList<string> Rejected,
        IReadOnlyList<string> Pitfalls,
        IReadOnlyList<string> Invariants);

    private static readonly Regex BacktickRx = new("`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex ChatLinkRx = new(@"\bчат\s+([A-Za-z0-9_-]+)", RegexOptions.Compiled);

    internal static ParsedDossier ParseMarkdown(string md)
    {
        string? sessionId = null;
        var files = new List<string>();
        var symbols = new List<string>();
        var decisions = new List<string>();
        var rejected = new List<string>();
        var pitfalls = new List<string>();
        var invariants = new List<string>();
        var whyLines = new List<string>();
        string? section = null;

        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                continue;
            }
            // Заголовок "# subject" не парсим — subject приходит из index.json
            if (section is null)
            {
                if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
                var item = line[2..].Trim();
                if (item.StartsWith("Файлы:", StringComparison.Ordinal))
                    files.AddRange(BacktickItems(item));
                else if (item.StartsWith("Символы:", StringComparison.Ordinal))
                    symbols.AddRange(BacktickItems(item));
                else if (item.StartsWith("Источник:", StringComparison.Ordinal))
                    sessionId = ChatLinkRx.Match(item) is { Success: true } m ? m.Groups[1].Value : null;
                // «Коммит» и «Заменил коммиты» — идентичность из index.json, здесь пропускаем
                continue;
            }

            var bullet = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim() : null;
            switch (section)
            {
                case "Зачем":
                    // Тело секции — свободный текст; пустые строки-разделители не копим.
                    // Join с '\n', не Environment.NewLine: стор сравнивается с исходником
                    // паспорта платформонезависимо (CI на Linux, ADR-независимо от Windows)
                    if (line.Length > 0) whyLines.Add(line);
                    break;
                case "Решения" when bullet is not null: decisions.Add(bullet); break;
                case "Отвергнуто" when bullet is not null: rejected.Add(bullet); break;
                case "Грабли" when bullet is not null: pitfalls.Add(bullet); break;
                case "Инварианты" when bullet is not null: invariants.Add(bullet); break;
                // Незнакомая секция (будущие версии формата) — пропускаем целиком
            }
        }

        return new ParsedDossier(sessionId, files, symbols,
            string.Join("\n", whyLines).Trim(), decisions, rejected, pitfalls, invariants);
    }

    // Значения в обратных кавычках: Файлы: `a.cs`, `b.cs` → [a.cs, b.cs]
    private static IEnumerable<string> BacktickItems(string item) =>
        BacktickRx.Matches(item).Select(m => m.Groups[1].Value);
}
