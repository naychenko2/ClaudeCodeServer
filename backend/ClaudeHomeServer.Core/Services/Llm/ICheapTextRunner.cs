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

// Единая точка «дешёвого» текстового one-shot вызова для фоновых действий. Скрывает от
// потребителя выбор исполнителя: если действие сконфигурировано на локаль (LocalActionRouter)
// и Ollama доступна — идёт бесплатный локальный вызов; при недоступности/ошибке/пустом ответе
// откатывается на существующий путь claude (OneShotClaudeRunner). Действие не на локали —
// сразу claude. Контракт RunAsync намеренно совпадает с прежним прямым вызовом раннера
// (единый prompt → строка ответа), чтобы потребители разбирали ответ теми же парсерами.
//
// Контракт и связанные DTO (OneShotResult/OneShotUsage/LlmTimeoutException) живут в Core,
// чтобы вертикали (Skills, Git, Notes, Tasks, …) могли зависеть от шва без ProjectReference
// на Main: следующие волны модульности выносят их в отдельные .csproj, и без общего
// интерфейса в спинке каждый вынос дублировал бы ICheapTextRunner у себя.
public interface ICheapTextRunner
{
    bool UsesLocal(string actionKey);

    // Есть ли у места ХОТЬ КАКОЙ-ТО бесплатный маршрут в цепочке RunFreeAsync
    // (прямой адаптер агрегатора → локальная модель). Та же логика, что в RunFreeAsync,
    // без вызова: нужно только знать «пройдёт ли», а не вызывать. Используется
    // потребителями, которые включают/выключают себя по наличию бесплатного пути
    // (OllamaActionRankService: ранжир действий AI-хаба идёт только когда есть
    // бесплатный шаг — иначе правило-based фолбэк).
    bool HasFreeRoute(string actionKey);

    // Краткое описание маршрута действия для лога и события: «kind=model/tier/local/claude»,
    // конкретная модель или слот если применимо. Нужно диагностике в местах, где чейн
    // разводится (планировщик «Командной реализации», сводка «Что нового»): без этого в
    // логе «не ответил» без версий и провайдера, и разбор каждого сбоя — раскопки.
    string DescribeRoute(string actionKey, string? fallbackModel);

    // actionKey — ключ из LocalActionCatalog (определяет маршрут и профиль вызова).
    // fallbackModel — модель claude для существующего пути (как раньше читалась из конфига).
    // ownerId — владелец для среды исполнения claude-пути (Ollama ходит по HTTP независимо).
    // jsonFormat — формат ответа для действий со СТРОГИМ JSON-контрактом: строка "json"
    // (просто «отвечай валидным JSON») либо полноценная JSON-схема. Локальный путь тогда идёт
    // в ChatJsonAsync (structured output Ollama) вместо свободной генерации: без этого мелкая
    // модель регулярно оборачивает JSON в прозу, парсер падает и действие всё равно уходит в
    // фолбэк на claude — экономии не возникает. Обычно достаточно "json": форму ответа задаёт
    // текст промпта, а схему пришлось бы держать в синхроне с моделями руками. На claude-путь
    // не влияет (там контракт задаётся промптом, как и раньше).
    Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default);

    // Только локаль, БЕЗ фолбэка на платный claude. null — локаль выключена/недоступна/пусто.
    // Для необязательных «украшений» (суть уведомления), где платный вызов нежелателен.
    Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default);

    // Бесплатная часть цепочки: прямой адаптер агрегатора → локальная модель, БЕЗ claude.
    // Шире RunLocalOnlyAsync (та требует именно Kind=Local и живой Ollama) и уже RunAsync
    // (та в конце всегда платит claude). Для «украшений», которые нужны на каждом шаге
    // сценария и потому не должны стоить денег, но и локалью не ограничены.
    // null — бесплатного маршрута нет либо он не дал ответа; вызывающий деградирует молча.
    Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default);

    // То же, что RunAsync, но с расходом вызова (OneShotResult.Usage) — для действий, которым
    // важна стоимость (сводка «Что нового»). На claude-пути usage приходит как раньше; на
    // локали и прямом адаптере usage=null (бесплатно — стоимости нет, что для них корректно).
    // timeout/maxTokens перекрывают профиль действия — для тяжёлых задач с собственными лимитами
    // (changelog: свой большой таймаут, длинный JSON-ответ). null → значения из профиля.
    // jsonFormat — как в RunAsync: structured output локального шага (LLM-канал модулей
    // передаёт сюда responseFormat из §10.3 — просьбу, а не гарантию).
    Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
        object? jsonFormat = null, CancellationToken ct = default);
}
