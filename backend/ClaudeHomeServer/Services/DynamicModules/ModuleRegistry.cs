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
}
