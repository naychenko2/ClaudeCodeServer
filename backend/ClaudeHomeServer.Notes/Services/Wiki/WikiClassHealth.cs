using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Автовыключение класса правки `origin × op` (ADR-035, решение 12г, мера 7) и нарастающий замер
// доли откатов (мера 8). Класс выключается сам, когда его самоприменение перестаёт быть
// качественным: доля откатов в окне последних WindowSize применённых операций класса ≥
// RetractSharePercent % или ≥ RetractSevenDayMin откатов за 7 дней. Пороги — числа ADR;
// делать их настройками — следующий шаг (здесь это константы кода).
//
// Источник правды — журнал и стор пакетов этого владельца (чужие песочницы не читаются:
// пути per-owner):
// - применённые операции класса — пакеты со статусом «applied»/«reverted», операции по тому же
//   правилу «что применилось», что и на шаге 6 применения (WikiApplyService.Applies): «auto»
//   всегда, «inbox» — только принятая человеком. Время операции — CreatedAt пакета: единственный
//   неизменяемый штамп пакета; применение всегда идёт после создания, и порядок применений
//   класса по нему не расходится с порядком создания.
// - откатанные операции — строки журнала op=retract (мера 7: «откат по источнику засчитывается
//   каждой затронутой операции»): строка называет пакеты и затронутые страницы — затронутой
//   считается применённая операция затронутого пакета, если её страница в списке строк; и
//   пакеты со статусом «reverted» (откат пакета, мера 4) — их применённые операции откатаны
//   все. Время отката — ts строки журнала, для «reverted» без строки — UpdatedAt пакета
//   (переход статуса ставит его в момент отката). Одна операция считается откатанной один раз
//   (множество по (пакет, индекс операции)), даже когда её задели обе записи.
//
// Окно — последние WindowSize применённых операций класса после ResumedAt («Снова применять
// само» начинает окно заново — старые откаты в него не входят). Счётчик «откатов за 7 дней» —
// откаты, случившиеся в [now−7д, now], класса после ResumedAt: он мерит приток откатов, а не
// их долю. Метрики 7/30 дней (мера 8) — другая пара: доля откатанных среди применённых в этом
// окне («что применено за 7 дней и отозвано»), по классам и источникам; проценты с одним знаком
// — та же формула, что у метрик S3-3.
//
// Выключение — запись в settings.json через WikiSettingsStore.Mutate (per-owner замок): решение
// могло опоздать (человек вернул класс во время оценки) — merge перечитывает состояние и
// пропускает выключение, если класс уже выключен или окно сдвинулось. Уведомление — ровно на
// переход «включён → выключен» (решение Софьи 3), не на каждый тик: его посылает тот же
// диспетчер, что и остальные уведомления вертикали наружу (INotificationSender).
//
// Флаг llm-wiki — существующий IFeatureFlagGate (решение Софьи 1): при выключенном флаге ни
// записей (settings/metrics), ни уведомлений. Оценка — одна за разом на владельца (замок):
// она читает журнал и стор и пишет settings/metrics; параллельные PUT настроек держит замок
// стора настроек, а не этот.
public sealed class WikiClassHealth
{
    // Пороги меры 7 (числа ADR-035 §12г).
    public const int WindowSize = 50;
    public const double RetractSharePercent = 10.0;
    public const int RetractSevenDayMin = 3;
    public static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
    public static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    private const string LogOpRetract = "retract";
    private const string LogOpRestore = "restore";
    private const string LogActorUser = "user";

    private readonly string _dataDir;
    private readonly Composition.IFeatureFlagGate? _flags;
    private readonly Composition.INotificationSender? _notifications;
    private readonly ILogger<WikiClassHealth>? _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _evalLocks = new();

    // Версия кода в строке метрик — та же форма, что у метрик линта (S3-3): сравнение рядов
    // без штампа — не сравнение.
    internal static readonly string CodeVersion =
        typeof(WikiClassHealth).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public WikiClassHealth(
        IConfiguration config,
        Composition.IFeatureFlagGate? flags = null,
        Composition.INotificationSender? notifications = null,
        ILogger<WikiClassHealth>? log = null)
    {
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
        _flags = flags;
        _notifications = notifications;
        _log = log;
    }

    public enum RunState { Ran, FeatureOff }

    /// <summary>Замер класса за 7 и 30 дней (мера 8): Applied{N} — операций класса, применённых
    /// в окне; Retracted{N} — из них откатанных (откат в любое время); Share{N} — доля, процент
    /// с одним знаком. Disabled — состояние класса после этой оценки.</summary>
    public sealed record ClassStat(
        string Class, bool Disabled,
        int Applied7, int Retracted7, double Share7,
        int Applied30, int Retracted30, double Share30);

    public readonly record struct Outcome(RunState State, IReadOnlyList<ClassStat> Stats);

    /// <summary>Оценка здоровья классов владельца: журнал + стор пакетов → пороги меры 7 →
    /// выключение (с уведомлением на переход) → строка метрик в keeper/metrics.jsonl.
    /// FeatureOff — флаг выключен: ни записей, ни уведомлений (решение Софьи 1).</summary>
    public Task<Outcome> EvaluateAsync(string ownerId) => EvaluateAsync(ownerId, DateTimeOffset.UtcNow);

    /// <summary>Оценка с ятивным «сейчас»: окна [now−7д, now] и [now−30д, now] проверяются
    /// на границе детерминированно, без гонки с часами процесса (internal — для теста).</summary>
    internal async Task<Outcome> EvaluateAsync(string ownerId, DateTimeOffset now)
    {
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true)
            return new(RunState.FeatureOff, []);

        var sem = _evalLocks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1));
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            return await EvaluateBodyAsync(ownerId, now).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<Outcome> EvaluateBodyAsync(string ownerId, DateTimeOffset now)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var (settings, invalid) = WikiSettingsStore.Read(root);
        if (invalid)
            SignalInvalidSettings(root);

        var byId = new Dictionary<string, WikiPackage>(StringComparer.Ordinal);
        var opsByClass = new Dictionary<string, List<OpRef>>(StringComparer.Ordinal);
        foreach (var pkg in WikiPackageStore.List(root).Packages)
        {
            byId[pkg.Id] = pkg;
            // «Применённые операции» — из применённого и откатанного пакета: откатаный был
            // применён, и его откат обязан остаться в окне, которое его породил (иначе доля
            // окна не смогла бы посчитать «5 откатов из 50 применённых»).
            if (pkg.Status is not (WikiPackage.StatusApplied or WikiPackage.StatusReverted))
                continue;
            // Пакеты человека политику не проходят — классы human×* автоматикой не оцениваются.
            if (pkg.Author == WikiPackage.AuthorHuman)
                continue;
            for (var i = 0; i < pkg.Operations.Count; i++)
            {
                var op = pkg.Operations[i];
                if (!WasApplied(op) || !WikiSettingsStore.IsValidClassKey(op.Class))
                    continue;
                if (!opsByClass.TryGetValue(op.Class, out var list))
                    opsByClass[op.Class] = list = [];
                list.Add(new OpRef(pkg.Id, i, pkg.CreatedAt));
            }
        }

        var retracted = RetractedOperations(root, byId);

        var sevenAgo = now - SevenDays;
        var thirtyAgo = now - ThirtyDays;

        var stats = new List<ClassStat>();
        var toDisable = new List<(string Class, string Reason)>();

        // Порядок классов фиксированный: строка метрик и ответ GET — данные, их diff между
        // прогонами не должен зависеть от обхода словаря.
        foreach (var classKey in opsByClass.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var ops = opsByClass[classKey];
            var windowStart = settings.WindowStart(classKey);
            var afterStart = ops
                .Where(o => windowStart is null || o.CreatedAt > windowStart)
                .OrderByDescending(o => o.CreatedAt)
                .ThenBy(o => o.PkgId, StringComparer.Ordinal)
                .ToList();
            if (afterStart.Count == 0)
                continue; // окно после «Снова применять само» пусто — мерить нечего

            var window = afterStart.Take(WindowSize).ToList();
            var retractedInWindow = window.Count(o => retracted.ContainsKey(o.Key));
            var windowShare = (double)retractedInWindow / window.Count;
            var retracts7d = afterStart.Count(o =>
                retracted.TryGetValue(o.Key, out var t) && t >= sevenAgo);

            var alreadyDisabled = settings.IsClassDisabled(classKey);
            if (!alreadyDisabled)
            {
                if (windowShare * 100.0 >= RetractSharePercent)
                    toDisable.Add((classKey,
                        $"{retractedInWindow} откатов из {window.Count} последних операций "
                        + $"({Pct(windowShare)} %) при пороге {RetractSharePercent} %"));
                else if (retracts7d >= RetractSevenDayMin)
                    toDisable.Add((classKey,
                        $"{retracts7d} откатов за 7 дней при пороге {RetractSevenDayMin}"));
            }

            var applied7 = afterStart.Count(o => o.CreatedAt >= sevenAgo);
            var applied30 = afterStart.Count(o => o.CreatedAt >= thirtyAgo);
            var retr7 = afterStart.Count(o => o.CreatedAt >= sevenAgo && retracted.ContainsKey(o.Key));
            var retr30 = afterStart.Count(o => o.CreatedAt >= thirtyAgo && retracted.ContainsKey(o.Key));
            stats.Add(new ClassStat(
                classKey, alreadyDisabled || toDisable.Any(d => d.Class == classKey),
                applied7, retr7, Pct(applied7 == 0 ? 0 : (double)retr7 / applied7),
                applied30, retr30, Pct(applied30 == 0 ? 0 : (double)retr30 / applied30)));
        }

        // Выключение и уведомление — до метрик: строка метрик обязана показать состояние,
        // реально оставшееся на диске после merge (в гонке с «Снова применять само» человека
        // правкой является запись настроек, а не решение этой оценки).
        var applied = await ApplyDisablesAsync(ownerId, settings, toDisable, now).ConfigureAwait(false);
        if (applied.Count != toDisable.Count)
        {
            var final = WikiSettingsStore.Read(root).Settings;
            for (var i = 0; i < stats.Count; i++)
                if (toDisable.Any(d => d.Class == stats[i].Class))
                    stats[i] = stats[i] with { Disabled = final.IsClassDisabled(stats[i].Class) };
        }
        if (stats.Count > 0)
            AppendMetricsLine(root, stats, now);

        return new(RunState.Ran, stats);
    }

    /// <summary>Выключенные операции: строки op=retract (пакеты × затронутые страницы —
    /// мера 5/7) и пакеты со статусом «reverted» (мера 4). Ключ — (пакет, индекс операции):
    /// задетая двумя записями операция остаётся откатанной один раз.</summary>
    private static Dictionary<OpRef.OpKey, DateTimeOffset> RetractedOperations(
        string root, Dictionary<string, WikiPackage> byId)
    {
        var retracted = new Dictionary<OpRef.OpKey, DateTimeOffset>();

        foreach (var line in ReadLogRows(root, LogOpRetract))
        {
            if (line.Packages is null || line.Packages.Count == 0)
                continue;
            var pages = new HashSet<string>(line.Pages ?? [], StringComparer.Ordinal);
            foreach (var pkgId in line.Packages)
            {
                if (!byId.TryGetValue(pkgId, out var pkg))
                    continue; // id из журнала старше стора (пакет не удаляется) — измерить нечем
                for (var i = 0; i < pkg.Operations.Count; i++)
                {
                    var op = pkg.Operations[i];
                    if (!WasApplied(op) || !pages.Contains(op.Page))
                        continue;
                    retracted.TryAdd(new OpRef.OpKey(pkgId, i), line.Ts);
                }
            }
        }

        foreach (var pkg in byId.Values)
        {
            if (pkg.Status != WikiPackage.StatusReverted)
                continue;
            for (var i = 0; i < pkg.Operations.Count; i++)
            {
                if (WasApplied(pkg.Operations[i]))
                    retracted.TryAdd(new OpRef.OpKey(pkg.Id, i), pkg.UpdatedAt);
            }
        }

        // «Вернуть мою» (ADR-038, «Стык с автовыключением классов»): человек отверг автозамену
        // своей формулировки — откат ТОЛЬКО для операций класса с хвостом @human. Строка
        // op=restore (actor user) пакетов не называет, только страницу: каждая строка снимает
        // одну — последнюю не откатанную до неё — применённую операцию «…@human» на этой странице.
        // Классы без хвоста (замены ИИ) и страницы без таких операций сигналом не задеваются.
        var humanOps = new List<(OpRef.OpKey Key, string Page, DateTimeOffset CreatedAt)>();
        foreach (var pkg in byId.Values)
        {
            if (pkg.Status is not (WikiPackage.StatusApplied or WikiPackage.StatusReverted))
                continue;
            for (var i = 0; i < pkg.Operations.Count; i++)
            {
                var op = pkg.Operations[i];
                if (WasApplied(op) && WikiPackage.ParseClassKey(op.Class) is { HumanTarget: true })
                    humanOps.Add((new OpRef.OpKey(pkg.Id, i), op.Page, pkg.CreatedAt));
            }
        }
        if (humanOps.Count > 0)
        {
            foreach (var row in ReadLogRows(root, LogOpRestore).Where(r => r.Actor == LogActorUser).OrderBy(r => r.Ts))
            {
                foreach (var page in row.Pages ?? [])
                {
                    var hit = humanOps
                        .Where(o => o.Page == page && o.CreatedAt <= row.Ts && !retracted.ContainsKey(o.Key))
                        .OrderByDescending(o => o.CreatedAt)
                        .Select(o => ((OpRef.OpKey Key, string Page, DateTimeOffset CreatedAt)?)o)
                        .FirstOrDefault();
                    if (hit is { } h)
                        retracted[h.Key] = row.Ts;
                }
            }
        }

        return retracted;
    }

    private sealed record OpRef(string PkgId, int OpIdx, DateTimeOffset CreatedAt)
    {
        public OpKey Key => new(PkgId, OpIdx);
        public readonly record struct OpKey(string PkgId, int OpIdx);
    }

    /// <summary>«Что применилось» — ровно то же правило, что у шага 6 применения
    /// (WikiApplyService.Applies): «auto» всегда, «inbox» — только принятое человеком.</summary>
    private static bool WasApplied(WikiPackage.Operation op) =>
        op.Decision == WikiPackage.DecisionAuto ||
        (op.Decision == WikiPackage.DecisionInbox && op.HumanDecision == WikiPackage.HumanAccepted);

    /// <summary>Выключение — через merge стора: решение могло опоздать за время оценки.
    /// Merge перечитывает состояние под замком и пропускает выключение, если класс уже
    /// выключен или окно сдвинулось («Снова применять само» во время оценки) — окно, по которому
    /// решали, перестал быть текущим. Возвращает классы, реально выключенные этим вызовом:
    /// только на них и приходит уведомление (переход «включён → выключен», не тик).</summary>
    private async Task<List<string>> ApplyDisablesAsync(
        string ownerId, WikiSettingsStore.Settings settingsBefore, List<(string Class, string Reason)> toDisable, DateTimeOffset now)
    {
        if (toDisable.Count == 0)
            return [];

        var applied = new List<string>();
        WikiSettingsStore.Mutate(_dataDir, ownerId, current =>
        {
            var classes = new List<WikiSettingsStore.ClassState>(current.Classes);
            var changed = false;
            foreach (var (classKey, reason) in toDisable)
            {
                if (current.IsClassDisabled(classKey))
                    continue;
                if (current.WindowStart(classKey) != settingsBefore.WindowStart(classKey))
                    continue;
                var prev = classes.Find(c => c.Class == classKey);
                classes.RemoveAll(c => c.Class == classKey);
                // ResumedAt прежней записи сохраняется: окно класса не «обнуляется» молча при
                // выключении — оно обнуляется только человеком («Снова применять само»).
                classes.Add(prev is not null
                    ? prev with { Disabled = true, Reason = reason, DisabledAt = now }
                    : new WikiSettingsStore.ClassState(classKey, true, reason, now, null));
                applied.Add(classKey);
                changed = true;
            }
            return changed
                ? current with { Classes = classes.OrderBy(c => c.Class, StringComparer.Ordinal).ToList() }
                : current;
        });

        foreach (var classKey in applied)
        {
            if (_notifications is null)
                continue; // диспетчера в этом составе нет (тест/сборка без шва): решение осталось, сигнала нет
            var reason = toDisable.First(d => d.Class == classKey).Reason;
            try
            {
                await _notifications.SendAsync(ownerId, new CreateNotificationRequest
                {
                    Kind = "warning",
                    Type = "wiki_class_disabled",
                    Title = $"Класс «{classKey}» выключен в самоприменении",
                    Body = reason + ". Его правки идут во «Входящие», пока вы не вернёте его "
                        + "кнопкой «Снова применять само».",
                    // Ссылка — вкладка «Вики» → «Настройки» → блок «Применение» (маршрут S7a).
                    Url = "#/wiki",
                    Source = "wiki",
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Класс уже выключен и запись согласована: сбой доставки не отменяет решение
                // и не рвёт оценку. Он назван в логе — молчания нет.
                _log?.LogWarning(ex, "уведомление об автовыключении класса «{Class}» не доставлено", classKey);
            }
        }

        return applied;
    }

    // ─── журнал ───────────────────────────────────────────────────────────────

    private sealed record LogRow(
        DateTimeOffset Ts, string Op, IReadOnlyList<string>? Pages, IReadOnlyList<string>? Packages,
        string? Actor = null);

    private static readonly JsonSerializerOptions LogJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Строки журнала с заданным op этого владельца (чужие песочницы не читаются — путь per-owner).
    /// Нечитаемая строка пропускается: журнал append-only, половина строки после падения не
    /// должна останавливать измерение; целые строки от этого не меняются.</summary>
    private static List<LogRow> ReadLogRows(string root, string op)
    {
        var full = SafePath.Join(root, "keeper/log.jsonl");
        if (!File.Exists(full))
            return [];
        var rows = new List<LogRow>();
        try
        {
            foreach (var line in File.ReadLines(full, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                LogRow? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<LogRow>(line, LogJsonOpts);
                }
                catch (JsonException) { continue; }
                if (parsed is not null && parsed.Op == op)
                    rows.Add(parsed);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return rows;
    }

    // ─── метрики (мера 8) ─────────────────────────────────────────────────────

    private sealed record MetricsLine(
        DateTimeOffset Ts, string CodeVersion, string Kind,
        IReadOnlyList<ClassStat> Classes,
        IReadOnlyList<OriginStat> Origins);

    private sealed record OriginStat(
        string Origin, int Applied7, int Retracted7, double Share7, int Applied30, int Retracted30, double Share30);

    private static readonly JsonSerializerOptions MetricsJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Доля → процент с одним знаком — ровно формула метрик S3-3/базовой линии.
    /// Пустой знаменатель даёт 0.</summary>
    private static double Pct(double share) => Math.Round(100.0 * share, 1);

    /// <summary>Строка метрик доли откатов по классам и источникам за 7 и 30 дней — append в
    /// keeper/metrics.jsonl (тот же приём, что у метрик линта: дописывание строки, не
    /// перезапись файла; дописывание идёт под замком этой оценки). Пустого замера нет: без
    /// применённых операций класса мерить нечего и строка была бы шумом.</summary>
    private void AppendMetricsLine(string root, List<ClassStat> stats, DateTimeOffset now)
    {
        var origins = stats
            .GroupBy(s => s.Class.Split('×')[0], StringComparer.Ordinal)
            .Select(g => new OriginStat(
                g.Key,
                g.Sum(s => s.Applied7), g.Sum(s => s.Retracted7),
                Pct(g.Sum(s => s.Applied7) == 0 ? 0 : (double)g.Sum(s => s.Retracted7) / g.Sum(s => s.Applied7)),
                g.Sum(s => s.Applied30), g.Sum(s => s.Retracted30),
                Pct(g.Sum(s => s.Applied30) == 0 ? 0 : (double)g.Sum(s => s.Retracted30) / g.Sum(s => s.Applied30))))
            .OrderBy(o => o.Origin, StringComparer.Ordinal)
            .ToList();

        var line = JsonSerializer.Serialize(
            new MetricsLine(now, CodeVersion, "retractShare", stats, origins), MetricsJsonOpts);
        var full = SafePath.Join(root, "keeper/metrics.jsonl");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.AppendAllText(full, line + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // Метрика — наблюдение, не транзакция: её сбой не отменяет выключение класса
            // (запись settings уже состоялась до этой строки).
            _log?.LogWarning(ex, "строка метрик доли откатов не записана");
        }
    }

    /// <summary>Сигнал о невалидном settings.json (ADR-035:325): строка в _health.md.
    /// Настройки — уровень владельца, health — файл раздела; сигналом держится health «Общего»
    /// — постоянного раздела любой вики (ADR-036, решение 1). Его файла на диске нет (раздел
    /// ещё не заводился) — писать некуда: молчание здесь сознательное, падение — нет.</summary>
    internal static void SignalInvalidSettings(string root)
    {
        var folder = WikiSections.Folder(WikiSections.Common);
        if (folder is null) return;
        var full = SafePath.Join(root, "pages/" + folder + WikiKeeper.HealthFileName);
        if (!File.Exists(full)) return;
        try
        {
            File.AppendAllText(full,
                "\nНастройки keeper/settings.json нечитаемы: действуют умолчания. "
                + "Почините или удалите файл.\n", new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
