namespace ClaudeHomeServer.Services.Turn;

// Узкий шов PersonaBindingsService для выноса Turn (Этап 5). PersonaBindingsContributor
// зовёт только `BuildTurnBlockAsync` — собирает блок «Привязанные знания и правила»
// для системного промпта хода (индекс активных привязок + выжимки Always-источников
// по тексту хода). Полный PersonaBindingsService содержит EffectiveToolEnabled,
// ValidateAsync, BuildSubagentIndex и каталог Tool-привязок — Turn'у нужен только
// один метод.
//
// Сигнатура 1:1 с `PersonaBindingsService.BuildTurnBlockAsync`, чтобы адаптер в
// Main был тривиальным forward'ом. null — привязок нет или индекс пустой.
//
// Шов в Core/Services/Turn (рядом с IPromptSectionContributor, IAgentPromptSource,
// ITeamMechanicsBlockSource) — прецедент «узкие швы слоя промпта» уже там же.
public interface IPersonaBindingsSource
{
    // Собрать блок привязок для системного промпта хода. null — привязок нет,
    // или ни одна не попала в индекс, или в Always-выжимках пусто.
    Task<string?> BuildTurnBlockAsync(
        string ownerId,
        string personaId,
        string turnText,
        IReadOnlyList<string> mountedSections);
}
