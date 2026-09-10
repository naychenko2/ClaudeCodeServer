using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Turn;

// Узкий шов PersonaPromptBuilder для выноса Turn (Этап 5). PersonaLayerContributor
// зовёт только `Build(...)` — собирает слой системного промпта персоны (идентичность
// + секции контракта + дисциплинарный слой провайдера модели). Полный
// PersonaPromptBuilder содержит BuildForSubagent (для файлового .md-агента, которого
// Turn не касается) и DisciplineFor (внутренний статический каталог — Turn тоже
// не касается).
//
// Контракт ровно повторяет сигнатуру `PersonaPromptBuilder.Build`, чтобы адаптер
// в Main был тривиальным forward'ом без логики.
//
// Шов живёт в Core/Services/Turn (рядом с IPromptSectionContributor) — прецедент
// IAgentPromptSource/ITeamMechanicsBlockSource уже там же.
public interface IPersonaPromptAssembler
{
    // Собрать системный промпт персоны для слоя. Сигнатура 1:1 с PersonaPromptBuilder.Build.
    string Build(
        Persona persona,
        string? model,
        bool switched = false,
        bool greeted = false,
        string? teamMechanicsBlock = null,
        bool voiceMode = false,
        string? voiceStyle = null);
}
