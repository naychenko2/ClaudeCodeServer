using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Вертикаль плагинов владельца (ADR-031): пакет из существующих примитивов —
// навыки/команды нативным форматом CLI через --plugin-dir, панели sandbox-iframe
// с мостом v1, серверный прокси net:{host}. Ключ подсистемы `plugins`, тумблер
// `Subsystems:plugins:Enabled` (дефолт true — выключение явное).
//
// Шаг 1.2 (пачка A): хранилище PluginStore (data/plugins/{owner}: пакеты,
// installed.json, черновики) и реализация шва IPluginDirSource (PluginDirSource) —
// каталоги включённых пакетов владельца для --plugin-dir. Обработчики прав
// IPluginCapability (notes:read/tasks:read) регистрируют сами вертикали-владельцы
// (Notes, Tasks) отдельной задачей — здесь их нет, шов живёт в Core.
//
// Отключаемость по ADR-014: при выключенной подсистеме Register не вызывается,
// и PluginStore/IPluginDirSource в контейнер не попадают. Обязательного параметра
// конструктора от Plugins вне самой вертикали нет (потребители берут IPluginDirSource
// опциональной зависимостью) — дефекта «обязательный параметр от отключаемой
// подсистемы» нет.
public sealed class PluginsSubsystem : IAppSubsystem
{
    public string Key => "plugins";

    public string Title => "Плагины";

    public string Description =>
        "Пакеты владельца: навыки и команды CLI (--plugin-dir), панели, прокси net: (ADR-031).";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Singleton-ы без внутреннего порядка: PluginDirSource зависит от PluginStore,
        // DI порядок регистраций при резолве не учитывает.
        services.AddSingleton<PluginStore>();
        services.AddSingleton<IPluginDirSource, PluginDirSource>();
        // Валидатор пакета (ADR-031 §2.3, шаг 1.3): allow-list файлов + sha256.
        // Stateless — singleton; вызывается PluginsController при установке.
        services.AddSingleton<PluginPackageValidator>();
        // Каскад удаления установки (пакет, запись, черновики, granted, секреты + шов).
        services.AddSingleton<PluginUninstall>();
        // Каскад «внешних» ссылок (привязки персон, слоты). MVP — no-op: реальный
        // адаптер, снимающий биндинги/слоты, приходит с UI-шагом 2.x (ADR-031 §4).
        services.AddSingleton<IPluginInstallCascade, NoOpPluginInstallCascade>();
    }
}
