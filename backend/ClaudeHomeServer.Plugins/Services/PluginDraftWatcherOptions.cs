namespace ClaudeHomeServer.Services.Plugins;

// Секция Plugins:DraftWatcher (QA-фикс U): выключатель наблюдателя черновиков
// (PluginDraftWatcher). Читается через единую точку (этот класс), как LocalModelStatsOptions:
// false — наблюдатель не регистрируется вовсе (нет hosted-сервиса, нет inotify-экземпляра).
// Тестовый хост (appsettings.Testing.json) выключает его по умолчанию: иначе его inotify
// искажал процессные замеры тестов master (FileWatcherSubscriptionTests). Дефолт true —
// боевое поведение не меняется (наблюдатель стартует, как раньше).
public sealed class PluginDraftWatcherOptions
{
    public const string Section = "Plugins:DraftWatcher";

    // true по умолчанию: боевой инстанс наблюдает за черновиками (поведение не меняется).
    public bool Enabled { get; set; } = true;
}
