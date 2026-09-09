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
// - `IProjectIconMigrator` / `IDataBackupService` (Core, Этап 5, волна C, шаг 2) —
//   швы для записи `Icon.Glyph` и снимка data перед необратимой операцией.
//   Прежде прямые ссылки на `ProjectManager` и `BackupCore` помечались как
//   полумера (`ProjectIconMigration.cs:78-84`); курс Андрея 2026-09-08 (вынос
//   ВСЕХ вертикалей) делает швы обязательными — формализованы в Core.
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
