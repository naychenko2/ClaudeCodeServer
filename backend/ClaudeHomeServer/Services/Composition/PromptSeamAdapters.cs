using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Dossiers;
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
