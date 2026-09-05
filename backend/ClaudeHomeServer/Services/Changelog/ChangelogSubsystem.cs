using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Changelog;

// Подсистема «Что нового» (волна 4A, шаг 2): продуктовая история по коммитам всех
// проектов с фоновым прогревом кеша.
//
// Известные границы (сознательные):
// - `Services.Llm.ICheapTextRunner` — префикс-шов (как у `Backgrounds`/`Git`/`Deploy`):
//   дневная сводка идёт через дешёвую модель общего назначения, используемую и
//   другими разделами (теги заметок, сводки, память). Расширять `SharedAllowedPrefixes`
//   им нельзя — это открыло бы любой подсистеме весь `Services.Llm`.
// - `FileService` (`Services`-корень) — точечный: чтение коммитов и кеша сводок
//   (ChangelogService читает `data/changelog/product.json` и git-вывод).
public sealed class ChangelogSubsystem : IAppSubsystem
{
    public string Key => "changelog";

    public string Title => "«Что нового»";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ChangelogService>();
        services.AddGatedHostedService<ChangelogWarmupService>(config);
    }
}