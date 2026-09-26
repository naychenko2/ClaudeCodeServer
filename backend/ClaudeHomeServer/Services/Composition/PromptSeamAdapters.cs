using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Composition;

// Адаптеры узких швов, которыми контрибьюторы промпта (Services/Turn) заменили прямые
// ссылки на вертикали (Этап 5, узкие швы Turn). Тонкие обёртки один-к-одному по образцу
// `SpendAdapters`: вызов приходит из контрибьютора, адаптер форвардит в фасад вертикали.
// Регистрация — в композиционном корне (Program.cs), не в вертикали: адаптер знает обе
// стороны, и это единственное место, которому это позволено.

public sealed class PersonaRecallSourceAdapter(PersonaMemoryService memory) : IPersonaRecallSource
{
    public bool DossierRecallAvailable => memory.DossierRecallAvailable;

    public async Task<PersonaRecallBlock?> BuildRecallAsync(
        string ownerId, string personaId, string query, int topK, double minScore,
        DossierRecallRequest? dossierRequest, bool splitDossier)
    {
        var recall = await memory.BuildRecallAsync(
            ownerId, personaId, query, topK, minScore, dossierRequest, splitDossier);
        if (recall is null) return null;

        // Проекция в Core-DTO: наружу идут только Id и текст записи — скоринг, теги и
        // даты остаются внутри вертикали. Паспорта (`ChangeDossier`) — уже Core-модель.
        return new PersonaRecallBlock(
            recall.Text,
            recall.DossierText,
            [.. recall.Hits.Select(h => new PersonaRecallEntry(h.Id, h.Text))],
            [.. recall.TeamHits.Select(e => new PersonaRecallEntry(e.Id, e.Text))],
            recall.DossierHits);
    }
}

public sealed class AgentPromptSourceAdapter(SkillsService skills) : IAgentPromptSource
{
    public string? GetAgentSystemPrompt(string projectRootPath, string agentFileName) =>
        skills.GetAgentSystemPrompt(projectRootPath, agentFileName);
}

public sealed class TeamMechanicsBlockAdapter(SkillsService skills) : ITeamMechanicsBlockSource
{
    public string? BuildBlock() => TeamMechanicsPromptCatalog.BuildPromptBlock(InstalledSkillNames());

    // Перенесено из PersonaLayerContributor без изменений, включая гашение исключений:
    // сорванное чтение каталогов умений оставляет пустое множество, и блок всё равно
    // собирается — из механик, которым скилл не нужен (RequiredSkill == null).
    private IReadOnlySet<string> InstalledSkillNames()
    {
        try
        {
            return skills.GetGlobalSkills().Select(s => s.Name)
                .Concat(skills.GetGlobalWorkflows().Select(s => s.Name))
                .Concat(skills.GetPluginSkills().Select(s => s.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

// Этап 5 (Turn): ещё 4 узких шва, чтобы Turn зависел только от Core. Тонкие обёртки
// 1:1, форвардят в фасад корневого сервиса (FeatureFlagService, PersonaManager,
// PersonaPromptBuilder, PersonaBindingsService) — контрибьюторы промпта ссылаются
// только на шов, иначе Turn тащил бы Main ради 4 типов.

public sealed class FeatureFlagGateAdapter(FeatureFlagService flags) : IFeatureFlagGate
{
    public bool IsEnabled(string userId, string key) => flags.IsEnabled(userId, key);
}

public sealed class PersonaResolverAdapter(PersonaManager personas) : IPersonaResolver
{
    public Persona? Get(string id, string userId) => personas.Get(id, userId);
}

public sealed class PersonaPromptAssemblerAdapter(PersonaPromptBuilder builder) : IPersonaPromptAssembler
{
    public string Build(Persona persona, string? model, bool switched = false, bool greeted = false,
        string? teamMechanicsBlock = null, bool voiceMode = false, string? voiceStyle = null) =>
        builder.Build(persona, model, switched, greeted, teamMechanicsBlock, voiceMode, voiceStyle);
}

public sealed class PersonaBindingsSourceAdapter(PersonaBindingsService bindings) : IPersonaBindingsSource
{
    public Task<string?> BuildTurnBlockAsync(string ownerId, string personaId, string turnText,
        IReadOnlyList<string> mountedSections) =>
        bindings.BuildTurnBlockAsync(ownerId, personaId, turnText, mountedSections);
}

// Этап 5 (Skills): два узких шва, чтобы Llm не зависел от SkillsService. Те же адаптеры
// 1:1, что и выше — форвардят в фасад SkillsService. Регистрация — в композиционном корне.

public sealed class CommandExpansionAdapter(SkillsService skills) : ICommandExpansion
{
    public string? ExpandSkill(string message) => skills.TryExpandSkill(message);
}

public sealed class SkillSnapshotSourceAdapter(SkillsService skillsService) : ISkillSnapshotSource
{
    // Сборка двух каталогов (профильные + проектные) с теми же гашениями исключений, что
    // были в ClaudeSession.BuildCliLayerFilesInternal: сорванное чтение одного каталога не
    // убивает второй, ошибка идёт в stderr, снимок продолжает собираться (у пустого
    // списка Skills снимок всё равно валиден — секция просто не появляется).
    public IReadOnlyList<CliSkillDto>? GetCliSkills(string projectRootPath, string? configRootPath)
    {
        var result = new List<CliSkillDto>();

        if (!string.IsNullOrEmpty(configRootPath))
        {
            try
            {
                result.AddRange(skillsService.GetSkillsInConfigRoot(configRootPath)
                    .Select(s => new CliSkillDto(s.Name, s.Description, "profile")));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SkillSnapshotSourceAdapter] Профильный каталог не прочитан: {ex.Message}");
            }
        }

        try
        {
            result.AddRange(skillsService.GetProjectSkills(projectRootPath)
                .Select(s => new CliSkillDto(s.Name, s.Description, "project")));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SkillSnapshotSourceAdapter] Проектный каталог не прочитан: {ex.Message}");
        }

        return result.Count > 0 ? result : null;
    }
}

// Волна NotesToolset (Этап 5, финал извлекаемости Notes): швы контекста вызова
// MCP-over-HTTP-тулсетов. Адаптеры 1:1 форвардят в god-объекты Main.
// Регистрация — в Program.cs (композиционный корень).

public sealed class McpSessionAccessorAdapter(SessionManager sessions) : IMcpSessionAccessor
{
    public Session? GetOwned(string sessionId, string ownerId) =>
        sessions.GetOwned(sessionId, ownerId);
}

// Тулсет редактора картинок тратит деньги: запрет и на делегированном ходу, и на
// реакционном ходу доклада; вызов без вызывателя — отказ (fail-closed, ADR-018 §2)
public sealed class DelegatedTurnGateAdapter(SessionManager sessions) : IDelegatedTurnGate
{
    public string? Deny(string ownerId, string callerSessionId, string action)
    {
        var decision = ClaudeHomeServer.Filters.DelegatedTurnGate.Decide(sessions, ownerId, callerSessionId, action,
            alsoWhenExecutorSuppressed: true, allowInTeamImplement: false, failOpenWhenUnknown: false);
        return decision.Allowed ? null : decision.DenyText;
    }
}

public sealed class McpPersonaBindingsAdapter(PersonaBindingsService bindings) : IMcpPersonaBindings
{
    public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key) =>
        bindings.EffectiveToolEnabled(ownerId, persona, key);
    public bool SectionEnabled(string? ownerId, Persona? persona, string key) =>
        bindings.SectionEnabled(ownerId, persona, key);
}
