using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.CodeGraph;

// Slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A): хабы по
// связности + правило навигации. Структурный slice + статичное правило «какой
// инструмент навигации по коду звать» (ADR-011 шаг 3) едут двумя секциями из
// одного контрибьютора: иначе пришлось бы дублировать гейт IsEnabled (он один и
// тот же: проектный чат с включённым codegraph) и есть риск рассинхрона.
//
// IsEnabled: провайдер подключён, rootPath не пустой, у владельца tool:codegraph
// не выключен Off-привязкой (прежний BuildCodeGraphProvider).
//
// Семантика гейта — серверная deny-only (`IPersonaServerToolGate`):
// если персоны у сессии нет — гейт не мешает (нет Off-привязки = включено); если
// персона есть — её явная Off-привязка на «codegraph» отключает секцию. Эффективный
// гейт EffectiveToolEnabled здесь неуместен: «codegraph» в ServerKeys, и Persona.Tools
// о нём никогда не знал (включая дефолт «без ограничений») — переключение на
// Effective сломало бы типичную персону с суженным Tools = [tasks, notes].
//
// Этап 5, шаг 6 (инверсия контрибьюторов промпта): контрибьютор переехал в вертикаль
// CodeGraph из Services/Turn — Turn больше не знает про этот кусок. Регистрируется
// в `CodeGraphSubsystem.Register` через DI как `IPromptSectionContributor`; Turn
// собирает `IEnumerable<IPromptSectionContributor>` и натравливает на шину, порядок
// секций задаётся `Order` (см. `TurnEventBus.ApplyAsync`). Гейт идёт через Core-шов
// `IPersonaServerToolGate` (узкая часть прежнего `PersonaBindingsService.ServerToolEnabled`),
// чтобы не тянуть root Services в вертикаль.
public sealed class CodeGraphContributor : IPromptSectionContributor
{
    private readonly CodeGraphPromptProvider? _provider;
    private readonly IPersonaServerToolGate _toolGate;
    private readonly ILogger<CodeGraphContributor> _log;

    public CodeGraphContributor(CodeGraphPromptProvider? provider,
        IPersonaServerToolGate toolGate, ILogger<CodeGraphContributor> log)
    {
        _provider = provider;
        _toolGate = toolGate;
        _log = log;
    }

    public string Key => "code-graph";
    public string Title => "Главные узлы кода проекта";
    public int Order => 600;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        _provider is not null
        && sessionContext.ServerContent
        && !string.IsNullOrWhiteSpace(sessionContext.RootPath)
        && _toolGate.IsServerToolEnabled(sessionContext.OwnerId, sessionContext.Persona, "codegraph");

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (_provider is null) return null;
        string? codeGraphBlock = null;
        try
        {
            // RootPath — рабочая директория сессии (для worktree-чата это ветка чата);
            // MainRootPath — корень ГЛАВНОЙ ветки проекта, fallback для slice пока граф
            // worktree-ветки ещё не построен (ADR-003). Совпадение корней — обычный чат
            // без worktree: GetSliceAsync сводит fallback к no-op.
            codeGraphBlock = await _provider.GetSliceAsync(sessionContext.RootPath, sessionContext.MainRootPath);
        }
        catch (Exception ex)
        {
            // блок графа не должен ронять ход (прежнее поведение)
            _log.LogWarning(ex, "Code Graph slice для {Owner}", sessionContext.OwnerId);
        }

        // Статичное правило выбора codegraph / LSP / Grep едет всегда, когда провайдер
        // активен: иначе оно советовало бы инструменты, которых у хода нет.
        var sections = new List<PromptSection>
        {
            new(Key, codeGraphBlock ?? string.Empty),
            new PromptSection(
                "code-navigation",
                CodeNavigationPrompts.SectionText),
        };
        return new PromptSectionContribution(sections);
    }
}