using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Журнал цен хода: трекинг и догоняющий учёт fal/glif-генераций, публикация стоимости в
// историю и ленту, запись расхода (ISpendCollector) и лимитов подписки
// (SubscriptionLimitRecorder). Вынесен из SessionManager (S3-2,
// docs/research/session-manager-split-2026-11.md §1 «цены fal/glif»): с историей в одном
// классе это цикл (GetHistoryAsync досчитывает цены, а публикация цены пишет в историю),
// раздельно — две зависимости вниз и склейка в фасаде.
// _falPersistLock здесь НЕТ своего: каждая запись в историю идёт через
// SessionHistory.WithFalPersistLockAsync — тот же лок, что у остальных внеходовых записей
// (иначе публикация цены и оживление аккумулятора теряли бы друг друга).
// FalCostService.OnCostResolved = PublishFalCostAsync переехал сюда же.
// Зависимостей девять (falCost, glif, spend, limitRecorder, history, chatHistory, store,
// llmProviders): в таблице S3-0 было шесть — SaveSnapshotAsync аккумулятора через
// ChatHistoryService в ней не досчитан; llmProviders добавлен в S3-6 — запись расхода хода
// (RecordTurnSpend) переехала сюда вместе с телом конца хода. Класс public
// ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4).
public sealed class TurnCostLedger
{
    private readonly FalCostService _falCost;
    private readonly GlifAccountService? _glif;
    private readonly Spend.ISpendCollector? _spend;
    private readonly SubscriptionLimitRecorder _limitRecorder;
    private readonly SessionHistory _history;
    // SaveSnapshotAsync аккумулятора пишет снимок через ChatHistoryService — та же спинка,
    // что у SessionHistory. Здесь она отдельным параметром: логика журнала (дедуп, ветки,
    // аналитика) остаётся в роли, а диск — за спинкой.
    private readonly ChatHistoryService _chatHistory;
    private readonly SessionStore _store;
    // Реестр провайдеров — для записи расхода хода (S3-6): ResolveModelOrDefault и различение
    // локального провайдера нужны RecordTurnSpend, а не только слою Llm. С ним зависимостей
    // девять (потолок 10).
    private readonly LlmProviderRegistry _llmProviders;
    private readonly ILogger<TurnCostLedger> _log;

    public TurnCostLedger(
        FalCostService falCost,
        GlifAccountService? glif,
        Spend.ISpendCollector? spend,
        SubscriptionLimitRecorder limitRecorder,
        SessionHistory history,
        ChatHistoryService chatHistory,
        SessionStore store,
        LlmProviderRegistry llmProviders,
        ILogger<TurnCostLedger> log)
    {
        _falCost = falCost;
        _glif = glif;
        _spend = spend;
        _limitRecorder = limitRecorder;
        _history = history;
        _chatHistory = chatHistory;
        _store = store;
        _llmProviders = llmProviders;
        _log = log;
        // Найденную стоимость fal.ai публикуем в SignalR + историю
        _falCost.OnCostResolved = PublishFalCostAsync;
    }

    // Учёт лимитов подписок по rate_limit_event хода: та же точка, что у шлюза LLM. Состояние
    // рекордер не держит (оно в UsageService и ClaudeSubscriptionPool), поэтому свой экземляр
    // здесь — не второй счётчик.
    internal SubscriptionLimitRecorder LimitRecorder => _limitRecorder;

    // Запись расхода штатного хода в аналитику (Spend Analytics): сам код — в SpendMapping,
    // сюда переехал вызов вместе с телом конца хода (S3-6): обе стороны записи (ISpendCollector
    // и LlmProviderRegistry) уже здесь, а реестр актора — стор (GetTurnActor: актор жив,
    // пока мы в разборе сообщения, — урок R-S2a-6c).
    internal void RecordTurnSpend(Session? s, ResultMessage m) =>
        SpendMapping.RecordTurnSpend(_spend, _llmProviders, _store.ResolveOwnerId, _log, s, m, _store);

    // Запись расхода Higgsfield-генерации по tool_use того же хода.
    internal void RecordHiggsfieldGeneration(Session s) =>
        SpendMapping.RecordHiggsfieldGeneration(_spend, _store.ResolveOwnerId, _log, s);

    // Ставит результат генерации fal.ai на отслеживание стоимости (опрос billing-events — в фоне).
    internal void TryTrackFalCost(string sessionId, string content)
    {
        if (!_falCost.Enabled) return;
        var requestId = SpendMapping.TryExtractFalRequestId(content);
        if (!string.IsNullOrEmpty(requestId))
            _falCost.Track(sessionId, requestId);
    }

    // Догоняет стоимость для СТАРЫХ генераций fal.ai в истории, у которых ещё нет fal_cost
    // (сгенерированы до появления фичи/ключа). Вызывается при загрузке истории сессии.
    internal void BackfillFalCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (!_falCost.Enabled) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredFalCostMessage f) have.Add(f.RequestId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var rid = SpendMapping.TryExtractFalRequestId(t.Result);
            if (rid != null && !have.Contains(rid))
                _falCost.Track(sessionId, rid);
        }
    }

    // Детектит завершённую glif-генерацию прямо в tool_result: кредиты уже есть в _meta.glif,
    // поэтому публикуем сообщение синхронно, без фонового опроса как у fal.
    internal void TryTrackGlifCost(string sessionId, string content)
    {
        if (_glif?.Enabled != true) return;
        var msg = GlifCostParser.TryParse(content);
        if (msg is not null)
            _ = PublishGlifCostAsync(sessionId, msg);
    }

    // Догоняет учёт для СТАРЫХ glif-генераций в истории, у которых ещё нет glif_cost.
    // Вызывается при загрузке истории сессии.
    internal void BackfillGlifCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (_glif?.Enabled != true) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredGlifCostMessage g) have.Add(g.JobId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var msg = GlifCostParser.TryParse(t.Result);
            if (msg is not null && !have.Contains(msg.JobId))
                _ = PublishGlifCostAsync(sessionId, msg);
        }
    }

    // Публикация учёта glif-генерации: запись в историю (дедуп) + broadcast клиентам.
    // Зеркало PublishFalCostAsync: активная сессия → аккумулятор; неактивная → прямо на диск.
    public async Task PublishGlifCostAsync(string sessionId, GlifCostMessage msg)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;

        // Лок держим РОВНО один раз на весь путь «проверить entry.Accumulator → выбрать
        // ветку → записать»: иначе EnsureAccumulatorAsync под тем же локом успевает
        // прочитать историю до нашей записи, создать аккумулятор со старым снимком и
        // следующий SaveSnapshotAsync затирает нашу запись. SemaphoreSlim не реентерабелен,
        // поэтому AppendIfNotDuplicateStoredNoLockAsync внутри WithFalPersistLockAsync
        // НЕ берёт лок повторно (контракт — caller holds).
        var result = await _history.WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnGlifCost(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model))
                    return SessionHistory.AppendResult.Duplicate;
                try { await entry.Accumulator.SaveSnapshotAsync(_chatHistory); }
                catch (Exception ex) { Console.Error.WriteLine($"[GlifCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                return SessionHistory.AppendResult.Added;
            }
            else
            {
                try
                {
                    return await _history.AppendIfNotDuplicateStoredNoLockAsync(entry,
                        m => m is StoredGlifCostMessage g && g.JobId == msg.JobId,
                        () => new StoredGlifCostMessage(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model));
                }
                catch (Exception ex)
                {
                    // Дисковая запись не удалась — но аналитику и broadcast обязаны пройти
                    // (зеркало поведения PublishFalCostAsync до правки): карточка стоимости
                    // должна появиться у пользователя даже при сбое истории, иначе при рестарте
                    // она пропадёт совсем.
                    Console.Error.WriteLine($"[GlifCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}");
                    return SessionHistory.AppendResult.Added;
                }
            }
        });

        if (result == SessionHistory.AppendResult.Duplicate) return;

        // Аналитика: генерация glif — счётчик операций, кредиты про запас, стоимость USD неизвестна.
        SpendMapping.RecordGlifGeneration(_spend, _store.ResolveOwnerId, _log, entry.Info, msg);

        await _store.BroadcastAsync(sessionId, msg);
    }

    // Публикация найденной стоимости fal.ai: запись в историю (дедуп) + broadcast клиентам.
    // Активная сессия → через аккумулятор; неактивная (нет аккумулятора) → прямо в файл истории,
    // иначе стоимость не переживёт переоткрытие и «считается…» зависнет.
    public async Task PublishFalCostAsync(string sessionId, FalCostMessage msg)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;

        // Лок держим РОВНО один раз на весь путь «проверить entry.Accumulator → выбрать
        // ветку → записать»: иначе EnsureAccumulatorAsync под тем же локом успевает
        // прочитать историю до нашей записи, создать аккумулятор со старым снимком и
        // следующий SaveSnapshotAsync затирает нашу запись. SemaphoreSlim не реентерабелен,
        // поэтому AppendIfNotDuplicateStoredNoLockAsync внутри WithFalPersistLockAsync
        // НЕ берёт лок повторно (контракт — caller holds).
        var result = await _history.WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnFalCost(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice))
                    return SessionHistory.AppendResult.Duplicate;
                try { await entry.Accumulator.SaveSnapshotAsync(_chatHistory); }
                catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                return SessionHistory.AppendResult.Added;
            }
            else
            {
                // Сессия не активна — пишем стоимость напрямую в историю на диске. Дедуп
                // по RequestId и проверка наличия ClaudeSessionId идут в
                // AppendIfNotDuplicateStoredNoLockAsync.
                try
                {
                    return await _history.AppendIfNotDuplicateStoredNoLockAsync(entry,
                        m => m is StoredFalCostMessage f && f.RequestId == msg.RequestId,
                        () => new StoredFalCostMessage(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice));
                }
                catch (Exception ex)
                {
                    // Дисковая запись не удалась — но аналитику и broadcast обязаны пройти:
                    // карточка стоимости должна появиться у пользователя даже при сбое истории,
                    // иначе при рестарте она пропадёт совсем (нет ни AppendIfNotDuplicate, ни
                    // аккумулятора).
                    Console.Error.WriteLine($"[FalCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}");
                    return SessionHistory.AppendResult.Added;
                }
            }
        });

        if (result == SessionHistory.AppendResult.Duplicate) return; // дубль — не ретранслируем

        // Аналитика расхода: генерация fal.ai — счётчик операций (токенов у fal нет),
        // фактическая стоимость про запас. Дедуп выше гарантирует одну запись на request_id.
        SpendMapping.RecordFalGeneration(_spend, _store.ResolveOwnerId, _log, entry.Info, msg);

        await _store.BroadcastAsync(sessionId, msg);
    }
}
