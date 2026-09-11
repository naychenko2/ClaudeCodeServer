using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Services.Images;

// Сторож регистрации `ImagesSubsystem`: гарантирует, что подсистема подключает
// ВСЕ ожидаемые сервисы раздела генерации картинок (роутер, настройки, очередь
// догоняющей генерации и оба драйвера fal/glif). Если кто-то тихо уберёт вызов
// `new ImagesSubsystem()` из Program.cs, тест упадёт с понятным сообщением, а не
// молча сломает аватар персоны в проде (отсутствующая регистрация = пустой набор
// IImageGenerator = невозможно сгенерировать картинку).
//
// Вторая часть — парсер Program.cs: гарантирует, что вызов `ImagesSubsystem` в
// `AddSubsystems(...)` раскомментирован, а не висит как мёртвая правка.
public class ImagesSubsystemRegistrationTests
{
    private static (IServiceCollection services, IConfiguration config) NewHost()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ImageBackfillHostedService резолвит IHostEnvironment и IConfiguration —
                // без них `BuildServiceProvider` ругнётся на фоне, и тест провалится
                // по ложной причине.
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["Testing:EnableHostedServices"] = "false",
            })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new HostEnvironmentStub());
        // FalImageService (драйвер картинок) принимает IHttpClientFactory в конструкторе
        // (см. FalImageService.cs:26) — без этого резолв упадёт по ложной причине.
        services.AddHttpClient();
        return (services, config);
    }

    [Fact]
    public void Register_РезолвитРоутерНастроекОчередьИДрайверы()
    {
        var (services, config) = NewHost();
        services.AddSubsystems(config, new ImagesSubsystem());

        using var provider = services.BuildServiceProvider();

        // Сервисы без сложных зависимостей — резолвим напрямую. Это доказывает, что
        // DI-граф для них собран подсистемой, а не прилетел извне.
        Assert.NotNull(provider.GetService<ImageGenerationSettingsStore>());
        Assert.NotNull(provider.GetService<ImageBackfillStore>());
        Assert.NotNull(provider.GetService<ImageGenerationService>());

        // Роутер собирает IEnumerable<IImageGenerator> — их должно быть ровно 2
        // (fal и glif). Если кто-то добавит третий драйвер без расширения allow-list
        // вертикали в SubsystemBoundaryTests, тест укажет на расхождение.
        var drivers = provider.GetServices<IImageGenerator>().ToList();
        Assert.Equal(2, drivers.Count);

        // Конкретные классы драйверов тоже должны быть в контейнере (`AddImageDriver<T>`
        // регистрирует и по своему типу, и в набор IImageGenerator). Драйверы
        // резолвятся через IHttpClientFactory (добавлен в NewHost).
        Assert.NotNull(provider.GetService<FalImageService>());
        Assert.NotNull(provider.GetService<GlifImageGenerator>());

        // ImageBackfillService резолвится НЕ здесь: его конструктор тянет PersonaManager,
        // IHubContext<SessionHub>, IHostApplicationLifetime — для теста регистрации
        // подсистемы это лишний огород. Факт регистрации проверяем через дескриптор.
        var backfillDescriptors = services
            .Where(d => d.ServiceType == typeof(ImageBackfillService))
            .ToList();
        Assert.NotEmpty(backfillDescriptors);
        Assert.Equal(ServiceLifetime.Singleton, backfillDescriptors[0].Lifetime);
    }

    [Fact]
    public void Register_KeyИTitleСоответствуютКонтракту()
    {
        var subsystem = new ImagesSubsystem();
        Assert.Equal("images", subsystem.Key);
        Assert.False(string.IsNullOrWhiteSpace(subsystem.Title));
    }

    // Негативная сторожевая: Program.cs ДОЛЖЕН содержать раскомментированный
    // вызов `new ImagesSubsystem()` в AddSubsystems(...). Парсинг текстовый — без
    // WebApplicationFactory (тот поднимает SignalR-хаб и хост и для этой задачи
    // дороже, чем пользы). Зачем: позитивный тест выше проверяет сам класс
    // подсистемы, но не проверяет, что он подключён в Program.cs. Без этого теста
    // можно спокойно закомментировать `new ImagesSubsystem()`, позитивный тест
    // останется зелёным, а в проде DI отдаст пустой набор IImageGenerator и
    // аватары персон молча перестанут генерироваться.
    [Fact]
    public void ProgramCs_CallsImagesSubsystem_Uncommented()
    {
        var programPath = LocateProgramCs();
        Assert.True(File.Exists(programPath), $"Program.cs должен существовать по пути {programPath}");

        var lines = File.ReadAllLines(programPath);

        // Строки с упоминанием подсистемы — есть ли вообще.
        var callLines = lines
            .Select((text, idx) => (Text: text.TrimStart(), Number: idx + 1))
            .Where(l => l.Text.Contains("ImagesSubsystem", StringComparison.Ordinal))
            .ToList();

        callLines.Should().NotBeEmpty(
            "Program.cs должен содержать вызов `new ImagesSubsystem()` — " +
            "иначе в проде DI отдаст пустой набор IImageGenerator, и аватары/иконки/фоны " +
            "перестанут генерироваться (тихая деградация, против которой писался сторож)");

        // Среди строк должна быть раскомментированная — именно вызов, а не упоминание в комментарии.
        // `// new ImagesSubsystem()` — закомментированный, не считается.
        var uncommentedCall = callLines.Any(l =>
            !l.Text.StartsWith("//")
            && l.Text.Contains("new ", StringComparison.Ordinal)
            && l.Text.Contains("ImagesSubsystem", StringComparison.Ordinal));
        uncommentedCall.Should().BeTrue(
            "вызов `new ImagesSubsystem()` должен быть РАСКОММЕНТИРОВАН в Program.cs — " +
            "закомментированный означает «DI пуст», что и есть та самая тихая деградация");

        // И он должен быть в составе AddSubsystems(...) — иначе сторож AddSubsystems
        // проигнорирует подсистему (допускает params-вариант без регистрации, но
        // закомментированная строка — не то же самое, что «передали в массив»).
        // Вызов многострочный — `AddSubsystems(` на одной строке, список подсистем
        // внутри на следующих, вплоть до закрывающей `)`. Идём от открывающей
        // строки до закрывающей скобки и проверяем, что `ImagesSubsystem` там есть
        // (и не закомментирована).
        var hasAddSubsystems = ContainsUncommentedAddSubsystemsWith(lines, "ImagesSubsystem");
        hasAddSubsystems.Should().BeTrue(
            "ImagesSubsystem должна быть передана в `builder.Services.AddSubsystems(...)`");
    }

    // Поднимаемся от bin/ теста до каталога, содержащего *.slnx (это backend/ClaudeHomeServer.slnx).
    // Program.cs лежит на одном уровне с .slnx.
    private static string LocateProgramCs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.EnumerateFiles(dir.FullName, "*.slnx").Any())
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "ClaudeHomeServer", "Program.cs");
    }

    // Проверяет, что многострочный `builder.Services.AddSubsystems(...)` содержит
    // раскомментированное упоминание указанной подсистемы. Идём от строки с открывающей
    // скобкой до строки, в которой скобок без закрытия становится 0 — внутри этого
    // блока ждём подсистему без префикса `//` (закомментированная — пропускаем).
    //
    // Зачем многострочный парсинг: реальный вызов в Program.cs разнесён по строкам:
    //   builder.Services.AddSubsystems(builder.Configuration,
    //       new VideoSubsystem(),
    //       new ClaudeHomeServer.Services.Reader.ReaderSubsystem(),
    //       new ClaudeHomeServer.Services.Images.ImagesSubsystem());
    // Простой `line.Contains("ImagesSubsystem")` не подходит — на одной строке оба
    // маркера не встречаются.
    private static bool ContainsUncommentedAddSubsystemsWith(string[] lines, string subsystemName)
    {
        var depth = 0;
        var inCall = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!inCall)
            {
                if (!trimmed.StartsWith("//")
                    && trimmed.StartsWith("builder.Services.AddSubsystems", StringComparison.Ordinal))
                {
                    inCall = true;
                    depth = CountChar(line, '(') - CountChar(line, ')');
                    if (depth <= 0) return false; // однострочный без аргументов — не наш случай
                }
                continue;
            }

            depth += CountChar(line, '(') - CountChar(line, ')');
            if (!trimmed.StartsWith("//") && line.Contains(subsystemName, StringComparison.Ordinal))
                return true;
            if (depth <= 0) return false;
        }
        return false;
    }

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var ch in s) if (ch == c) n++;
        return n;
    }

    // Заглушка IHostEnvironment — нужна, потому что ImageBackfillHostedService
    // (hosted) сидит в графе DI. Без stub'а BuildServiceProvider ругнётся на
    // отсутствующий сервис и замаскирует настоящий сценарий.
    private sealed class HostEnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ClaudeHomeServer.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
