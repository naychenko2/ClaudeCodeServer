namespace ClaudeHomeServer.Services.Turn;

// Slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A): хабы по
// связности + правило навигации. Структурный slice + статичное правило «какой
// инструмент навигации по коду звать» (ADR-011 шаг 3) едут двумя секциями из
// одного контрибьютора: иначе пришлось бы дублировать гейт IsEnabled (он один и
// тот же: проектный чат с включённым codegraph) и есть риск рассинхрона.
//
// IsEnabled: провайдер подключён, rootPath не пустой, у владельца tool:codegraph
// не выключен Off-привязкой (прежний BuildCodeGraphProvider).
//
// Семантика гейта — серверная deny-only (PersonaBindingsService.ServerToolEnabled):
// если персоны у сессии нет — гейт не мешает (нет Off-привязки = включено); если
// персона есть — её явная Off-привязка на «codegraph» отключает секцию. Эффективный
// гейт EffectiveToolEnabled здесь неуместен: «codegraph» в ServerKeys, и Persona.Tools
// о нём никогда не знал (включая дефолт «без ограничений») — переключение на
// Effective сломало бы типичную персону с суженным Tools = [tasks, notes].
public sealed class CodeGraphContributor : IPromptSectionContributor
{
    private readonly CodeGraph.CodeGraphPromptProvider? _provider;
    private readonly PersonaBindingsService _bindings;
    private readonly ILogger<CodeGraphContributor> _log;

    public CodeGraphContributor(CodeGraph.CodeGraphPromptProvider? provider,
        PersonaBindingsService bindings, ILogger<CodeGraphContributor> log)
    {
        _provider = provider;
        _bindings = bindings;
        _log = log;
    }

    public string Key => "code-graph";
    public string Title => "Главные узлы кода проекта";
    public int Order => 600;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        _provider is not null
        && !string.IsNullOrWhiteSpace(sessionContext.RootPath)
        && _bindings.ServerToolEnabled(sessionContext.OwnerId, sessionContext.Persona, "codegraph");

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (_provider is null) return null;
        string? codeGraphBlock = null;
        try
        {
            codeGraphBlock = await _provider.GetSliceAsync(sessionContext.RootPath);
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
                Prompts.CodeNavigationPrompts.SectionText),
        };
        return new PromptSectionContribution(sections);
    }
}