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
// - `ICommitLogReader` (`Core/Services/Composition`) — узкий шов на чтение git-лога
//   (Этап 5, ярус 1, волна A): вырвали из FileService.GetCommitsRaw. Сам метод
//   остаётся в FileService — у него другие вызывающие, это отдельная уборка.
//   Кеш сводок (`data/changelog/product.json`) пишется через `System.IO.File`
//   напрямую — это продуктовый путь, не SafeJoin, синк знаний его не обслуживает.
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