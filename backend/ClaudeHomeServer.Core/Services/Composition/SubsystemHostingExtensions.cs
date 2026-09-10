namespace ClaudeHomeServer.Services.Composition;

// Хелперы, общие для подсистем: гейт Testing:EnableHostedServices (хосты в Testing по
// умолчанию НЕ регистрируются — 17 фоновых циклов на каждый из ~27 бутов тестовых
// хостов только жгли время прогона и порождали фоновую возню, диагностика 2026-07-30)
// и общая форма резолва каталога данных. Семантика — копия 1-в-1 локальных хелперов
// из Program.cs, чтобы у подсистем не было соблазна дублировать гейт самогейтом
// (см. костыль `Services/Images/ImageBackfillHostedService.cs:20-24`).
//
// Параметр `subsystemKey` — гейт `Subsystems:{Key}:Enabled`. Если передан,
// метод проверяет его ПЕРЕД проверкой среды: выключенная подсистема не
// получает hosted-сервиса ни в Testing, ни в Production. По умолчанию `null` —
// гейта по подсистеме нет, проверяется только среда (старая семантика, чтобы
// вызовы из Program.cs, не относящиеся к конкретной подсистеме, продолжали
// работать).
public static class SubsystemHostingExtensions
{
    // Регистрирует hosted-сервис, если разрешено средой и ключом. В Testing без явного
    // Testing:EnableHostedServices=true hosted НЕ регистрируется; в Production —
    // регистрируется всегда. Подсистемы вызывают этот метод вместо прямого
    // AddHostedService, чтобы случайно не притащить фоновый цикл в тесты.
    //
    // Параметр `subsystemKey` опционален: для подсистем это их собственный Key,
    // и тогда подсистема, задизейбленная по `Subsystems:{Key}:Enabled=false`,
    // не зарегистрирует hosted даже в Production. Для вызовов из Program.cs,
    // которые не относятся к конкретной подсистеме, ключ можно опустить.
    public static IServiceCollection AddGatedHostedService<T>(
        this IServiceCollection services, IConfiguration config, string? subsystemKey = null)
        where T : class, IHostedService
    {
        if (GatedHostedShouldRegister(config, subsystemKey))
            services.AddHostedService<T>();
        return services;
    }

    // То же, что AddGatedHostedService, но hosted создаётся из уже зарегистрированного
    // singleton-фабрикой (типичный случай: инстанс shared-сервиса с подписками в
    // StartAsync, чтобы подписки встали на тот же объект, что и в DI).
    public static IServiceCollection AddGatedHostedFrom<T>(
        this IServiceCollection services, IConfiguration config,
        Func<IServiceProvider, T> factory, string? subsystemKey = null)
        where T : class, IHostedService
    {
        if (GatedHostedShouldRegister(config, subsystemKey))
            services.AddHostedService(factory);
        return services;
    }

    private static bool GatedHostedShouldRegister(IConfiguration config, string? subsystemKey)
    {
        // Сначала гейт подсистемы: `Subsystems:{Key}:Enabled=false` блокирует регистрацию
        // ВСЕГДА, даже если Testing:EnableHostedServices=true. Иначе тестовая среда
        // с включёнными хостами обходила бы отключение подсистемы и тянула её фоновые
        // циклы — это размазывает состояние «выключено» между Production/Testing.
        if (subsystemKey is not null && !SubsystemGate.IsEnabled(config, subsystemKey))
            return false;

        // Та же проверка, что была в Program.cs:121-130: ASP.NET Core кладёт среду
        // в IConfiguration["ASPNETCORE_ENVIRONMENT"] при WebApplication.CreateBuilder.
        var isTesting = string.Equals(
            config["ASPNETCORE_ENVIRONMENT"], "Testing", StringComparison.OrdinalIgnoreCase);
        return !isTesting || config.GetValue<bool>("Testing:EnableHostedServices");
    }

    // Общая форма резолва каталога данных от DataPath. До этого хелпера тот же
    // сниппет жил в GraphPersistence/DossierStore/WorkspaceKnowledgeStore и в двух
    // местах Program.cs (пост-билд и handle-migration), и каждый раз рисковал
    // отстать от дефолта. Семантика:
    //   dataPath = config["DataPath"] ?? BaseDirectory + "data/projects.json"
    //   root     = DirectoryName(FullPath(dataPath)) ?? BaseDirectory + "data"
    //   если переданы subfolders — приклеить через Path.Combine по порядку.
    // Существующие три места НЕ переписывать здесь — только создать хелпер,
    // миграция пойдёт по мере выделения их подсистем.
    public static string ResolveDataDir(IConfiguration config, params string[] subfolders)
    {
        var dataPath = config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var root = Path.GetDirectoryName(Path.GetFullPath(dataPath))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        if (subfolders is { Length: > 0 })
            root = Path.Combine(new[] { root }.Concat(subfolders).ToArray());
        return root;
    }
}