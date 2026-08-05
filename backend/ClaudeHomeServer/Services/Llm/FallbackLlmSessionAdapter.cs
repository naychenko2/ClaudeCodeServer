using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Фолбэк выбора модели при рантайм-ошибках доставки (ADR «Порядок резолва модели,
// классы ошибок фолбэка, защита от зацикливания» §2–4). Декоратор адаптера сессии:
// видит ВСЕ события хода через перехват OnMessage и при ошибке фолбэк-класса
// перезапускает ход на другой паре «модель × подписка»:
//
//   Уровень 1 — та же модель, другая подписка ТОГО ЖЕ пула: существующие механизмы
//               ротации (MarkExhausted → Pick → перенос транскрипта), новых не заводится;
//   Уровень 2 — следующий сторонний провайдер цепочки (модель-эквивалент по слоту).
//
// Защита от зацикливания: каждая пара пробуется не более одного раза за ход
// (HashSet попыток), потолок подмен задаётся через FallbackSettingsStore
// (per-owner → global → дефолт), значение клампится в 1..HardMaxSubstitutions.
//
// SessionManager о подменах не знает: терминальные сообщения неудачных попыток
// задерживаются здесь, наружу уходит только финальный исход — статус, аккумулятор
// истории и наблюдатели задач видят ход одним целым. При исчерпании цепочки финал
// всегда ошибочный result: у задачи исполнителя существующий путь TaskExecutionService
// помечает сбой и уведомляет постановщика, а след перепробованных пар остаётся
// в ленте чата (ErrorMessage + маркеры provider_switched).
public sealed class FallbackLlmSessionAdapter : ILlmSessionAdapter
{
    private readonly ILlmSessionAdapter _inner;
    private readonly Func<string?> _effectiveModel;
    private readonly Func<ServerMessage, Task> _downstream;
    private readonly ClaudeSubscriptionPool _pool;
    private readonly LlmProviderRegistry? _providers;
    private readonly UserModelTierResolver? _tiers;
    private readonly string _rootPath;
    private readonly Execution.IProcessLauncher? _launcher;
    // Стор настроек фолбэка. Жёсткий потолок (FallbackSettingsStore.HardMaxSubstitutions)
    // сохраняется; конкретное значение читается per-owner → global → дефолт каждый ход
    // заново (админская правка в UI применяется без рестарта). null — в тестах без DI,
    // читаем дефолт напрямую.
    private readonly FallbackSettingsStore? _fallbackSettings;
    // Лог подмен: без него оркестрацию нечем отлаживать на стенде (что классифицировали,
    // куда переключились, почему кандидат отвергнут). null (тесты без DI) — пишем в
    // Console.Error, как делал код до появления логгера.
    private readonly ILogger? _log;
    // Корень профиля CLI на момент старта сессии (хостовый путь): источник для
    // переноса транскрипта и, у container-пользователя, способ вывести раскладку
    // песочных профилей (родитель = data/sandbox-profiles/{ownerId}).
    private readonly string? _initialProfileRoot;
    // Корень профиля ТЕКУЩЕЙ подписки/провайдера — обновляется каждой подменой
    private string? _profileRoot;
    private readonly CancellationTokenSource _cts = new();

    // Активная оркестрация фолбэка (null — сообщения проходят насквозь)
    private FallbackTurn? _turn;
    private readonly object _gate = new();
    private volatile bool _userInterrupted;
    // Снимок MessageCount перед ходом: оркестратор восстанавливает счётчик в конце,
    // чтобы повторы не раздували «сообщения пользователя» (одно user-сообщение = +1)
    private int _snapshotMessageCount;

    public FallbackLlmSessionAdapter(
        ILlmSessionAdapter inner,
        Func<string?> effectiveModel,
        Func<ServerMessage, Task> downstream,
        ClaudeSubscriptionPool pool,
        LlmProviderRegistry? providers,
        string rootPath,
        Execution.IProcessLauncher? launcher,
        string? initialProfileRoot,
        UserModelTierResolver? tiers = null,
        FallbackSettingsStore? fallbackSettings = null,
        ILogger? log = null)
    {
        _inner = inner;
        _effectiveModel = effectiveModel;
        _downstream = downstream;
        _pool = pool;
        _providers = providers;
        _rootPath = rootPath;
        _launcher = launcher;
        _initialProfileRoot = initialProfileRoot;
        _tiers = tiers;
        _fallbackSettings = fallbackSettings;
        _log = log;
        _profileRoot = initialProfileRoot ?? ResolveRootFor(CurrentProviderKey());
    }

    // Ход сейчас под фолбэк-оркестрацией. По этому признаку SessionManager отходит в
    // сторону на rate_limit_event: ротацией подписок владеет оркестратор (M1 — на одно
    // событие реагирует ровно один механизм, см. комментарий у обработчика RateLimitMessage).
    public bool FallbackTurnActive => _turn is not null;

    // Единая точка логирования: с DI — обычный ILogger, без него (тесты, ручная сборка
    // адаптера) — Console.Error, чтобы диагностика не пропадала совсем
    private void LogWarn(string message)
    {
        if (_log is not null) _log.LogWarning("[ModelFallback] {Message}", message);
        else Console.Error.WriteLine($"[ModelFallback] {message}");
    }

    private void LogInfo(string message) => _log?.LogInformation("[ModelFallback] {Message}", message);

    // Эффективный потолок подмен для владельца сессии: per-owner → global → дефолт 3
    // (FallbackSettingsStore.ClampMaxSubstitutions). Info.OwnerId может быть пуст у
    // проектных сессий без владельца — тогда читаем global-слой.
    private int EffectiveMaxSubstitutions() =>
        _fallbackSettings?.ResolveMaxSubstitutions(Info.OwnerId)
        ?? FallbackSettingsStore.DefaultMaxSubstitutions;

    public Session Info => _inner.Info;
    public LlmCapabilities Capabilities => _inner.Capabilities;
    public int CurrentTurnAgentDepth => _inner.CurrentTurnAgentDepth;
    public bool CurrentTurnSuppressTasksExecute => _inner.CurrentTurnSuppressTasksExecute;
    public bool HasLiveTurn => _inner.HasLiveTurn;

    public Task StartAsync() => _inner.StartAsync();
    public Task CompactAsync() => _inner.CompactAsync();
    public void RespondPermission(string requestId, string behavior) => _inner.RespondPermission(requestId, behavior);
    public void AnswerQuestion(string toolUseId, string updatedInputJson) => _inner.AnswerQuestion(toolUseId, updatedInputJson);
    public void RespondPlan(string requestId, bool approve, string? feedback) => _inner.RespondPlan(requestId, approve, feedback);
    public bool TrySetPermissionModeLive(ClaudeMode mode) => _inner.TrySetPermissionModeLive(mode);
    public bool TrySetModelLive(string model) => _inner.TrySetModelLive(model);

    public void Interrupt()
    {
        // Остановка пользователем — не ошибка доставки: фолбэк на этом ходу запрещён
        _userInterrupted = true;
        _inner.Interrupt();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _inner.DisposeAsync();
    }

    // Запуск хода с фолбэк-оркестрацией. Возвращаемся сразу (как базовый адаптер):
    // цикл живёт в фоне и держит ход в своих руках до финального исхода.
    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
        int agentDepth = 0, bool suppressTasksExecute = false)
    {
        lock (_gate)
        {
            // Оркестрация уже идёт (нештатно: SessionManager ставит ходы в очередь при
            // занятом чате) — второй цикл фолбэка не строим, просто делегируем
            if (_turn is not null)
                return _inner.SendMessageAsync(text, attachedPaths, agentDepth, suppressTasksExecute);
            _turn = new FallbackTurn();
        }
        _userInterrupted = false;
        _snapshotMessageCount = Info.MessageCount;
        var turn = _turn;
        _ = Task.Run(() => RunFallbackLoopAsync(turn, text, attachedPaths ?? [], agentDepth, suppressTasksExecute));
        return Task.CompletedTask;
    }

    // Перехват событий хода: терминальные сообщения попытки задерживаются до решения
    // оркестратора (провал+повтор / финал), остальные идут downstream сразу.
    internal Task HandleMessageAsync(ServerMessage msg)
    {
        var turn = _turn;
        if (turn is null) return _downstream(msg);

        switch (msg)
        {
            case ResultMessage res:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    // Result попытки задерживаем: при повторе он не должен завершить ход
                    // в SessionManager (статус/аккумулятор/наблюдатели задач), а при
                    // финале уйдёт наружу решением оркестратора
                    turn.NoteResult(res);
                    return Task.CompletedTask;
                }

            case ErrorMessage { ExpectResultFollows: true } em:
                // Текст API-ошибки провайдера ПЕРЕД result: показываем в ленте честно
                // (пользователь видит, что случилось) и запоминаем для классификации
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    turn.NoteErrorText(em.Text);
                }
                return _downstream(msg);

            case ErrorMessage em:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    turn.NoteErrorText(em.Text);
                    if (turn.SwallowCleanup) return Task.CompletedTask; // уборка прерванной ротацией попытки
                    turn.Hold(msg);
                    turn.ResolveAttempt(AttemptEndKind.FatalError);
                    return Task.CompletedTask;
                }

            case ExitedMessage:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    if (turn.SwallowCleanup) return Task.CompletedTask; // процесс убит ротацией — глотаем
                    turn.Hold(msg);
                    // Процесс умер без result — обрыв потока. Если попытка уже разрешена
                    // result'ом — это штатный выход после хода, не новый исход
                    if (!turn.AttemptResolved) turn.ResolveAttempt(AttemptEndKind.ProcessGone);
                    return Task.CompletedTask;
                }

            case RateLimitMessage rl:
                // Сначала downstream: SessionManager пишет usage-снимок и помечает пул
                // (существующий механизм), реакция фолбэка — после, по помеченному пулу
                return ForwardThenAsync(msg, () =>
                {
                    lock (turn.Sync)
                    {
                        if (turn.Settled || turn.AttemptResolved) return;
                        if (rl.Status != "rejected" || !ClaudeSubscriptionPool.IsExhaustionWindow(rl.LimitType)) return;
                        // CLI приостановил ход до сброса окна — снимем его с паузы и уйдём
                        // на другую подписку (ADR §2: rejected по окну исчерпания = фолбэк)
                        turn.RateLimitResetsAt = rl.ResetsAt;
                        turn.ResolveAttempt(AttemptEndKind.RateLimited);
                    }
                });

            default:
                return _downstream(msg);
        }
    }

    // Основной цикл: попытка → исход → классификация → ротация → повтор.
    private async Task RunFallbackLoopAsync(FallbackTurn turn, string text,
        IReadOnlyList<string> attachedPaths, int agentDepth, bool suppressTasksExecute)
    {
        // Учёт попыток «модель × подписка»: пара не пробуется дважды за ход (ADR §4)
        var attempted = new HashSet<(string Model, string Key)>();
        // След перепробованных пар — в финальную ошибку (виден в ленте/карточке задачи)
        var trace = new List<string>();
        var substitutions = 0;
        AttemptEnd? lastEnd = null;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_userInterrupted) { await SettleAsync(turn); return; }

                var model = _effectiveModel() ?? "";
                var key = CurrentProviderKey();
                attempted.Add((model, key));

                TaskCompletionSource<AttemptEnd> attemptTcs;
                lock (turn.Sync)
                {
                    turn.BeginAttempt();
                    attemptTcs = turn.AttemptTcs;
                }

                await _inner.SendMessageAsync(text, attachedPaths, agentDepth, suppressTasksExecute);

                AttemptEnd end;
                try { end = await attemptTcs.Task.WaitAsync(_cts.Token); }
                catch (OperationCanceledException) { return; } // сессию закрыли — оркестрация снята
                lastEnd = end;

                if (_userInterrupted) { await SettleAsync(turn); return; }

                // Успех (нет ошибки доставки) — задержанный result уходит наружу, ход окончен
                if (!IsDeliveryFailure(end)) { await SettleAsync(turn); return; }

                var cls = TurnErrorClassifier.Classify(new TurnAttemptOutcome
                {
                    HasResult = end.Kind == AttemptEndKind.Result,
                    Subtype = end.Result?.Subtype,
                    ApiErrorStatus = end.Result?.ApiErrorStatus,
                    ErrorText = end.ErrorText,
                    RateLimitRejected = end.Kind == AttemptEndKind.RateLimited,
                });
                // Неизвестная/содержательная ошибка — фолбэк НЕ запускается (fail-closed)
                if (cls == FallbackErrorClass.None) { await SettleAsync(turn); return; }

                trace.Add(TraceLine(model, key, cls));
                if (substitutions >= EffectiveMaxSubstitutions())
                {
                    await FailExhaustedAsync(turn, trace, substitutions, end);
                    return;
                }

                var next = ResolveNextTarget(cls, end, attempted, model, key);
                if (next is null) { await FailExhaustedAsync(turn, trace, substitutions, end); return; }

                // Терминальные сообщения провальной попытки наружу не идут — ход продолжается
                lock (turn.Sync)
                {
                    turn.Held.Clear();
                    // Attempt прерван ротацией: последующие Exited/ErrorMessage — уборка, глотать
                    // (выставляем ВСЕГДА, не только на RateLimited — уборка нужна и после
                    // ProcessGone/FatalError; иначе Exited/ErrorMessage пойдут downstream
                    // и осядут в ленте «ответом» уже выбывшей попытки)
                    turn.SwallowCleanup = true;
                }
                // Паузу CLI на исчерпанном окне снимаем убийством процесса — следующий
                // процесс запустится уже на новой паре
                if (end.Kind == AttemptEndKind.RateLimited) _inner.Interrupt();

                // Подмена помечается в ленте существующим provider_switched (Auto=true).
                // Label=null — переключил SessionManager.TryPoolFailover, маркер уже был.
                // Reason — wire-имя класса ошибки, чтобы фронт показал каноническую
                // формулировку подсказки вместо сырого текста Label
                if (next.Label is not null)
                {
                    var reason = TurnErrorClassifier.WireName(cls);
                    await _downstream(new ProviderSwitchedMessage(next.Key, next.Model, next.Label, Auto: true, Reason: reason));
                    LogInfo($"Подмена: {TraceLine(model, key, cls)} → «{KeyLabel(next.Key)}» / «{next.Model ?? model}» (причина {reason ?? "неизвестно"})");
                }
                else
                {
                    LogInfo($"Подмена без маркера: {TraceLine(model, key, cls)} → «{KeyLabel(next.Key)}» (маркер уже был от SessionManager)");
                }

                ApplyTarget(next);
                substitutions++;
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Сбой оркестрации ({Info.Id}): {ex.Message}");
            try { await SettleAsync(turn); } catch { /* сессия уже закрыта */ }
        }
        finally
        {
            // Счётчик сообщений пользователя: реальный ClaudeSession инкрементирует
            // MessageCount на каждую отправку — после подмен восстановим так, чтобы
            // одно user-сообщение с N попытками считалось одним сообщением
            Info.MessageCount = _snapshotMessageCount + 1;
            lock (_gate) if (ReferenceEquals(_turn, turn)) _turn = null;
        }
    }

    // Ошибка доставки: всё, кроме чистого успеха. API-ошибка провайдера приходит как
    // subtype=success + is_error (в протоколе остаётся следом api_error_status и текстом
    // ошибки ErrorMessage(ExpectResultFollows=true)) — оба признака учтены.
    private static bool IsDeliveryFailure(AttemptEnd end) => end.Kind switch
    {
        AttemptEndKind.Result => end.Result!.Subtype == "error"
            || !string.IsNullOrEmpty(end.Result.ApiErrorStatus)
            || !string.IsNullOrEmpty(end.ErrorText),
        _ => true,
    };

    // Уровень 1: другая подписка того же пула (та же модель). Уровень 2: следующий
    // сторонний провайдер цепочки. null — кандидатов не осталось.
    private FallbackTarget? ResolveNextTarget(FallbackErrorClass cls, AttemptEnd end,
        HashSet<(string Model, string Key)> attempted, string model, string currentKey)
    {
        // Лимитные классы помечают подписку исчерпанной в пуле (существующий механизм):
        // 5xx/обрыв — НЕ помечают, это не квота аккаунта (инцидент 2026-08-02: ложные баны)
        if (cls is FallbackErrorClass.RateLimit or FallbackErrorClass.UsageLimit)
        {
            DateTime? resetsAt = null;
            if (end.RateLimitResetsAt is { } s && DateTime.TryParse(s, out var dt))
                resetsAt = dt.ToUniversalTime();
            _pool.MarkExhausted(currentKey, resetsAt);
        }

        var modelForPool = string.IsNullOrWhiteSpace(model) ? null : model;
        var isNativeClaude = _providers?.ResolveByModel(modelForPool) is null;

        if (isNativeClaude && _pool.HasExtra)
        {
            // SessionManager.TryPoolFailover по rate_limit_event мог уже переключить чат
            // (транскрипт перенесён им) — тогда не мигрируем повторно и не шлём второй маркер
            var switched = Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
            if (switched != currentKey
                && !attempted.Contains((model, switched))
                && !_pool.IsExhausted(switched)
                && _pool.SupportsModel(switched, modelForPool))
                return new FallbackTarget(switched, null, Label: null,
                    ProfileRoot: ResolveRootFor(switched), IsProviderSwitch: false);

            // Иначе крутим сами: Pick — штатный механизм ротации пула. Если он вернул
            // уже пробованную пару (напр. пометку исчерпания посреди хода снял warmup-пинг)
            // — перебираем остальных последовательно; новой логики выбора не заводим (ADR §3)
            foreach (var candidate in SubscriptionCandidates(modelForPool, currentKey))
            {
                if (attempted.Contains((model, candidate))) continue;
                // dst-корень может быть неизвестен (нет провайдеров, container без initialProfileRoot)
                // — TryMigrateTranscript сам обрабатывает отсутствие транскрипта: новый ход
                // стартует на чистом профиле целевой подписки, мигрировать нечего
                var dstRoot = ResolveRootFor(candidate);
                if (!TryMigrateTranscript(dstRoot)) continue;
                return new FallbackTarget(candidate, null,
                    Label: $"Автофолбэк: подписка «{KeyLabel(currentKey)}» → «{KeyLabel(candidate)}»",
                    ProfileRoot: dstRoot, IsProviderSwitch: false);
            }
        }

        // Уровень 2: цепочка сторонних провайдеров в порядке конфига
        if (_providers is not null)
        {
            foreach (var provider in _providers.Enabled)
            {
                if (provider.Key == currentKey) continue;
                var equivalent = EquivalentModel(provider, modelForPool);
                if (equivalent is null || attempted.Contains((equivalent, provider.Key))) continue;
                var dstRoot = ResolveRootFor(provider.Key);
                if (!TryMigrateTranscript(dstRoot)) continue;
                var name = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Key : provider.DisplayName;
                return new FallbackTarget(provider.Key, equivalent,
                    Label: $"Автофолбэк: смена провайдера → «{name}»",
                    ProfileRoot: dstRoot, IsProviderSwitch: true);
            }
        }

        return null;
    }

    // Кандидаты уровня 1: сначала штатный Pick (с учётом исчерпания, SupportsModel,
    // тарифа и утилизации), затем последовательно остальные подписки пула
    private IEnumerable<string> SubscriptionCandidates(string? model, string currentKey)
    {
        var pick = _pool.Pick(model);
        if (pick != currentKey && !_pool.IsExhausted(pick) && _pool.SupportsModel(pick, model))
            yield return pick;
        foreach (var sub in _pool.All)
        {
            if (sub.Key == currentKey || sub.Key == pick) continue;
            if (_pool.IsExhausted(sub.Key) || !_pool.SupportsModel(sub.Key, model)) continue;
            yield return sub.Key;
        }
    }

    // Модель-эквивалент по слоту (ADR §3): тир текущей модели определяем совпадением
    // со слотами владельца, у целевого провайдера берём модель того же тира
    // (с фолбэком на первый каталог — провайдер без моделей пропускается выше)
    private string? EquivalentModel(LlmProviderConfig provider, string? currentModel)
    {
        string? FirstCatalogModel() => provider.Models.FirstOrDefault()?.Id;
        var m = ResolveTier(currentModel) switch
        {
            ModelTier.Strong => FirstNonEmpty(provider.TierStrong, provider.TierMedium, FirstCatalogModel()),
            ModelTier.Weak => FirstNonEmpty(provider.TierWeak, FirstCatalogModel()),
            _ => FirstNonEmpty(provider.TierMedium, FirstCatalogModel()),
        };
        return string.IsNullOrWhiteSpace(m) ? null : m;
    }

    private ModelTier ResolveTier(string? model)
    {
        // У проектных сессий OwnerId пуст — слоты тогда глобальные, как и при резолве хода
        if (_tiers is null || string.IsNullOrWhiteSpace(model)) return ModelTier.Medium;
        foreach (var tier in new[] { ModelTier.Strong, ModelTier.Medium, ModelTier.Weak })
        {
            var slot = _tiers.ModelFor(tier, Info.OwnerId);
            if (!string.IsNullOrWhiteSpace(slot)
                && string.Equals(slot.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase))
                return tier;
        }
        return ModelTier.Medium;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    // Перенос транскрипта в профиль целевой пары (тот же механизм, что у ручной миграции
    // MigrateProviderAsync и автофейловера TryPoolFailover). Ход ещё не начинался —
    // переносить нечего. Не удалось — кандидат пропускается (fail-closed)
    private bool TryMigrateTranscript(string? dstRoot)
    {
        if (Info.ClaudeSessionId is null) return true;
        if (_profileRoot is null || dstRoot is null) return false;
        string cwd;
        try { cwd = _launcher is { IsSandboxed: true } ? _launcher.Paths.ToRuntime(_rootPath) : _rootPath; }
        catch (InvalidOperationException) { return false; } // папка вне монтирований песочницы
        if (!TranscriptMigrator.TryMigrate(_profileRoot, dstRoot, cwd, Info.ClaudeSessionId, out var error))
        {
            LogWarn($"Транскрипт {Info.Id} не перенесён {dstRoot}: {error}");
            return false;
        }
        return true;
    }

    // Применение выбранной пары: смена подписки двигает только Provider, смена провайдера —
    // и модель (эквивалент по слоту). Следующий ход пересоберёт env и процесс сам:
    // оверрайды подписки читаются из Info.Provider, модель — из Info.Model на каждый ход
    private void ApplyTarget(FallbackTarget next)
    {
        if (next.IsProviderSwitch) Info.Model = next.Model;
        Info.Provider = next.Key;
        Info.UpdatedAt = DateTime.UtcNow;
        _profileRoot = next.ProfileRoot;
        // Персистентность подхватится ближайшим SaveSessions (статус хода меняется на
        // финале) — своего сохранения у адаптера нет, им владеет SessionManager
    }

    // Текущий ключ пары: провайдер по модели (приоритет), затем Provider сессии —
    // тот же порядок, что у MigrateProviderAsync при вычислении currentKey
    private string CurrentProviderKey()
    {
        var byModel = _providers?.ResolveByModel(
            string.IsNullOrWhiteSpace(Info.Model) ? null : Info.Model)?.Key;
        return byModel ?? (string.IsNullOrEmpty(Info.Provider)
            ? ClaudeSubscriptionPool.PrimaryKey : Info.Provider);
    }

    // Хостовый физический корень профиля CLI для ключа пары (источник/приёмник переноса).
    // Раскладка зеркалит сборку env хода (ClaudeSession + BuildCliEnv/BuildOAuthCliEnv)
    // и её перепись для песочницы (DockerProcessRunner.RewriteProfileEnv)
    private string? ResolveRootFor(string key)
    {
        if (_providers?.GetByKey(key) is not null)
            return SandboxAwareRoot(key, _providers.GetProfileDir(key));
        if (_pool.All.Any(s => s.Key == key && s.Enabled))
            return SandboxAwareRoot("sub-" + key, _providers?.GetProfileDir("sub-" + key));
        // Первичный аккаунт без записи пула (или запись без креденшалов) — ~/.claude
        return SandboxAwareRoot("default", _providers?.UserProfileDir);
    }

    // Local — как есть; container-пользователь — data/sandbox-profiles/{ownerId}/{папка}:
    // родитель выводится из корневого пути старта сессии (SessionManager.ConfigRootFor
    // уже разрешил раскладку песочницы для текущей подписки)
    private string? SandboxAwareRoot(string folder, string? localRoot)
    {
        if (_launcher is not { IsSandboxed: true }) return localRoot;
        if (_initialProfileRoot is null) return null;
        var parent = Path.GetDirectoryName(_initialProfileRoot.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, folder);
    }

    // Финал при исчерпании цепочки: след пар + последняя причина — в ленту, финальный
    // result — ошибочный (у задачи исполнителя существующий путь пометит сбой и уведомит
    // постановщика; в исходный статус задача не возвращается)
    private async Task FailExhaustedAsync(FallbackTurn turn, IReadOnlyList<string> trace,
        int substitutions, AttemptEnd lastEnd)
    {
        var reason = string.IsNullOrEmpty(lastEnd.Result?.ApiErrorStatus)
            ? lastEnd.ErrorText ?? "поток прерван"
            : lastEnd.Result!.ApiErrorStatus!;
        await _downstream(new ErrorMessage(
            $"Фолбэк моделей исчерпан (подмен: {substitutions}, потолок {EffectiveMaxSubstitutions()}). "
            + $"Пробованные пары: {string.Join("; ", trace)}. Последняя ошибка: {Truncate(reason, 300)}"));

        List<ServerMessage> held;
        lock (turn.Sync)
        {
            held = [.. turn.Held];
            turn.Held.Clear();
            turn.Settled = true;
        }
        // Реальный result последней попытки со subtype=error выпускаем как есть;
        // success-с-API-ошибкой заменяем ошибочным — иначе сбой выглядел бы успехом
        var result = held.OfType<ResultMessage>().FirstOrDefault(r => r.Subtype == "error")
            ?? new ResultMessage("error", 0, 0, null, null, ApiErrorStatus: lastEnd.Result?.ApiErrorStatus);
        await _downstream(result);
        foreach (var m in held.Where(m => m is not ResultMessage))
            await _downstream(m);
    }

    // Финал без подмены (успех / неизвестная ошибка / interrupt): задержанные
    // терминальные сообщения попытки уходят наружу как есть
    private async Task SettleAsync(FallbackTurn turn)
    {
        List<ServerMessage> held;
        lock (turn.Sync)
        {
            held = [.. turn.Held];
            turn.Held.Clear();
            turn.Settled = true;
        }
        foreach (var m in held) await _downstream(m);
    }

    private async Task ForwardThenAsync(ServerMessage msg, Action after)
    {
        await _downstream(msg);
        after();
    }

    private static string TraceLine(string model, string key, FallbackErrorClass cls) =>
        $"{(string.IsNullOrEmpty(model) ? "модель по умолчанию" : $"модель «{model}»")} × «{KeyLabel(key)}» — {ClassLabel(cls)}";

    private static string ClassLabel(FallbackErrorClass cls) => cls switch
    {
        FallbackErrorClass.RateLimit => "лимит запросов",
        FallbackErrorClass.UsageLimit => "лимит использования",
        FallbackErrorClass.ProviderError => "ошибка провайдера",
        FallbackErrorClass.Unreachable => "эндпоинт недоступен",
        _ => "ошибка",
    };

    private static string KeyLabel(string key) =>
        key == ClaudeSubscriptionPool.PrimaryKey ? "claude" : key;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // Исход одной попытки
    private enum AttemptEndKind { Result, ProcessGone, FatalError, RateLimited }

    private sealed record AttemptEnd(
        AttemptEndKind Kind,
        ResultMessage? Result,
        string? ErrorText,
        string? RateLimitResetsAt);

    private sealed record FallbackTarget(
        string Key, string? Model, string? Label, string? ProfileRoot, bool IsProviderSwitch);

    // Состояние хода под фолбэком: задержанные терминальные сообщения, исход текущей
    // попытки и флаг «оркестрация завершена» (после него события идут насквозь)
    private sealed class FallbackTurn
    {
        public readonly object Sync = new();
        public TaskCompletionSource<AttemptEnd> AttemptTcs = NewTcs();
        public bool Settled;
        // Процесс попытки прерван ротацией (rate-limit пауза): следующие Exited/ErrorMessage
        // этой попытки — уборка, их глотаем до начала следующей попытки
        public bool SwallowCleanup;
        public bool AttemptResolved;
        public List<ServerMessage> Held = [];
        public string? ErrorText;
        public string? RateLimitResetsAt;

        public static TaskCompletionSource<AttemptEnd> NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Новая попытка: чистый TCS и сброшенные признаки (задержанное предыдущей
        // попыткой уже решено оркестратором)
        public void BeginAttempt()
        {
            AttemptTcs = NewTcs();
            AttemptResolved = false;
            SwallowCleanup = false;
            Held.Clear();
            ErrorText = null;
            RateLimitResetsAt = null;
        }

        public void NoteResult(ResultMessage res)
        {
            Held.Add(res);
            ResolveAttempt(AttemptEndKind.Result, res);
        }

        public void NoteErrorText(string text)
            => ErrorText = string.IsNullOrEmpty(ErrorText) ? text : ErrorText + "\n" + text;

        public void Hold(ServerMessage msg) => Held.Add(msg);

        public void ResolveAttempt(AttemptEndKind kind, ResultMessage? result = null)
        {
            if (AttemptResolved) return;
            AttemptResolved = true;
            AttemptTcs.TrySetResult(new AttemptEnd(kind, result, ErrorText, RateLimitResetsAt));
        }
    }
}
