namespace ClaudeHomeServer.Services.Dossiers;

// Шов пассивного recall паспортов изменений (Этап 5, волна 3 сцепки Memory↔Dossiers).
// Ровно один метод — столько и зовёт единственный потребитель: `PersonaMemoryService`
// подмешивает блок паспортов в auto-recall персоны (`BuildRecallAsync`). Раньше он
// держал прямую ссылку на `Dossiers.DossierRecallService`, и это было последнее
// «вертикаль → вертикаль» между двумя подсистемами.
//
// Контрактные DTO (`DossierRecallRequest`/`DossierRecallResult`) лежат рядом, в
// `DossierRecallContracts.cs` — они уехали в спину раньше, узкими швами Turn.
//
// Реализация — `DossierRecallService`, форвардер регистрирует `DossiersSubsystem`
// (сторона-поставщик объявляет, что исполняет Core-контракт).
//
// ⚠ Потребитель принимает шов ОПЦИОНАЛЬНО (`IDossierRecallSource?`): «канала паспортов
// нет» — штатное состояние (юнит-тесты, владелец без флага `change-dossiers-recall`), а
// не ошибка. На этом стоит публичный `PersonaMemoryService.DossierRecallAvailable`.
public interface IDossierRecallSource
{
    Task<DossierRecallResult> BuildRecallBlockAsync(string ownerId, DossierRecallRequest req);
}
