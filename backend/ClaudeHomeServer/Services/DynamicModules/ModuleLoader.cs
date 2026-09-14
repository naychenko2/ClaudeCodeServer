using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.DynamicModules;

// Загрузчик динамических модулей (сценарий Б): для каждого Enabled-модуля из ModuleRegistry
// грузит сборку по Backend.AssemblyPath, находит единственную реализацию IAppSubsystem, создаёт её и
// зовёт Register(IServiceCollection, IConfiguration). Возвращает загруженные сборки, чтобы
// Program.cs подключил их контроллеры через AddApplicationPart.
//
// Load-once: сборка идёт в ДЕФОЛЬТНЫЙ (не collectible) контекст, Unload() не делаем —
// DI сам свою сборку не выгружает, и collectible-контекст всё равно не спасёт (DI держит
// ссылки). Зависимости модуля (Core, ASP.NET) резолвятся из того же дефолтного контекста,
// что и хост, поэтому отдельный AssemblyLoadContext с fall-back не нужен.
public sealed class ModuleLoader
{
    private readonly ModuleRegistry _registry;
    private readonly IConfiguration _config;
    private readonly ILogger<ModuleLoader> _log;

    public ModuleLoader(ModuleRegistry registry, IConfiguration config, ILogger<ModuleLoader> log)
    {
        _registry = registry;
        _config = config;
        _log = log;
    }

    // Вызывается один раз на старте (до builder.Build()) в Program.cs. Возвращает
    // загрузки только тех модулей, сборка которых реально загрузилась (для них — AddApplicationPart).
    public IReadOnlyList<Assembly> LoadAll(IServiceCollection services)
    {
        var loaded = new List<Assembly>();
        foreach (var desc in _registry.All.Where(m => m.Enabled))
        {
            var asm = TryLoadOne(services, desc);
            if (asm is not null) loaded.Add(asm);
        }
        return loaded;
    }

    private Assembly? TryLoadOne(IServiceCollection services, ModuleDescriptor desc)
    {
        // Фронт-only модуль (N1: notes): Backend отсутствует, отдельной сборки и не ожидается —
        // его MF-remote отдаёт SubsystemModulesController. Без этого раннего возврата каждый старт
        // хоста шептал Warning «сборка не найдена «»» о штатно сконфигурированном модуле.
        if (desc.Backend is null)
        {
            _log.LogInformation("Модуль «{Key}» — фронт-only (MF-remote), сборка не ожидается", desc.Key);
            return null;
        }
        string? path;
        try { path = ResolvePath(desc.Backend?.AssemblyPath); }
        catch (UnauthorizedAccessException ex)
        {
            // SafePath.Join (Core) бросает, когда Backend.AssemblyPath уходит за базовый
            // каталог процесса (M6): один «плохой» модуль не должен ронять старт — лог и пропуск.
            _log.LogWarning(ex, "Модуль «{Key}»: путь сборки уходит за базовый каталог", desc.Key);
            return null;
        }
        if (path is null || !File.Exists(path))
        {
            _log.LogWarning("Модуль «{Key}»: сборка не найдена «{Path}»", desc.Key, desc.Backend?.AssemblyPath);
            return null;
        }

        try
        {
            // Default = дефолтный (не collectible) контекст: load-once, выгрузки нет.
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

            var implType = FindSubsystemType(asm);
            if (implType is null)
            {
                _log.LogWarning("Модуль «{Key}»: в сборке «{Name}» нет реализаций IAppSubsystem",
                    desc.Key, asm.GetName().Name);
                // Сборку всё равно возвращаем: её контроллеры (если есть) подключаются,
                // а отсутствие подсистемы — штатное состояние «модуль только с маршрутами».
                return asm;
            }

            var subsystem = (IAppSubsystem)Activator.CreateInstance(implType)!;
            subsystem.Register(services, _config);
            _log.LogInformation("Модуль «{Key}» «{Title}» v{Version} загружен: {Path}",
                desc.Key, subsystem.Title, desc.Version, desc.Backend?.AssemblyPath);
            return asm;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Модуль «{Key}»: ошибка загрузки «{Path}»", desc.Key, desc.Backend?.AssemblyPath);
            return null;
        }
    }

    // Относительный Backend.AssemblyPath резолвим от базового каталога процесса (рядом с exe) —
    // так заглушка лежит в <bin>/modules/__stub/ и не зависит от cwd запуска.
    // SafePath.Join (Core-примитив, см. FileService.SafeJoin) замыкает путь на базовый
    // каталог: выход за него = UnauthorizedAccessException, а не тихий обход (M6).
    private static string? ResolvePath(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
        return Path.IsPathRooted(assemblyPath)
            ? assemblyPath
            : SafePath.Join(AppContext.BaseDirectory, assemblyPath);
    }

    private static Type? FindSubsystemType(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        // GetTypes падает, если у какого-то типа не резолвится базовый тип; частичный список
        // всё равно пригоден, если среди него есть наш IAppSubsystem.
        catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }

        var matches = types
            .Where(t => t is not null && !t.IsAbstract && !t.IsGenericTypeDefinition
                && typeof(IAppSubsystem).IsAssignableFrom(t))
            .ToList();

        // Инвариант «ровно одна IAppSubsystem на сборку»: несколько реализаций —
        // неинформативная ошибка конфигурации (какую брать?). Бросаем явно, а не молча
        // берём первую (FirstOrDefault до этого гасил дубликат, выбирая первого попавшегося).
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"В сборке «{asm.GetName().Name}» {matches.Count} реализации IAppSubsystem " +
                $"({string.Join(", ", matches.Select(t => t.FullName))}) — ожидается ровно одна.");
        return matches.FirstOrDefault();
    }
}
