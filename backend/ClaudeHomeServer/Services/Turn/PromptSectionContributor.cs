using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Turn;

// Результат работы контрибьютора: одна или несколько секций системного промпта и
// (опционально) айтемы манифеста F3 «использовано сейчас». Один контрибьютор может
// породить несколько секций — примеры:
//
//   PersonaRecallContributor → recall-memory + dossier-recall (обе под гейтом MemoryMcp);
//   CodeGraphContributor    → code-graph + code-navigation (статичная подсказка рядом).
//
// Секции НЕ сортируются контрибьютором: порядок задаётся Order, сборка — шиной.
public sealed record PromptSectionContribution(
    IReadOnlyList<PromptSection> Sections,
    IReadOnlyList<RecallItem>? ManifestItems = null);

// Контекст сессии, который шина кладёт в PromptAssembling для контрибьюторов.
// Намеренно лёгкий: ровно то, что нужно для гейта IsEnabled и для вызова сервисов
// контрибьютором. НЕ несёт удалённые 6 полей LlmSessionContext (провайдеры Func<…>) и
// DossierTrailerHint — контрибьюторы получают эти данные сами из своих DI-зависимостей
// и Session. per-owner изоляция — через Session.OwnerId.
//
// MainRootPath — корень ГЛАВНОЙ ветки проекта (project.RootPath), нужен отдельно от
// RootPath (рабочая директория сессии): у чата с отдельным worktree они расходятся, и
// CodeGraphContributor использует MainRootPath как fallback для slice графа (ADR-003),
// пока граф worktree-ветки ещё не построен. null — чат вне проекта, fallback не
// применяется; равен RootPath — обычный чат без worktree, fallback сводится к no-op
// (CodeGraphPromptProvider.GetSliceAsync это уже учитывает).
public sealed record PromptSessionContext(
    Session Session,
    string? OwnerId,
    Persona? Persona,
    string? RootPath,
    string? MainRootPath = null,
    // Снимки «подключён ли MCP-сервер сессии» — нужны для IsEnabled: recall-notes
    // работает только при NotesMcp, recall-memory — только при MemoryMcp. Без этих
    // флагов гейт был бы размазан по контрибьюторам и терялся бы при рефакторинге
    // (golden-фикстура 4 ловит именно потерю гейтинга).
    bool HasNotesMcp = false,
    bool HasMemoryMcp = false,
    bool HasWorkspaceMcp = false,
    // Секции workspace-MCP, реально смонтированные сессией (для PersonaBindings:
    // привязки типов без своей секции пропускаются).
    IReadOnlyList<string>? WorkspaceSections = null);

// Контракт контрибьютора секции системного промпта (этап 2 плана «Шина событий хода»,
// ADR-013). Реестр собирается Filter-событием prompt/assembling; регистрация — через
// DI и SessionManager, по одному экземпляру на инстанс (как шина). per-owner изоляция
// держится на OwnerId сессии в PromptAssembling.Turn, а не на отдельных шинах.
//
// Инвариант: контрибьютор НЕ меняет порядок склейки секций и не трогает склейку
// «слой персоны через TurnPromptAssembler.Combine + PersonaSeparator после всех
// секций» (CLAUDE.md, «Голосовой режим чата»). Чтобы оговорка голосового режима
// клеилась ПОСЛЕДНИМ блоком после слоя персоны, слой персоны едет своей секцией
// Key="persona-layer", а Combine различает её и использует PersonaSeparator.
//
// Исключения внутри BuildAsync ГАСЯТСЯ контрибьютором (как прежние inline-провайдеры):
// recall/граф/привязки не должны ронять ход. Возврат null — секция не добавляется
// (например, ничего не нашлось или вспомогательная секция пуста).
public interface IPromptSectionContributor
{
    // Ключ секции в снапшоте/UI ("recall-notes", "persona-layer"…).
    string Key { get; }

    // Заголовок секции для снапшота промпта (карточка «что ушло модели»).
    string Title { get; }

    // Порядок в склейке: меньше = раньше. Стабильный на всё время жизни бэкенда.
    // Фиксированный шаг (100) — чтобы новые контрибьюторы вставали между
    // существующими без перетряхивания всех Order'ов.
    int Order { get; }

    // Группа секции ("persona" | "mcp" | "project" | "recall" | "misc") — по ней UI
    // считает стоимость слоя персоны.
    string Group { get; }

    // ОБЯЗАТЕЛЬНЫЙ предикат включения. Без него контрибьютор дёрнется на ходах
    // без подключённого notes/memory/persona MCP-контекста и уронит ход NPE
    // (см. ClaudeSession.cs:2632, 2798 — прежний гейт «_recallProvider is not null
    // && _notesMcp is not null»). golden-фикстуры 5/6 (RecallProviderЗаданНо…,
    // PersonaRecallProviderЗаданНо…) ловят именно потерю этого гейтинга.
    bool IsEnabled(PromptSessionContext sessionContext);

    // Построить секции для данной сессии и текста хода. turnText может быть null —
    // контрибьютор сам решит, нужно ли ему что-то искать. Исключения не выпускать.
    Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText);
}