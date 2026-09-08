namespace ClaudeHomeServer.Services.Turn;

// Блок «Привязанные знания и правила» персоны (флаг persona-bindings): индекс источников
// «когда → откуда» + выжимки режима «всегда». Только у персонных сессий; провайдер сам
// гейтит по наличию привязок и секций.
//
// IsEnabled: есть персона (PersonaPromptProvider раньше давал бы её), есть владелец,
// personaId сессии задан. Сам PersonaBindingsService внутри возвращает null, если
// привязок нет или они все Off — BuildAsync отдаст null и секция не добавится.
public sealed class PersonaBindingsContributor : IPromptSectionContributor
{
    private readonly PersonaBindingsService _bindings;
    private readonly ILogger<PersonaBindingsContributor> _log;

    public PersonaBindingsContributor(PersonaBindingsService bindings,
        ILogger<PersonaBindingsContributor> log)
    {
        _bindings = bindings;
        _log = log;
    }

    public string Key => "persona-bindings";
    public string Title => "Знания и правила, привязанные к персоне";
    // После prompt-sections (400), до code-graph (600) — порядок проводки.
    public int Order => 500;
    public string Group => "persona";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is not null
        && sessionContext.Persona is not null
        && !string.IsNullOrEmpty(sessionContext.Session.PersonaId);

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is null || sessionContext.Persona is null) return null;
        if (turnText is null) return null;
        try
        {
            // `?? []` не косметика: `WorkspaceSections` nullable, а `BuildTurnBlockAsync`
            // принимает non-nullable список и отдаёт его в `BuildIndex` без проверки.
            // Пустой список — честное «смонтированных секций нет», null был бы миной.
            var block = await _bindings.BuildTurnBlockAsync(
                sessionContext.OwnerId, sessionContext.Persona.Id, turnText,
                sessionContext.WorkspaceSections ?? []);
            if (string.IsNullOrWhiteSpace(block)) return null;
            return new PromptSectionContribution([new PromptSection(Key, block)]);
        }
        catch (Exception ex)
        {
            // блок привязок не должен ронять ход (прежнее поведение)
            _log.LogWarning(ex, "Блок привязок персоны {Persona}", sessionContext.Persona.Id);
            return null;
        }
    }
}