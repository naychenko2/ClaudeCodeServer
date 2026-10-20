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
// (Notes → NotesPluginCapability, Tasks → TasksPluginCapability, шаг 3.5) — здесь
// их нет, шов живёт в Core; мост резолвит IEnumerable<IPluginCapability> из DI.
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
        // Секреты плагинов (M7): per-owner архив plugin-secrets.json, отдельно от installed.json.
        services.AddSingleton<PluginSecretsStore>();
        services.AddSingleton<IPluginDirSource, PluginDirSource>();
        // Шов «навык плагина существует» (QA-фикс L3): PersonaBindingsService (Main) проверяет
        // Skill-привязку plugin:{id}/{skill} при создании. Stateless поверх PluginStore +
        // IFeatureFlagGate (Core-швы); Main берёт его опциональной зависимостью (null =
        // подсистема выключена).
        services.AddSingleton<IPluginSkillResolver, PluginSkillResolver>();
        // Валидатор пакета (ADR-031 §2.3, шаг 1.3): allow-list файлов + sha256.
        // Stateless — singleton; вызывается PluginInstaller при установке.
        services.AddSingleton<PluginPackageValidator>();
        // Черновики build-it-yourself (шаг 4.1): создание из шаблона, запись/удаление файлов,
        // валидация, сводка. Stateless поверх PluginStore + PluginPackageValidator; его используют
        // и MCP-тулсет plugins (4.1), и наблюдение/«Принять» (4.2/4.3).
        services.AddSingleton<PluginDraftService>();
        // Сравнение черновика с установленной версией (шаг 4.3b): stateless поверх PluginStore;
        // его использует REST-ручка GET /api/plugins/drafts/{slug}/diff.
        services.AddSingleton<PluginDraftDiff>();
        // MCP-тулсет plugins (шаг 4.1) регистрируется в Main НЕусловно (Program.cs), а не здесь:
        // при выключенной подсистеме Register не вызывается, и если бы тулсет жил только здесь,
        // MCP-сервер plugins исчезал бы из набора (состав tools/list менялся, POST /mcp/plugins
        // → 404). Неусловная регистрация держит сервер в наборе; PluginDraftService (зарегистрирован
        // здесь) — опциональная зависимость тулсета, null при выключенной подсистеме.
        // Ядро установки (шаг 1.4, C1/C2/M5/M6): метка источника, атомарное копирование,
        // no-op повтора. Stateless-логика поверх PluginStore + валидатора.
        services.AddSingleton<PluginInstaller>();
        // Каскад удаления установки (пакет, запись, черновики, granted, секреты + шов).
        // netProxy — для очистки окна ограничения частоты (rework 12, minor 8).
        services.AddSingleton<PluginUninstall>(sp => new PluginUninstall(
            sp.GetRequiredService<PluginStore>(),
            sp.GetRequiredService<PluginSecretsStore>(),
            sp.GetRequiredService<IPluginInstallCascade>(),
            sp.GetRequiredService<PluginNetProxy>()));
        // Каскад «внешних» ссылок (привязки персон, слоты). MVP — no-op: реальный
        // адаптер, снимающий биндинги/слоты, приходит с UI-шагом 2.x (ADR-031 §4).
        services.AddSingleton<IPluginInstallCascade, NoOpPluginInstallCascade>();
        // Мост панели v1 (шаг 3.1): диспетчер IPluginCapability. Stateless — singleton;
        // список обработчиков (Notes/Tasks) резолвится DI'ем из вертикалей-владельцев.
        // PluginBridgeController — ApplicationPart (Web SDK), регистрируется MVC'ом.
        services.AddSingleton<PluginBridge>();
        // Прокси net:{host} (шаг 3.3): обработчик net.fetch. Stateless — singleton;
        // транспорт — SocketsHttpHandler с ConnectCallback, закрепляющим адрес (rework 12,
        // critical 1: TOCTOU/DNS rebinding между SSRF-проверкой и TCP-connect закрыт).
        services.AddSingleton<PluginNetProxy>(sp => new PluginNetProxy(
            sp.GetRequiredService<PluginSecretsStore>(), PluginNetProxy.CreateHandler()));
        // Обработчик прав: мост резолвит IEnumerable<IPluginCapability> (net: + Notes/Tasks).
        services.AddSingleton<IPluginCapability>(sp => sp.GetRequiredService<PluginNetProxy>());
        // Наблюдатель черновиков (шаг 4.2): один inotify-экземпляр на процесс, следит за
        // data/plugins/{owner}/_drafts, шлёт владельцу событие plugin_draft_changed (дебаунс 300 мс).
        // Hosted-сервис: подсистема выключена → Register не вызывается → наблюдатель не стартует,
        // приложение поднимается (отключаемость по ADR-014).
        services.AddHostedService<PluginDraftWatcher>();
    }
}
