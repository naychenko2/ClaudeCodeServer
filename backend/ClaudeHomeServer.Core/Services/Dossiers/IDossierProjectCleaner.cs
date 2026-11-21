namespace ClaudeHomeServer.Services.Dossiers;

// Шов «уборка паспортов удалённого проекта» (доработка Б8e, сигнал ревью d2842bf4): проект
// сносит корневой ProjectDeletionService — он из корня Services и на вертикаль Dossiers
// напрямую ссылаться не имеет права (root-сторож RootSubsystemBoundaryTests). Реализация —
// `DossierStore`, форвардер регистрирует `DossiersSubsystem` (сторона-поставщик объявляет,
// что исполняет Core-контракт) — ровно тот образец, что у `IDossierRecallSource` выше.
//
// Ровно один метод — столько и зовёт единственный потребитель.
public interface IDossierProjectCleaner
{
    // Уборка паспортов проекта: локальный стор, архивный JSONL и Dify-датасет проекта.
    Task DeleteProjectDossiersAsync(string ownerId, string projectId);
}
