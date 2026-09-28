using Microsoft.Extensions.Configuration;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.DynamicModules;

// Реестр динамических модулей (сценарий Б): список ModuleDescriptor, прочитанный из
// секции "DynamicModules" конфига. Живёт в Main (спина) — потребляет его Program.cs при сборке
// контейнера. НЕ путать с внешними YARP-модулями (Services.Modules.ModuleRegistry,
// манифесты module.json, секция "Modules:Path"/"Modules:Manifests", отдельный процесс за
// reverse-proxy): это другой канал подключения — наша сборка грузится ВНУТРИ процесса,
// а не за прокси. Свои секции ("DynamicModules" vs "Modules") снимают коллизию.
public sealed class ModuleRegistry
{
    private readonly List<ModuleDescriptor> _modules;

    public ModuleRegistry(IConfiguration config)
    {
        // Секция "DynamicModules" — массив манифестов (DynamicModules:0:Key, ...).
        // Пустая/отсутствующая секция → пустой реестр, ядро стартует как раньше.
        // Binding-ошибку гасим: если секцию напишут ОБЪЕКТОМ вместо массива, бинд в список
        // легитимно падёт — считаем это «нет динамических модулей», а не фатальной ошибкой.
        var section = config.GetSection("DynamicModules");
        try
        {
            _modules = (section.Get<List<ModuleDescriptor>>() ?? [])
                .Where(m => m is { Key.Length: > 0 })
                .ToList();
        }
        catch
        {
            _modules = [];
        }
    }

    public IReadOnlyList<ModuleDescriptor> All => _modules;

    // Модули, чей MF-remote раздаём по /{key}-remote: запись включена, Frontend есть И модуль
    // реально загружен (активен в сторе). Одного Enabled мало: при Subsystems:{Key}:Enabled=false
    // ModuleLoader сборку не грузит, а remote раздавался бы всё равно. Стор к месту раздачи уже
    // заполнен — LoadAll идёт в Program.cs до построения конвейера.
    public IEnumerable<ModuleDescriptor> ServedRemotes(SubsystemStateStore states)
    {
        var active = states.ActiveKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _modules.Where(m => m.Enabled && m.Frontend is not null && active.Contains(m.Key));
    }
}
