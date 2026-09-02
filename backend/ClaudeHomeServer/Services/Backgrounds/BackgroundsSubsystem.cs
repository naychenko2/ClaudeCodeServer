using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Backgrounds;

// Подсистема «Фоны проектов» (ADR-008): промпт по смыслу проекта → JSON с фигурами от
// модели → собранный сервером SVG-тайл + разовый backfill существующих проектов на старте.
//
// Известные границы (сознательные):
// - `ProjectBackground` / `ProjectBackgroundKind` живут в `Models/Project.cs` — это
//   доменная модель проекта, а не внутренняя деталь вертикали. Вынос в собственные
//   модели потребовал бы смены `projects.json` и инкремента `BackupSchema.Version`.
// - Стартовый прогон идёт через `AddGatedHostedService<ProjectBackgroundBackfillService>`
//   (см. `Services/Composition/SubsystemHostingExtensions.cs`): в Testing без явного
//   `Testing:EnableHostedServices=true` hosted НЕ регистрируется — иначе 27 буто́в
//   тестовых хостов гоняли бы прогон на ровном месте.
// - `Services/Llm.ICheapTextRunner` — префикс-шов (как у `Git`/`Deploy`): фоновый
//   one-shot через дешёвую модель общего назначения, используемую и другими разделами
//   (теги заметок, сводки, память). Расширять `SharedAllowedPrefixes` им нельзя —
//   это открыло бы любой подсистеме весь `Services.Llm`.
public sealed class BackgroundsSubsystem : IAppSubsystem
{
    public string Key => "backgrounds";

    public string Title => "Фоны проектов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ProjectBackgroundService>();
        services.AddSingleton<ProjectBackgroundBackfill>();
        services.AddGatedHostedService<ProjectBackgroundBackfillService>(config);
    }
}
