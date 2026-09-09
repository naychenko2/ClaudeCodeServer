using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Turn;

// Карта событий шины TurnEventBus (подробности — ADR-013). Имя события (const Event) —
// канонический ключ для доки и логов; соответствие кода и таблицы в ADR проверяет
// TurnEventsMapContractTests.
//
// Правило: в UI события НЕ ходят. Дорога к клиенту одна — ServerMessage/OnMessage;
// шина второй не заводит.

// Этап 5, шаг 6: record-тип PromptSection переехал в Core
// (`Core/Services/Turn/IPromptSectionContributor.cs`) — контрибьюторы в чужих
// вертикалях (CodeGraph, Notes, …) ссылаются на Core-DTO, импорт Turn не нужен.

// prompt/assembling — Filter: цепочка подписчиков собирает секции системного промпта.
// Класс, а не record: Sections — мутируемый вход/выход waterfall (подписчик добавляет свою
// секцию или правит чужую), TurnText — текст хода для recall-провайдеров, Session —
// per-session снимок, который контрибьюторы читают для IsEnabled и BuildAsync, а
// ManifestItems — параллельный выход waterfall'а для F3 «использовано сейчас»:
// контрибьюторы recall-секций добавляют туда айтемы (kind note/memory/team/dossier),
// ClaudeSession после фильтра отдаёт их клиенту одним RecallManifestMessage.
// Слой персоны едет ОТДЕЛЬНОЙ секцией Key="persona-layer" и клеится Combine через
// PersonaSeparator после тела (CLAUDE.md, «Голосовой режим чата»); контрибьюторы
// его НЕ дублируют.
public sealed class PromptAssembling : ITurnFilter
{
    public const string Event = "prompt/assembling";

    public PromptAssembling(TurnContext turn, PromptSessionContext session,
        string? turnText = null,
        IEnumerable<PromptSection>? sections = null,
        IEnumerable<RecallItem>? manifestItems = null)
    {
        Turn = turn;
        Session = session;
        TurnText = turnText;
        Sections = sections is null ? new List<PromptSection>() : new List<PromptSection>(sections);
        ManifestItems = manifestItems is null
            ? new List<RecallItem>()
            : new List<RecallItem>(manifestItems);
    }

    public TurnContext Turn { get; }
    public PromptSessionContext Session { get; }
    public string? TurnText { get; }
    public List<PromptSection> Sections { get; }
    public List<RecallItem> ManifestItems { get; }
}

// Этап 1: данные для подписчика снимка промпта (PromptSnapshotStore). Snapshot=null у хода
// без снимков и для подписчиков, которым содержимое не нужно. Phase различает первичную
// запись (Draft) и дозапись состава инструментов из system/init (Tools). На Tools нужен
// уже записанный SnapshotId — иначе дописывать нечего, и событие уходит в пустоту.
public enum PromptSnapshotPhase
{
    Draft,
    Tools,
}

public sealed record PromptSnapshotPayload(
    PromptSnapshotPhase Phase,
    Protocol.PromptSnapshotDraft? Draft = null,
    string? SnapshotId = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyList<Protocol.McpServerInfo>? McpServers = null);

// prompt/assembled — промпт хода склеен и уходит в процесс; правки уже поздно.
// Snapshot несут ходы, у которых сессия ведёт снимки (этап 1 — переезд PromptSnapshotSink
// и PromptSnapshotToolsSink с полей контекста на шину). Для остальных подписчиков поле
// опциональное и обычно null: подписчик снимков отличает «событие моё» именно по нём.
public sealed record PromptAssembled(
    TurnContext Turn,
    string Prompt,
    IReadOnlyList<string> SectionKeys,
    PromptSnapshotPayload? Snapshot = null) : ITurnNotification
{
    public const string Event = "prompt/assembled";
}

// subagent/completed — сабагент хода завершился. Подписчик: session manager — пишет
// паспорт в SubagentRunLog и взводит side-effects (TruncatedSubagent/TruncatedBgNote),
// по которым политика добиваний отличает обрывок от итога. Этап 1 — переезд
// SubagentRunSink с поля контекста на шину. Не плодить новое переименование в
// turn/completed: иначе side-effects «обрыва посреди хода» ехали бы к самому концу
// хода, и директивы добиваний теряли бы своевременность.
public sealed record SubagentRunCompleted(TurnContext Turn, SubagentRunPassport Passport)
    : ITurnNotification
{
    public const string Event = "subagent/completed";
}

// turn/completed — ход завершился. Outcome — исход паспорта хода (success | failed |
// egress_down | interrupted | cancelled | crashed), ErrorClass — класс ошибки
// (TurnErrorClassifier) у неуспешного исхода; строки, а не enum'ы — чтобы шина не
// зависела от внутренних типов слоя LLM.
//
// Этап 1: Passport добавляется, чтобы подписчик TurnRunLog заменил прямой finally-
// вызов в FallbackLlmSessionAdapter единственной публикацией на шине и сохранил
// контракт «ровно один источник записи». CompactOutcome даёт подписчикам, которым
// достаточно исхода, не зависеть от типа TurnRunPassport.
public sealed record TurnCompleted(
    TurnContext Turn,
    string Outcome,
    string? ErrorClass = null,
    TurnRunPassport? Passport = null) : ITurnNotification
{
    public const string Event = "turn/completed";
}
