using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Services;

// Чистые функции сборки SpendRecord из потока сообщений/событий приёма хода. Вынесены из
// SessionManager (этап 4, волна 1 «приём хода», 2026-09-07) — это код спины, использующий
// обе стороны: Core-контракт `ISpendCollector` (зависимость на вертикаль Spend идёт через
// интерфейс в Core, не через ProjectReference на Spend.dll) и `LlmProviderRegistry`/
// `IModelResolver` слоя Llm. Перенос самого SpendMapping в вертикаль Spend отклонён
// архитектором: потянуло бы правку конструктора SessionManager ради косметики.
// Никакого состояния, только зависимости в параметрах — шов здесь не нужен.
internal static class SpendMapping
{
    // One-shot-флаги: при отсутствии ISpendCollector предупреждаем один раз за процесс,
    // чтобы каждый ход не спамил лог
    private static int _turnSpendWarned;
    private static int _falSpendWarned;
    private static int _glifSpendWarned;

    // request_id внутри текста: для результата, который перестал быть одним JSON. По наблюдению
    // разведки (f7e8c46d), tool_result внешнего MCP mcp.fal.ai с блоком картинки Claude CLI
    // отдаёт с маркером "[Image: source: …webp]" вместо этого блока — после этого JSON целиком
    // не парсится, и такие вызовы не учитывались вовсе (эмпирика 29.09–01.11: 4 пропущенных).
    // Символы id ограничены [A-Za-z0-9_-] и длиной 64 — чтобы не выхватить длинное поле чужого
    // формата; форма id'а fal (UUID) в этот набор входит.
    private static readonly Regex FalRequestIdInText = new(
        @"""request_id""\s*:\s*""([0-9A-Za-z_-]{1,64})""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Извлекает request_id результата вызова, если это генерация fal.ai. Признак fal —
    // наличие fal-домена где-либо в ответе. Покрывает обе формы результата:
    //  • run_model/submit_job: fal.run в *_url (status_url/response_url/cancel_url);
    //  • get_job_result (видео/аудио): *_url нет, но fal.media в URL медиа.
    // Форма контента — любая: цельный JSON-объект или текст, где JSON испорчен маркером
    // изображения (поиск первого request_id). Для цельного объекта берутся только
    // СТРУКТУРНЫЕ поля ответа fal: top-level request_id (одиночный вызов) и request_id
    // элементов массива jobs[] (пакетные submit_jobs/wait_for_jobs: верхнего id у них нет,
    // и один такой tool_result терял сразу все N запросов — живая сверка 04f65504). Чужие
    // вложенные id (напр. data.request_id) не берутся: это не структурная форма fal.
    // Id, названный и на верхнем уровне, и в jobs[], отдаётся один раз: одному сетевому
    // циклу — одна запись расхода. Ничего не найдено — пустой список тихо, без исключения.
    public static IReadOnlyList<string> TryExtractFalRequestIds(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        if (!content.Contains("fal.run") && !content.Contains("fal.ai") && !content.Contains("fal.media")) return [];
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var ids = new List<string>();
                AddStructuralId(root, ids);
                if (root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                    foreach (var job in jobs.EnumerateArray())
                        AddStructuralId(job, ids);
                // Дедуп по первому вхождению: верхний id, повторённый в jobs[], считается один раз.
                return ids.Distinct(StringComparer.Ordinal).ToList();
            }
        }
        catch { /* не JSON целиком — tool_result с маркером [Image: …], читаем по тексту ниже */ }
        // Нецельный контент: первое вхождение request_id в тексте (маркер — блок картинки этого
        // же вызова, отдельного запроса в тексте нет).
        var match = FalRequestIdInText.Match(content);
        return match.Success ? [match.Groups[1].Value] : [];
    }

    // request_id структурной позиции цельного JSON-объекта: только строковое поле с этим
    // именем у самого элемента; не-объект, отсутствующее поле или не-строка — ничего не добавляют.
    private static void AddStructuralId(JsonElement el, List<string> into)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        if (el.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String)
        {
            var v = rid.GetString();
            if (!string.IsNullOrEmpty(v)) into.Add(v);
        }
    }

    // Запись расхода штатного хода в аналитику (Spend Analytics): разрезы — из фактического
    // маршрута прогона (UpstreamProvider/UpstreamModel в result'е, «учётка хода» SP1), фолбэк
    // на старые result без этих полей — настройки сессии. Ошибка записи ход не роняет.
    //
    // actors — реестр актора хода (S2a-6b): актор (тот, кто подал ход) читается здесь, в
    // момент учёта: ядро снимает актора только после обработчика result (смена статуса идёт
    // ниже по методу), поэтому здесь он ещё жив — тот самый, что запомнен на подаче.
    // Урок S2a-6c (журнал событий): потребитель, читающий реестр ПОСЛЕ конца хода,
    // получает null — здесь мы до конца хода. Без реестра (null) и для ходов без человека
    // запись остаётся без актора: срез чтения относится такие записи владельцу.
    public static void RecordTurnSpend(
        ISpendCollector? spend,
        LlmProviderRegistry llmProviders,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session? s,
        ResultMessage m,
        ClaudeHomeServer.Services.Access.ITurnActorRegistry? actors = null)
    {
        if (spend is null || s is null || m.Usage is null)
        {
            if (spend is null && Interlocked.Exchange(ref _turnSpendWarned, 1) == 0)
                log?.LogWarning("spend: коллектор недоступен, запись расхода хода пропущена");
            return;
        }
        try
        {
            // Провайдер и модель — из фактического маршрута прогона (SP1: ключ апстрима пишется
            // в result при старте прогона и в маршруте токена шлюза), а не из настроек чата:
            // при автофолбэке и ходе через шлюз расход приписывался провайдеру, который ход не
            // делал. Никакого резолва провайдера по UsageModel: он префиксный и уводит настоящие
            // подписки Claude в «дёшево». Фолбэк (result до SP1) — прежний путь: настройки сессии.
            var provider = SpendSources.NormalizeProvider(m.UpstreamProvider ?? s.Provider);
            // Фактическая модель: апстрим шлюза (его знает только шлюз — сессия не видит
            // ротацию), затем modelUsage (субагенты могли считать другой), затем модель сессии;
            // пустой результат резолвится в дефолт подписки, чтобы SpendRecord никогда не
            // оставался без модели (иначе в аналитике копилась «Модель по умолчанию»).
            var model = llmProviders.ResolveModelOrDefault(m.UpstreamModel ?? m.UsageModel ?? s.Model, provider);
            // Локальный провайдер (IsLocal в конфиге) — свой движок, подписки за ним нет:
            // его ходы бесплатные независимо от ключа (напр. "local-qwen"). Признак считается
            // по тому же ключу, что пишется в запись: класс и источник расхода не разъезжаются.
            var isLocal = llmProviders.GetByKey(provider)?.IsLocal == true;
            var owner = resolveOwnerId(s) ?? "";
            // Актор хода — из реестра; реестр или актор неизвестен — null (ход без человека,
            // чат удалён, после рестарта). Ошибка реестра не роняет запись расхода.
            string? actorUserId;
            try { actorUserId = actors?.Actor(s.Id, owner); }
            catch (Exception ex) { actorUserId = null; log?.LogWarning(ex, "spend: актор хода не прочитался"); }
            // Initiator намеренно НЕ заполняется (null = «не назван», не human): однозначного
            // признака «ход человека» в этой точке нет. Актор реестра не годится — он «липкий»
            // у внутренних ходов (дренаж, WorkLoop, nudge) и несёт id человека и у агентских
            // подач (chats_send), а признак fromHuman живёт в SessionManager.NoteTurnActor и
            // до RecordTurnSpend не доезжает. Требует решения Александра (новый шов в реестр
            // актора); до него не выдумываем — иначе агентский ход запишется как human.
            spend.Record(new SpendRecord
            {
                OwnerId = owner,
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                ActorUserId = string.IsNullOrEmpty(actorUserId) ? null : actorUserId,
                Provider = provider,
                Model = model,
                Source = SpendSources.IsFree(provider, model, isLocal) ? SpendSources.Free : SpendSources.ChatTurn,
                InputTokens = m.Usage.InputTokens,
                OutputTokens = m.Usage.OutputTokens,
                CacheReadTokens = m.Usage.CacheReadTokens,
                CacheCreationTokens = m.Usage.CacheCreationTokens,
                CostUsd = m.TotalCostUsd,
                DurationMs = m.DurationMs,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись хода не удалась"); }
    }

    // Аналитика расхода: генерация fal.ai — счётчик операций (токенов у fal нет),
    // фактическая стоимость про запас. Дедуп выше гарантирует одну запись на request_id.
    // Вызывается уже после снятия _falPersistLock — внешнего состояния не держит.
    //
    // Этот путь — агентский по построению: в mcp.fal.ai ходит сам CLI, через роутер
    // картинок он не идёт. Инициатор — agent (S-SPEND-8): у агентской траты один признак,
    // «null + SessionId» вторым способом сказать то же самое быть не должно.
    public static void RecordFalGeneration(
        ISpendCollector? spend,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session s,
        FalCostMessage msg)
    {
        if (spend is null)
        {
            if (Interlocked.Exchange(ref _falSpendWarned, 1) == 0)
                log?.LogWarning("spend: коллектор недоступен, запись расхода генерации fal.ai пропущена");
            return;
        }
        try
        {
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = "fal",
                Model = msg.EndpointId,
                Source = SpendSources.Fal,
                CostUsd = msg.CostUsd,
                Generations = 1,
                Label = msg.EndpointId,
                Initiator = SpendInitiators.Agent,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись генерации fal не удалась"); }
    }

    // Аналитика: генерация glif — счётчик операций, кредиты про запас, стоимость USD неизвестна.
    // Инициатор — agent: glif из хода зовёт CLI напрямую (тот же признак, что у fal, S-SPEND-8).
    public static void RecordGlifGeneration(
        ISpendCollector? spend,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session s,
        GlifCostMessage msg)
    {
        if (spend is null)
        {
            if (Interlocked.Exchange(ref _glifSpendWarned, 1) == 0)
                log?.LogWarning("spend: коллектор недоступен, запись расхода генерации glif пропущена");
            return;
        }
        try
        {
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = "glif",
                Model = msg.Model ?? msg.OutputType,
                Source = SpendSources.Glif,
                CostUsd = null,
                Generations = 1,
                Label = msg.OutputType,
                Initiator = SpendInitiators.Agent,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись генерации glif не удалась"); }
    }

    // Аналитика: генерация Higgsfield — признак по ИМЕНИ инструмента (mcp__higgsfield__generate_*),
    // не по контенту результата. У Higgsfield кредиты, не токены и не USD — считаем только факт
    // генерации. Вызывается по ToolUseMessage (имя доступно в момент вызова, до tool_result).
    public static bool TryExtractHiggsfieldGeneration(string? toolName) =>
        toolName is not null && toolName.Contains("mcp__higgsfield__generate_");

    // Запись расхода Higgsfield-генерации: Generations=1, CostUsd=null (кредиты не пересчитываем).
    // Инициатор — agent: инструмент зовёт CLI в ходе (тот же признак, что у fal/glif, S-SPEND-8).
    public static void RecordHiggsfieldGeneration(
        ISpendCollector? spend,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session s)
    {
        if (spend is null) return;
        try
        {
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = SpendSources.Higgsfield,
                Source = SpendSources.Higgsfield,
                CostUsd = null,
                Generations = 1,
                Label = "higgsfield-generation",
                Initiator = SpendInitiators.Agent,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись генерации higgsfield не удалась"); }
    }
}
