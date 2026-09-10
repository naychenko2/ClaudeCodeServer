using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Dossiers;

namespace ClaudeHomeServer.Services.Memory;

// Узкий шов долгой памяти персоны для сборки промпта хода (Этап 5, узкие швы Turn).
// Контрибьютор `PersonaRecallContributor` звал у `PersonaMemoryService` РОВНО один метод —
// `BuildRecallAsync`, — но тянул за собой всю вертикаль Memory: сам фасад, тип хита
// (`PersonaMemoryHit`) и вложенный результат (`PersonaMemoryService+PersonaRecallResult`),
// три допуска в стороже границ.
//
// Признак `DossierRecallAvailable` заменил ВТОРУЮ зависимость того же контрибьютора —
// `Dossiers.DossierRecallService?`, которую он держал только ради проверки «is not null»
// (сам сервис он не звал ни разу: recall паспортов выполняет `PersonaMemoryService`
// своим экземпляром того же сервиса). Спрашивать «есть ли канал паспортов» правильно
// у того, кто по этому каналу пойдёт, а не у второго экземпляра ссылки.
public interface IPersonaRecallSource
{
    // Подключён ли канал паспортов изменений (ADR-004 §5). false — запрос
    // `DossierRecallRequest` собирать незачем, паспорта в блок не попадут.
    bool DossierRecallAvailable { get; }

    // Markdown-блок памяти для системного промпта хода + записи, реально попавшие в блок.
    // null — персона не найдена / память выключена. Аргументы один-в-один с
    // `PersonaMemoryService.BuildRecallAsync`; необязательных значений у шва нет
    // намеренно — вызывающий один, дефолты фасада ему не нужны.
    Task<PersonaRecallBlock?> BuildRecallAsync(
        string ownerId, string personaId, string query, int topK, double minScore,
        DossierRecallRequest? dossierRequest, bool splitDossier);
}

// Запись памяти, попавшая в блок recall: только то, что нужно манифесту атрибуции F3
// («персона опирается на…») — идентификатор и текст. Скоринг, теги и даты остаются
// внутри вертикали: потребитель промпта их не читает.
public sealed record PersonaRecallEntry(string Id, string Text);

// Блок recall для промпта: текст основной секции, текст выделенной секции паспортов
// (splitDossier=true, иначе null — досье остаётся внутри Text) и три набора записей
// для манифеста. `ChangeDossier` — уже Core-модель, пересказывать её незачем.
public sealed record PersonaRecallBlock(
    string? Text,
    string? DossierText,
    IReadOnlyList<PersonaRecallEntry> Hits,
    IReadOnlyList<PersonaRecallEntry> TeamHits,
    IReadOnlyList<ChangeDossier> DossierHits);
