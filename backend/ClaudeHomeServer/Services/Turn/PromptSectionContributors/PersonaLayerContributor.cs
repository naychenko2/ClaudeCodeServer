using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Services.Turn;

// Слой персоны: промпт персоны имеет приоритет над .md-агентом — чат ведётся от её
// лица, характер задаёт именно персона. Едет ОТДЕЛЬНОЙ секцией Key="persona-layer"
// и клеится TurnPromptAssembler.Combine через PersonaSeparator ПОСЛЕ тела (CLAUDE.md,
// «Голосовой режим чата»): без отдельной склейки «Формат ответов» персоны перебил бы
// голосовое правило.
//
// Контрибьютор выезжает на каждый ход (характер, оверлеи — голос, группа, онбординг),
// поэтому дёргает сервисы живьём, а не кэширует.
//
// Один контрибьютор на persona-layer — особенный случай: секция обязана идти ПОСЛЕ
// всех остальных секций (Order=900). Голосовая оговорка digest клеится Combine через
// тот же PersonaSeparator (PersonaPromptBuilder.Build дописывает DigestPersonaOverride
// в конец слоя персоны), поэтому оговорка оказывается последним непустым текстом
// всего --append-system-prompt.
public sealed class PersonaLayerContributor : IPromptSectionContributor
{
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly PersonaPromptBuilder _promptBuilder;
    private readonly ProjectManager _projects;
    private readonly SkillsService? _skills;
    private readonly Func<bool> _personasEnabled;

    public PersonaLayerContributor(
        UserStore users,
        PersonaManager personas,
        PersonaPromptBuilder promptBuilder,
        ProjectManager projects,
        SkillsService? skills,
        // Резолвер «разрешены ли персоны для этой сессии» — определяет, добавлять ли
        // подсказку про создание персоны в онбординг-оверлей. Без него (тесты) —
        // оверлей деградирует к минимуму.
        Func<bool>? personasEnabled = null)
    {
        _users = users;
        _personas = personas;
        _promptBuilder = promptBuilder;
        _projects = projects;
        _skills = skills;
        _personasEnabled = personasEnabled ?? (() => true);
    }

    public string Key => "persona-layer";
    public string Title => "Кто она: роль и характер";
    // ПОСЛЕДНИМ — после dossier-trailer (100), recall-notes (200), recall-memory (300),
    // prompt-sections (400), persona-bindings (500), code-graph (600).
    public int Order => 900;
    public string Group => "persona";

    // IsEnabled = есть кандидат на слой: либо persona prompt, либо skills agent-prompt
    // (файловый .md-агент при пустой персоне). Прежний код:
    //   agentPrompt = _personaPromptProvider?.Invoke();
    //   if (agentPrompt is null && !string.IsNullOrEmpty(Info.AgentName) && _skills is not null)
    //       agentPrompt = _skills.GetAgentSystemPrompt(_rootPath, Info.AgentName);
    public bool IsEnabled(PromptSessionContext sessionContext)
    {
        if (sessionContext.OwnerId is null) return false;
        var session = sessionContext.Session;

        // Онбординг пользователя: персоны ещё нет, мастер настройки ведёт чат.
        if (session.OnboardingKind == OnboardingKinds.User && session.PersonaId is null) return true;
        // Обычная персонная сессия.
        if (session.PersonaId is not null) return true;
        // Файловый агент (.md) при пустой персоне — fallback.
        if (!string.IsNullOrEmpty(session.AgentName) && _skills is not null) return true;
        return false;
    }

    public Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is null) return Task.FromResult<PromptSectionContribution?>(null);
        var ownerId = sessionContext.OwnerId;
        var session = sessionContext.Session;

        string? agentPrompt = null;

        // 1) Онбординг пользователя без персоны: мастер настройки тем же каналом.
        if (session.OnboardingKind == OnboardingKinds.User && session.PersonaId is null)
        {
            var owner = _users.GetById(ownerId);
            var assistantId = owner?.AssistantPersonaId;
            if (assistantId is { } aid && _personas.Get(aid, ownerId) is { } draft)
                agentPrompt = Prompts.OnboardingPrompts.UserMaster(
                    owner?.DisplayName ?? owner?.Username, draft.Id, draft.Name);
            else
                agentPrompt = Prompts.OnboardingPrompts.UserMaster(owner?.DisplayName ?? owner?.Username);
        }
        // 2) Обычная персонная сессия.
        else if (session.PersonaId is not null)
        {
            var persona = _personas.Get(session.PersonaId, ownerId);
            if (persona is null)
            {
                // персона удалена — слой не добавляется
                return Task.FromResult<PromptSectionContribution?>(null);
            }
            var built = _promptBuilder.Build(persona, session.Model, session.PersonaSwitched,
                greeted: !string.IsNullOrWhiteSpace(persona.Greeting),
                teamMechanicsBlock: BuildTeamMechanicsBlock(session, persona),
                voiceMode: session.VoiceMode,
                voiceStyle: session.TaskExecution || session.AutomationRuleId is not null
                    ? VoiceStyles.Talk
                    : session.VoiceStyle);

            // Групповой чат: надстройка со списком участников и правилом «говори только за себя».
            if (session.Participants is { Count: > 1 } memberIds)
            {
                var members = memberIds.Select(id => _personas.Get(id, ownerId))
                    .OfType<Persona>().ToList();
                if (members.Count > 1) built += "\n\n" + BuildGroupChatHint(persona, members);
            }
            // Проектный онбординг: надстройка наставника поверх слоя личной дефолт-персоны.
            if (session.OnboardingKind == OnboardingKinds.Project && session.ProjectId is { } prjId
                && _projects.GetById(prjId) is { } prj
                && Prompts.OnboardingPrompts.ProjectOverlayActive(prj))
            {
                built += "\n\n" + Prompts.OnboardingPrompts.ProjectOnboardingOverlay(
                    prj.Name, prj.PresetKey, _personasEnabled());
            }
            agentPrompt = built;
        }
        // 3) Файловый .md-агент при пустой персоне — fallback.
        else if (!string.IsNullOrEmpty(session.AgentName) && _skills is not null)
        {
            agentPrompt = _skills.GetAgentSystemPrompt(sessionContext.RootPath ?? "", session.AgentName);
        }

        if (string.IsNullOrWhiteSpace(agentPrompt))
            return Task.FromResult<PromptSectionContribution?>(null);

        return Task.FromResult<PromptSectionContribution?>(
            new PromptSectionContribution([new PromptSection(Key, agentPrompt!)]));
    }

    // Блок «Командные механики» для руководителя проекта (мост в механики). Только
    // когда персона чата — дефолт-персона проекта (Project.DefaultPersonaId).
    // Без SkillsService (тесты) — механики без скилла, не падаем.
    private string? BuildTeamMechanicsBlock(Session session, Persona persona)
    {
        if (session.ProjectId is not { } projectId) return null;
        var project = _projects.GetById(projectId);
        if (project is null || project.DefaultPersonaId != persona.Id) return null;
        if (_skills is null) return null;
        return TeamMechanicsPromptCatalog.BuildPromptBlock(InstalledSkillNames());
    }

    private IReadOnlySet<string> InstalledSkillNames()
    {
        if (_skills is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return _skills.GetGlobalSkills().Select(s => s.Name)
                .Concat(_skills.GetGlobalWorkflows().Select(s => s.Name))
                .Concat(_skills.GetPluginSkills().Select(s => s.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Групповая надстройка промпта: участники чата + дисциплина «отвечай только от
    // своего лица». Добавляется к persona-слою активного спикера.
    private static string BuildGroupChatHint(Persona self, IReadOnlyList<Persona> participants)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Это ГРУППОВОЙ чат: пользователь общается сразу с несколькими персонами, " +
                      "отвечает та, к кому обращаются (@handle). Участники:");
        foreach (var p in participants)
        {
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.AppendLine($"- @{p.Handle} — {title}{(p.Id == self.Id ? " (это ты)" : "")}");
        }
        sb.AppendLine("Сейчас отвечаешь ты. Отвечай ТОЛЬКО от своего лица и в своём характере — " +
                      "НЕ сочиняй и не пиши реплики за других участников.");
        sb.Append("Если пользователь обращается ко всем или просит мнение другого участника — " +
                  "спроси его (способ указан в блоке о консультациях с персонами) и передай " +
                  "суть ответа своими словами, явно указав автора.");
        return sb.ToString();
    }
}