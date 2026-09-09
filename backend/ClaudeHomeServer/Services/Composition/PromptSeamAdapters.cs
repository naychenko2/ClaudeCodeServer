using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Memory;

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
