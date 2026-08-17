using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm;

// Расход одного вызова: токены по видам, стоимость и модель, которой он реально
// посчитан. Вход разбит на виды — они тарифицируются по-разному (cache read дешевле).
public sealed record OneShotUsage(
    long InputTokens, long CacheCreationTokens, long CacheReadTokens, long OutputTokens,
    double? CostUsd, string? Model)
{
    public long TotalInputTokens => InputTokens + CacheCreationTokens + CacheReadTokens;
}

// Ответ вызова вместе с расходом. Usage = null, если CLI метрик не дал
// (нераспознанный формат ответа) — потребитель должен это пережить.
public sealed record OneShotResult(string Text, OneShotUsage? Usage, long DurationMs);

// Отказ one-shot вызова по таймауту. Наследник InvalidOperationException — потребители,
// ловящие её, ведут себя как раньше; отдельный тип нужен, чтобы CheapTextRunner мог
// сделать ОДИН повтор (обрыв по таймауту — не приговор), а человек увидел честную
// причину отказа вместо «уточните задачу». Базовое сообщение обязано сохранять подстроку
// «не ответил за отведённое время»: по ней ChangelogService.DescribeFailure различает
// таймаут и сбой CLI. Вариант с деталями (TimeoutMessage) называет применённый лимит
// и фактическую длительность — без них лог места («модель не ответила») не разбирается.
public sealed class LlmTimeoutException(string? message = null)
    : InvalidOperationException(message ?? "AI не ответил за отведённое время");

// Абстракция one-shot вызова LLM — для мокирования в тестах.
// В DI интерфейс указывает на тот же singleton OneShotClaudeRunner.
public interface IOneShotRunner
{
    // Модель ненастроенного провайдера тихо заменяется дефолтом claude
    string? NormalizeModel(string? model);

    // ownerId — владелец вызова: его среда исполнения определяет, где запустится claude
    // (локально или в песочнице). null — системный вызов, всегда локально.
    // effort — усилие рассуждения (--effort), для моделей с его поддержкой.
    // label — подпись операции для аналитики расхода (ключ фонового действия).
    Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null, string? label = null);

    // То же, но с расходом вызова — для мест, которые показывают пользователю цену генерации.
    Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null, string? label = null);
}

// Общий раннер одноразовых вызовов claude --print (без сессии): промпт через stdin,
// ответ — stdout целиком. Модель стороннего провайдера подключается env-оверрайдами
// (LlmProviderRegistry.BuildCliEnv). Рабочая папка — пустая temp (claude не получает
// доступ к файлам). Используется сводками «Что нового» (ChangelogService),
// генерациями задач и заметок, персонами (ask/характер).
public sealed class OneShotClaudeRunner(LlmProviderRegistry llmProviders, ILauncherFactory launchers,
    IConfiguration config, Spend.ISpendCollector? spend = null,
    AppSettingsService? appSettings = null,
    UserModelTierResolver? userTiers = null,
    ClaudeSubscriptionPool? subscriptionPool = null,
    SubscriptionActivityTracker? activity = null) : IOneShotRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Рубильник на случай CLI старее флага --no-session-persistence (см. BuildArgs): CLI
    // валидирует аргументы и падает с кодом 1 на незнакомом, а образ песочницы может нести
    // версию старее хостовой. true — вернуть прежнее поведение с записью транскриптов.
    private readonly bool _persistSessions = config.GetValue("Claude:PersistOneShotSessions", false);

    // Авто-деградация: CLI не игнорирует незнакомый аргумент, а падает с кодом 1. Образ
    // песочницы собирается отдельно от хоста и может нести версию без флага — тогда КАЖДЫЙ
    // фоновый вызов container-пользователя обрывался бы, а наружу это выглядит как «сводки не
    // генерятся» при живом сервере (так уже было с мертвым MultiEdit в deny-правилах). Первый
    // такой отказ переводит раннер в режим без флага до конца жизни процесса: работающие
    // фоновые задачи важнее экономии файлов. Рубильник в конфиге остается ручным дублером.
    private static volatile bool _flagUnsupported;

    // Отказ именно из-за нашего флага, а не по другой причине (не логин, не таймаут).
    // internal — покрыто тестом: строку ошибки CLI руками в тесте не воспроизвести иначе.
    internal static bool LooksLikeUnknownSessionFlag(string detail) =>
        detail.Contains("no-session-persistence", StringComparison.OrdinalIgnoreCase)
        && (detail.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unexpected", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("не поддерж", StringComparison.OrdinalIgnoreCase));

    // Модель ненастроенного провайдера тихо заменяется дефолтом claude —
    // генерация не должна падать из-за отсутствующего ключа
    public string? NormalizeModel(string? model) =>
        llmProviders.ResolveByModel(model) is { Enabled: false } ? null : model;

    // Итоговая модель вызова: не задана вызывающим = слот «средняя» (генеричный one-shot
    // без контекста места; вызовы С контекстом резолвят модель у себя — CheapTextRunner по
    // маршруту действия, PersonaAskService по месту «чат с персоной»). NormalizeModel — ПОСЛЕ
    // подстановки: слот тоже может указывать на модель провайдера без ключа, и такой вызов
    // обязан деградировать в дефолт CLI, а не падать.
    // internal — точка подстановки покрыта тестом без запуска процесса.
    internal string? ResolveModel(string? model, string? ownerId = null) =>
        NormalizeModel(string.IsNullOrWhiteSpace(model)
            ? userTiers?.ModelFor(ModelTier.Medium, ownerId) ?? appSettings?.TierModel(ModelTier.Medium)
            : model);

    // Суффикс [1m] тир-алиаса остаётся, пока в пуле есть живой кандидат с поддержкой 1M-окна;
    // иначе срезается (деградация в 200K). Без пула — срезаем безусловно (безопасный 200K).
    private string? ResolveWindowAlias(string? model) =>
        subscriptionPool?.ResolveWindowAlias(model) ?? LlmProviderRegistry.StripClaudeWindowAlias(model);

    // Env процесса + ключ аккаунта пула, которым реально пойдёт вызов (null — сторонний
    // провайдер ИЛИ пул пуст/недоступен, тогда возвращённый Env тоже null — CLI наследует
    // окружение сервера, как раньше). Родная модель Claude (BuildCliEnv не нашёл стороннего
    // провайдера) при непустом пуле подписок выбирает аккаунт так же, как это делает живой
    // чат (ClaudeSession) — иначе фон всегда бил бы в основной аккаунт мимо пула (диагноз
    // задачи). internal — тестируется без запуска процесса (подменить его в тестах нечем).
    internal (IReadOnlyDictionary<string, string>? Env, string? PoolSubKey) ResolveEnv(string? model)
    {
        var env = llmProviders.BuildCliEnv(model);
        if (env is not null || subscriptionPool?.HasExtra != true)
            return (env, null);

        var subKey = subscriptionPool.Pick(model);
        var sub = subscriptionPool.All.FirstOrDefault(s => s.Key == subKey);
        var oauthEnv = sub is not null
            ? llmProviders.BuildOAuthCliEnv(sub.Key, sub.OAuthToken, sub.ApiKey, model)
            : null;
        return oauthEnv is not null ? (oauthEnv, sub!.Key) : (null, null);
    }

    public async Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null, string? label = null) =>
        (await RunCliAsync(prompt, model, timeout, ct, ownerId, effort, label)).Text;

    public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null, string? label = null) =>
        RunCliAsync(prompt, model, timeout, ct, ownerId, effort, label);

    // Формат всегда json: раньше текстовый путь шёл без него, но аналитике расхода нужен
    // usage КАЖДОГО вызова, а его отдаёт только json-ответ. Потребители RunAsync по-прежнему
    // получают чистый текст (result из json).
    private async Task<OneShotResult> RunCliAsync(string prompt, string? model,
        TimeSpan? timeout, CancellationToken ct, string? ownerId, string? effort, string? label)
    {
        var launcher = launchers.ForOwner(ownerId);
        var workDir = Path.Combine(launcher.HostTempDir, "claude-oneshot");
        Directory.CreateDirectory(workDir);

        // Модель не задана вызывающим = «по умолчанию»: подставляем глобальную настройку.
        // ДО BuildArgs и BuildCliEnv — env маршрутизации обязан считаться от итоговой модели,
        // иначе glm/kimi из настройки уехали бы на эндпоинт Anthropic
        model = ResolveModel(model, ownerId);
        // Резолв окна [1m] — тоже до BuildArgs/ResolveEnv: и --model, и выбор подписки пула
        // (Pick) должны сходиться на одной модели (иначе Pick по opus[1m] выбрал бы 1M-аккаунт,
        // а --model ушёл бы срезанным opus, или наоборот).
        model = ResolveWindowAlias(model);

        var withFlag = !_persistSessions && !_flagUnsupported;
        var args = BuildArgs(Claude.ClaudeRuntimeSettings.HooksOffArgs(launcher),
            safeMode: !launcher.IsSandboxed, persistSessions: !withFlag, model, effort);

        var (env, poolSubKey) = ResolveEnv(model);

        // Отсчёт простоя — от ПОПЫТКИ хода, а не от успеха (как идл-пинг): это фактическая
        // активность аккаунта, идл-пинг по нему до следующего порога не нужен.
        if (poolSubKey is not null)
            activity?.Touch(poolSubKey);

        var turnId = Guid.NewGuid().ToString("N")[..12];
        using var process = launcher.Start(new ProcessSpec
        {
            FileName = launcher.ClaudeCliCommand,
            Args = args,
            WorkingDirectory = workDir,
            Env = env,
            // Как и в ходе чата: системные ANTHROPIC_* машины не должны переопределять
            // маршрут фоновых задач (сводки, теги, память) — см. ProviderEnvKeys
            ClearEnv = llmProviders.EnvKeysToClear,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = turnId,
        });

        // Чтение вывода запускаем ДО записи промпта — иначе на большом промпте
        // возможен deadlock на заполненных пайпах
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            started.Stop();

            if (process.ExitCode != 0)
            {
                // Причину CLI пишет не только в stderr: «Not logged in · Please run /login»
                // уходит в stdout при пустом stderr. Раньше её тут теряли, и в логах всех
                // сервисов оставалось «завершился с кодом 1:» без объяснения.
                var detail = stderr.Trim();
                if (detail.Length == 0) detail = ErrorDetail(stdout.Trim());
                // Единственный аргумент, которого может не знать более старый CLI (образ
                // песочницы живет своей жизнью) — снимаем его и повторяем вызов один раз,
                // вместо того чтобы уронить фоновую задачу
                if (withFlag && LooksLikeUnknownSessionFlag(detail))
                {
                    _flagUnsupported = true;
                    Console.Error.WriteLine(
                        "[OneShotClaudeRunner] CLI не знает --no-session-persistence — повторяю без него. " +
                        "Транскрипты one-shot снова будут копиться; лечится обновлением claude в образе песочницы");
                    return await RunCliAsync(prompt, model, timeout, ct, ownerId, effort, label);
                }
                if (detail.Length > 500) detail = detail[..500] + "…";
                throw new InvalidOperationException(
                    $"claude завершился с кодом {process.ExitCode}: {detail}");
            }

            // Время меряем по своим часам, а не по duration_ms от CLI: пользователь ждёт
            // весь вызов вместе со стартом процесса (~5-15 с), а не только запрос к API
            var result = ParseJsonResult(stdout, model, started.ElapsedMilliseconds);
            RecordSpend(result, model, ownerId, label, poolSubKey);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена пришла СНАРУЖИ (у icon/suggest это RequestAborted: фронт рвёт HTTP
            // раньше нашего лимита), а не по нашему таймауту — честно называем это в
            // сообщении, иначе «лимит 180 с, ждали 30 с» читается как ложный таймаут
            launcher.Kill(process, turnId);
            throw new LlmTimeoutException(
                $"ход отменён вызывающим после {FmtSec(started.Elapsed)} (лимит был {FmtSec(timeout ?? DefaultTimeout)})");
        }
        catch (OperationCanceledException)
        {
            // Обрыв по нашему лимиту: применённое значение и фактическая длительность —
            // в сообщение, их подхватывают логи мест (CheapTextRunner, ProjectIconGlyphService)
            launcher.Kill(process, turnId);
            throw new LlmTimeoutException(TimeoutMessage(timeout, started.Elapsed));
        }
    }

    // Сообщение отказа по времени: применённый лимит + сколько фактически ждали.
    // Подстрока «не ответил за отведённое время» — контракт ChangelogService.DescribeFailure.
    // internal — тестируется напрямую, без запуска процесса.
    internal static string TimeoutMessage(TimeSpan? timeout, TimeSpan elapsed)
    {
        var limit = timeout ?? DefaultTimeout;
        return $"AI не ответил за отведённое время (лимит {FmtSec(limit)}, ждали {FmtSec(elapsed)})";
    }

    // Числа — инвариантной культурой: в ru-RU «0.#» даёт запятую, и по логам место
    // отказа невозможно grep'ать стабильно (та же грабля, что ADR-008 §4.3)
    private static string FmtSec(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " с";

    // Аргументы запуска claude для one-shot. Вынесены из RunCliAsync отдельным методом,
    // чтобы состав флагов проверялся тестом без запуска процесса.
    // hooksOffArgs — готовые --settings (хуки плагинов не нужны и плодят окна консоли на
    // хосте; скиллы one-shot не зовет). Нужны и при safeMode: в песочнице флага нет, а
    // хуки отключить все равно надо.
    internal static List<string> BuildArgs(IEnumerable<string> hooksOffArgs,
        bool safeMode, bool persistSessions, string? model, string? effort)
    {
        // Формат всегда json: usage нужен аналитике расхода на КАЖДОМ вызове
        var args = new List<string> { "--print", "--output-format", "json" };
        args.AddRange(hooksOffArgs);
        // Транскрипт one-shot мертв с рождения: --resume по нему никто не делает, а CLI
        // писал по файлу на вызов (замер: ~287 файлов за сутки на одном инстансе) и держал
        // их до своей плановой уборки (~30 дней). Флаг работает только вместе с --print —
        // ровно наш режим. Ходов чата это НЕ касается: там транскрипт и есть память
        // разговора, которую читает --resume. Рубильник Claude:PersistOneShotSessions
        // возвращает прежнее поведение, если CLI окажется старее флага.
        if (!persistSessions) args.Add("--no-session-persistence");
        // --safe-mode: CLI не тянет пользовательские кастомизации (~/.claude/CLAUDE.md
        // с правилами, скиллы, плагины, хуки, MCP) в системный промпт. One-shot — чистая
        // генерация текста, юзерский контекст ей не нужен, а стоил он ~половину входа
        // (замер на 2.1.207: 31.6 тыс. → 15.3 тыс. токенов обвязки), и личные правила
        // пользователя протекали в тон продуктовых текстов. CLAUDE_CONFIG_DIR так не
        // умеет (память CLI грузит мимо него), --bare ломает OAuth-авторизацию
        // (пропускает чтение кредов). Только локально: флаг появился в CLI 2.1.169,
        // песочница может нести версию старее — там не рискуем.
        if (safeMode) args.Add("--safe-mode");
        // Инструменты жестко выключены. Это контракт (пустая temp-cwd и раньше
        // подразумевала «без файлов», но не мешала Read по абсолютному пути) и защита
        // от инъекции в промпт — в т.ч. когда вызов сделан от имени изолированного
        // пользователя, а процесс работает на хосте. Skill дополнительно отключает
        // инжекцию каталога скиллов в системный промпт (~3 тыс. токенов), когда
        // safe-mode недоступен (песочница).
        args.Add("--disallowedTools");
        // MultiEdit тут был до CLI 2.1.x: инструмент убрали без прямой замены (он батчил
        // несколько правок одного файла в одно одобрение; теперь это просто отдельные вызовы
        // Edit), а новый CLI ВАЛИДИРУЕТ имена и падает с кодом 1 на неизвестном правиле, роняя
        // весь one-shot. Не возвращать несуществующие имена — список сверять с набором CLI.
        args.Add("Bash,Read,Write,Edit,NotebookEdit,Glob,Grep,WebFetch,WebSearch,Task,Agent,KillShell,BashOutput,Skill");
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            // Модель уже финальна: резолв окна [1m] по способности пула делает вызывающий
            // (RunCliAsync), BuildArgs только подставляет её как есть.
            args.Add(model!);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.Add("--effort");
            args.Add(effort);
        }
        return args;
    }

    // Запись расхода one-shot вызова в аналитику. Источник — one-shot; модель ":free"
    // (агрегатор через CLI) выделяется источником free. Ошибка записи вызов не роняет.
    // poolSubKey — ключ аккаунта пула подписок, которым реально ходили (см. RunCliAsync);
    // null — сторонний провайдер ИЛИ пул пуст/недоступен, тогда провайдер — как раньше.
    // internal — тестируется напрямую с готовым OneShotResult, без запуска процесса.
    internal void RecordSpend(OneShotResult result, string? model, string? ownerId, string? label, string? poolSubKey)
    {
        if (spend is null || result.Usage is not { } u) return;
        try
        {
            // Как и у живого чата (SessionManager.RecordTurnSpend по Session.Provider):
            // аналитика должна знать, КАКОЙ аккаунт подписки потратил токены, а не всегда "claude".
            var provider = SpendSources.NormalizeProvider(poolSubKey ?? llmProviders.ProviderKey(model));
            var usedModel = llmProviders.ResolveModelOrDefault(u.Model ?? model, provider);
            spend.Record(new SpendRecord
            {
                OwnerId = ownerId ?? "",
                Provider = provider,
                Model = usedModel,
                Source = SpendSources.IsFree(provider, usedModel)
                    ? SpendSources.Free : SpendSources.OneShot,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CacheReadTokens = u.CacheReadTokens,
                CacheCreationTokens = u.CacheCreationTokens,
                CostUsd = u.CostUsd,
                DurationMs = result.DurationMs,
                Label = label,
            });
        }
        catch { /* аналитика не должна ронять генерацию */ }
    }

    // Причина ошибки лежит в поле result json-ответа, а не голым текстом —
    // достаём её, чтобы в логи и degraded-подпись не уезжала простыня JSON
    private static string ErrorDetail(string stdout)
    {
        if (stdout.Length == 0) return stdout;
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            var text = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? stdout : text!;
        }
        catch { return stdout; }
    }

    // Ответ CLI в json: { result, total_cost_usd, modelUsage: { "<model>": {…} }, usage: {…} }.
    // Метрики берём из modelUsage — это агрегат по всем итерациям ответа, тогда как usage
    // описывает только последнюю (на длинных ответах расходятся в разы).
    private OneShotResult ParseJsonResult(string stdout, string? model, long durationMs)
    {
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            var text = (root.TryGetProperty("result", out var r) ? r.GetString() : null) ?? "";

            long input = 0, cacheCreate = 0, cacheRead = 0, output = 0;
            double? cliCost = null;
            string? usedModel = null;

            if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            {
                foreach (var m in mu.EnumerateObject())
                {
                    usedModel ??= m.Name;
                    input += Num(m.Value, "inputTokens");
                    cacheCreate += Num(m.Value, "cacheCreationInputTokens");
                    cacheRead += Num(m.Value, "cacheReadInputTokens");
                    output += Num(m.Value, "outputTokens");
                }
            }
            else if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                input = Num(u, "input_tokens");
                cacheCreate = Num(u, "cache_creation_input_tokens");
                cacheRead = Num(u, "cache_read_input_tokens");
                output = Num(u, "output_tokens");
            }

            if (root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cost))
                cliCost = cost;

            // На стороннем эндпоинте CLI считает стоимость по ценам Anthropic — пересчитываем
            // по ценам конфига (та же логика, что у ходов сессии). Для родного Claude
            // ComputeCost возвращает null, и остаётся оценка CLI.
            var usage = new Protocol.UsageInfo((int)input, (int)output, (int)cacheRead, (int)cacheCreate);
            var finalCost = llmProviders.ComputeCost(model, usage) ?? cliCost;

            return new OneShotResult(text.Trim(),
                new OneShotUsage(input, cacheCreate, cacheRead, output, finalCost, usedModel ?? model),
                durationMs);
        }
        catch
        {
            // Формат ответа не распознан — отдаём как есть, без метрик: генерация важнее цифр
            return new OneShotResult(stdout.Trim(), null, durationMs);
        }
    }

    private static long Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
