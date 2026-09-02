using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ProjectIcons;

// Подсистема «Значки проектов» (ADR-009): двухходовый подбор имени иконки из белого
// списка lucide по смыслу проекта + разовая миграция значков существующим проектам
// на старте.
//
// Известные границы (сознательные):
// - `ProjectGlyph` живёт в `Models/Project.cs` — доменная модель проекта, а не
//   внутренняя деталь вертикали. Вынос сменил бы `projects.json` и потребовал бы
//   инкремента `BackupSchema.Version`.
// - `LucideGlyphs` (белый список имён) и `lucide-icon-names.g.txt` (генерируемая
//   копия, `EmbeddedResource`) живут внутри вертикали — это её собственный ресурс,
//   состав держит `LucideGlyphWhitelistGuardTests`.
// - `Services/Llm.ICheapTextRunner` — префикс-шов (как у `Git`/`Backgrounds`): оба
//   хода подбора идут через дешёвую модель. Префикс, а не расширение
//   `SharedAllowedPrefixes`, чтобы не открывать любой подсистеме весь `Services.Llm`.
// - `BackupCore.Snapshot` / `BackupContext.FromConfiguration` в `ProjectIconMigration`
//   — статические вызовы из тел методов; рефлексия стражей их НЕ видит
//   (см. `SubsystemBoundaryTests`, «Известное ограничение»). Выделение мьютекса
//   деплоя в отдельный примитив для `Deploy` помечено там же TODO — здесь аналогично:
//   явный шов не нужен, пока инвариант держит сам статический вызов и сбой бэкапа
//   останавливает миграцию (см. `ProjectIconMigration.RunAsync`).
public sealed class ProjectIconsSubsystem : IAppSubsystem
{
    public string Key => "project-icons";

    public string Title => "Значки проектов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ProjectIconGlyphService>();
        services.AddSingleton<ProjectIconMigration>();
        services.AddGatedHostedService<ProjectIconMigrationService>(config);
    }
}
