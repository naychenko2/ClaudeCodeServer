using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Sessions;

// Типы-данные контрактов ядра сессий (хвост линии ядра, снятие alias'ов на фасад).
// Раньше жили вложенными в SessionManager, а роли ядра ссылались на них через
// `using X = ClaudeHomeServer.Services.SessionManager.X;` — ребро «ядро → фасад» было
// закрыто только формально. Тела перенесены дословно, поведения здесь нет. TurnResult и
// ChatVisibilityResult/Outcome тоже здесь: ядро не ссылается ни на один тип фасада.

// Атрибуция доставленного хода для лога «Доставка хода» (инцидент 2026-08-10 П3): кто
// инициировал авто-доставку, когда src=auto/origin пустой. Различает точки, прежде бывшие
// неразличимыми (drain очереди / work-loop / доклад исполнителя / обход байпаса), и pinpoint'ит
// источник остаточных повторных доставок после закрытия байпаса (П3 п.3).
public enum DeliveryCause
{
    User,           // hub — человек через SignalR
    QueueUser,      // fromQueue — пользовательское сообщение из Pending
    QueueAgent,     // fromQueue — агентское сообщение из Pending (в т.ч. обход байпаса)
    Direct,         // auto — прямая отправка (SendOrEnqueueAsync при свободном чате: доклад исполнителя и пр.)
    WorkLoop,       // auto — цикл «до готово» (верификация/продолжение)
    SubagentNudge,  // auto — добивание сабагента, оборвавшегося на середине
    ToolCallLeakNudge, // auto — повтор вызова инструмента, ушедшего в текст (локальная модель)
    ServerRestartResume, // auto — возобновление хода, оборванного рестартом сервера (фича turn-auto-resume)
    HoldResume,     // auto — продолжение хода, прерванного паузой фичи (W4-HOLD-D)
    HoldCard,      // auto — карточка оркестратору в ключевой чат при снятии паузы: сама
                  // нотификация, а не продолжение прерванного хода (W4-HOLD-N, хвост R-N)
    Unknown,        // auto — точка не помечена (диагностика: проставить cause)
}

// Снимок пользовательского сообщения, ушедшего в работу, — для возврата в композер
// по «Стоп», если пользовательских в очереди не оказалось
public record UserTurnSnapshot(string Text, IReadOnlyList<string> AttachedPaths, string? Mode);

// Исход постановки пользовательского сообщения (Hub SendMessage возвращает клиенту):
// Started — ход запущен сразу; Queued — чат занят/очередь непуста, сообщение встало в
// серверную очередь и уйдёт по FIFO (оптимистичный баллон рисовать не надо — придёт
// снимок pending_messages, а доставленное вернётся событием user_message);
// QueuedPreempted — то же, но идущий ход при этом пришлось прервать (ждал человека либо
// шёл цикл «до готово»). Клиенту это нужно знать: убитый ход пришлёт голый exited, и без
// отметки о прерывании лента нарисует ложную аварию «AI завершился неожиданно».
public enum SendUserOutcome { Started, Queued, QueuedPreempted }

// Результат отправки с ожиданием: Queued — сессия была занята, сообщение встало в очередь
// и уйдёт после текущего хода; QueueFull — очередь переполнена, сообщение отброшено;
// Completed — ход завершился в срок; Running — ход продолжается (wait=none или истёк таймаут).
// Busy остаётся для совместимости сигнатуры, но занятость сама по себе больше не отказ.
public abstract record SendAndWaitResult
{
    public sealed record Busy(SessionStatus CurrentStatus) : SendAndWaitResult;
    // Dispatched — постановка сама форсировала доставку (очередь была пуста, а ход успел
    // кончиться): вызывающему прерывать нечего, ход уже идёт с этим сообщением
    public sealed record Queued(int Position, bool Duplicate, bool Dispatched = false) : SendAndWaitResult;
    public sealed record QueueFull(int Limit) : SendAndWaitResult;
    public sealed record Completed(TurnResult Result) : SendAndWaitResult;
    public sealed record Running : SendAndWaitResult;
    // Ход ОТКЛОНЁН на приёме (Х-W4-73, дефект 358bfe10): тот же отказ, что у хаб-пути
    // (TurnIntake.ChildTurnRefusal — ход детского чата только его владельца-ребёнка), но
    // возвращённый вызывающему, а не написанный в ленту: REST-канал (chats_send) не должен
    // отвечать «202 running» на ход, который никогда не начнётся. Ничего не началось: ни
    // очереди, ни процесса, ни реплики в ленте получателя — текст отказа принадлежит
    // вызывающему (он и есть тот, кто пытался начать ход).
    public sealed record Refused(string Reason) : SendAndWaitResult;
}

// Значения include ветвления чата (docs/research/chat-branching-2026-09.md §5, §6):
// turn — история по конец хода (кнопка под ответом ассистента), beforePrompt — история
// до этого сообщения, а его текст возвращается как draft для композера (транскрипт
// обязан заканчиваться завершённым ходом — висящий промпт склеился бы со следующим).
public static class ChatBranchInclude
{
    public const string Turn = "turn";
    public const string BeforePrompt = "beforePrompt";
}

// Итог ветвления: новый чат + черновик композера (не null только у include=beforePrompt).
public sealed record ChatBranchResult(Session Session, string? Draft);

// План сервера рабочего пространства — часть контракта фасада: его читает WorkspaceToolset
// по живой сессии (живой tools/list) и тесты. Сам план считает ExternalMcpContexts (S3-4).
internal sealed record WorkspaceMcpPlan(IReadOnlyList<string> Sections,
    IReadOnlyList<string>? AllowedProjectIds);

// Глобальная персона не бывает собеседником чата общего проекта (S2a-8): рамка хода в
// общем проекте узкая для всех, включая арендатора (TurnFrame, решение 29.10), а
// глобальная персона — личное пространство арендатора: её характер и память утекали бы
// в общий чат. Отдельный тип, а не текст: контроллер отвечает code=global_persona_in_shared,
// фронт различает этот отказ среди прочих 400 (по образцу transcript_damaged).
public sealed class GlobalPersonaInSharedException(string message) : InvalidOperationException(message)
{
    public const string Code = "global_persona_in_shared";
}

// Результат попытки отчитаться. TooDeep — цепочка автоотчётов упёрлась в потолок;
// NoParent — у чата нет эффективного родителя (обычный чат либо вынесен в корень).
public enum ReportUpResult { Delivered, Queued, NoParent, TooDeep, NotFound }

// Результат пакетного прогона значков: сколько чатов получили иконку и сколько пропущено
// (значок уже стоит, нет переписки, модель не дала имя). Идёт в тост палитры.
public sealed record IconBatchResult(int Processed, int Skipped);

// Повреждённый транскрипт: resume запрещён, человеку предлагаем начать ход заново.
// Отдельный тип, а не текст в InvalidOperationException: контроллер различает отказ
// гейта (просто текст) и этот случай (code=transcript_damaged + кнопка «начать заново»).
public sealed class TurnTranscriptDamagedException(string message) : InvalidOperationException(message);

// Результат перезапуска хода. Resumed=true — следующий ход продолжит разговор по
// транскрипту (--resume); false — ход начнётся заново без старого контекста (startFresh).
public sealed record StuckTurnRestartResult(bool Resumed, string Message);

// Исход возобновления. Sent=true — resume-ход отправлен (агент продолжит с того же
// места); false — отказ (статус не Orphaned / битый хвост), ход не шлём, в ленте
// остаётся карточка-баннер. Reason — человекочитаемая причина отказа для лога/отчёта.
public sealed record RestartResumeResult(bool Sent, string? Reason = null);

// Исход смены видимости чата (SetChatVisibility, S2d-7a). Отказы различимы для контроллера:
// NotAuthor — 403, NotSharedProject / HasMessages / AttachmentsMoveFailed / BoundToFeature / TeamOrGroup — 409, NotFound — 404.
public enum ChatVisibilityOutcome { Ok, NotFound, NotAuthor, NotSharedProject, HasMessages, AttachmentsMoveFailed, BoundToFeature, TeamOrGroup }

// AttachmentMoves (S2d-6b-4): карта «старый путь → новый» перенесённых вложений — композеру
// заменить ссылки черновика; null — переносить было нечего.
public record ChatVisibilityResult(ChatVisibilityOutcome Outcome, Session? Session)
{
    public IReadOnlyDictionary<string, string>? AttachmentMoves { get; init; }
}

// Итог завершённого хода для синхронного ожидания (SendMessageAndWaitAsync):
// текст последней реплики ассистента, длительность и стоимость (если провайдер её отдал).
// Succeeded — ход завершился штатным result (true); error/«Стоп» (exited) — false: тогда Reply
// это сообщение об ошибке или обрубок, а не готовая реплика.
public record TurnResult(string Reply, long DurationMs, double? CostUsd, bool Succeeded);
