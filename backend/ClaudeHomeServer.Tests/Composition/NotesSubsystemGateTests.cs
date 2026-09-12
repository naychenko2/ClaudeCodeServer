using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Гейт подсистемы Notes (блокер ревью notes-optional, Б3): при `Subsystems:Notes:Enabled=false`
// приложение не должно падать. Правило пилота — «обязательный параметр конструктора от
// отключаемой подсистемы — дефект»: `NotesService` уже был опционален (`NotesService? notes =
// null`), но `NotesKnowledgeService`/`NoteTaskSyncService`/`NotesAiService` оставались
// обязательными в 8 местах (Б1), а форвардер `IKnowledgeSyncParticipant → NotesKnowledgeService`
// в Program.cs был безусловным (Б2). До правок резолв валился с InvalidOperationException —
// и ломался КАЖДЫЙ ход (сборка MCP-конфига перечисляет IEnumerable<IMcpToolset>), а не только
// заметки. «До» зафиксировано отдельно (см. resultMarkdown задачи): при первой версии этого
// теста (до Б1/Б2) резолв PersonaBindingsService падал с
// "Unable to resolve service for type 'NotesKnowledgeService' while attempting to activate
// 'PersonaBindingsService'" — та же коренная причина, что здесь проверяется точечно.
//
// НЕ через TestWebApplicationFactory: экспериментально подтверждено, что
// `Subsystems:Notes:Enabled=false` нужно поставить ПЕРЕД `WebApplication.CreateBuilder(args)` —
// значение читается в `AddSubsystems` до того, как `WebApplicationFactory.ConfigureWebHost`
// успевает подмешать `ExtraConfig` (та же грабля, что описана в `TestEnvironmentGuard`:
// «ConfigureWebHost применяется ОТЛОЖЕННО»). Единственный способ повлиять на это раньше —
// переменная окружения ПРОЦЕССА, а `IConfiguration.AddEnvironmentVariables()` подхватывает её
// в ЛЮБОМ конкурентно строящемся хосте в этом же процессе — с `parallelizeTestCollections: true`
// (xunit.runner.json) это ловит ЧУЖИЕ тесты (эмпирически поймано: полный прогон решения уронил
// NotesControllerTests 500-ми, пока рядом строился хост с выключенными заметками). Поэтому
// здесь — точечные reflection-проверки конструкторов (без DI вообще) и один изолированный
// ServiceCollection, копирующий ровно снипет Program.cs для форвардера Б2. Интеграционный тест
// через WebApplicationFactory (404 на маршрутах, ход без секции) — отдельная задача adb71e8d.
public class NotesSubsystemGateTests
{
    // Параметры конструкторов, приходящие из отключаемой вертикали Notes: места Б1
    // (noteSync/ai — сервисы вертикали Notes*) и Core-швы `INoteAccessor` (волна
    // «разрыв Main↔NotesService») и `INoteSemanticIndex` (под-волна «разрыв
    // Main→NotesKnowledgeService»). Инвариант один: обязательный такой параметр вне
    // самой вертикали — дефект; у шва он должен быть строго `<Шов>? x = null`.
    // `NotesToolset` (MCP) уже вынесен в вертикаль `ClaudeHomeServer.Notes/Services/Mcp`,
    // но держит прямые `NotesKnowledgeService`/`NotesAiService`/`NoteTaskSyncService` —
    // это его внутренняя композиция, и шов для внешних потребителей на него не нужен.
    public static IEnumerable<object[]> OptionalNotesVerticalParams =>
    [
        [typeof(ProjectsController), "notesKb", typeof(INoteSemanticIndex)],
        [typeof(TasksController), "noteSync", typeof(INoteTaskSync)],
        [typeof(PersonaBindingsService), "notesKb", typeof(INoteSemanticIndex)],
        [typeof(SessionSummaryService), "kb", typeof(INoteSemanticIndex)],
        [typeof(TaskExecutionService), "kb", typeof(INoteSemanticIndex)],
        [typeof(UnifiedSearchService), "kb", typeof(INoteSemanticIndex)],
        [typeof(NotesToolset), "kb", typeof(NotesKnowledgeService)],
        [typeof(NotesToolset), "ai", typeof(NotesAiService)],
        [typeof(NotesToolset), "noteTasks", typeof(NoteTaskSyncService)],
        [typeof(TasksToolset), "noteSync", typeof(INoteTaskSync)],
        // Шов `INoteAccessor` (10 потребителей в спине и `Memory`).
        [typeof(FilesController), "notes", typeof(INoteAccessor)],
        [typeof(DocsController), "notes", typeof(INoteAccessor)],
        [typeof(PersonasController), "notes", typeof(INoteAccessor)],
        [typeof(DailyBriefingService), "notes", typeof(INoteAccessor)],
        [typeof(ChatDigestService), "notes", typeof(INoteAccessor)],
        [typeof(SessionSummaryService), "notes", typeof(INoteAccessor)],
        [typeof(PersonaBindingsService), "notes", typeof(INoteAccessor)],
        [typeof(PersonasCrudService), "notes", typeof(INoteAccessor)],
        [typeof(UnifiedSearchService), "notes", typeof(INoteAccessor)],
        [typeof(WorkspaceToolset), "notes", typeof(INoteAccessor)],
    ];

    // Прямая проверка «правила пилота»: DI-контейнер строит объект без исключения только
    // если у параметра ЕСТЬ дефолт (иначе Microsoft.Extensions.DependencyInjection не подставит
    // ничего и бросит "Unable to resolve service..." при отсутствии регистрации типа —
    // именно так падал резолв ДО Б1, см. шапку класса). Reflection не поднимает DI вовсе —
    // проверка детерминирована и не зависит от порядка/параллелизма тестов.
    [Theory]
    [MemberData(nameof(OptionalNotesVerticalParams))]
    public void CtorParam_FromDisablableNotesVertical_IsOptionalWithNullDefault(
        Type owner, string paramName, Type expectedType)
    {
        var ctor = owner.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var param = ctor.GetParameters().SingleOrDefault(p => p.Name == paramName);

        param.Should().NotBeNull(
            $"{owner.Name} должен иметь параметр конструктора '{paramName}' типа {expectedType.Name}");
        param!.ParameterType.Should().Be(expectedType);
        param.HasDefaultValue.Should().BeTrue(
            $"{owner.Name}.{paramName} обязан быть опциональным (Type? {paramName} = null) — иначе " +
            "DI не сможет собрать объект при выключенной подсистеме Notes, и резолв упадёт с " +
            "InvalidOperationException (было ДО Б1)");
        param.DefaultValue.Should().BeNull($"{owner.Name}.{paramName} обязан деградировать в null");
    }

    // Б2: форвардер `IKnowledgeSyncParticipant → NotesKnowledgeService` в Program.cs закрыт
    // гейтом SubsystemGate.IsEnabled — копия ровно того снипета (2 строки), а не отдельная
    // логика: расхождение с прод-кодом исключено самой простотой снипета.
    [Fact]
    public void KnowledgeSyncParticipantForwarder_GatedByNotesSubsystem_SkipsWhenDisabled()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:Notes:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        // NotesKnowledgeService НЕ регистрируется вовсе — эмулирует выключенную NotesSubsystem
        // (её Register() не вызывается при Subsystems:Notes:Enabled=false, см. AddSubsystems).
        if (ClaudeHomeServer.Services.Composition.SubsystemGate.IsEnabled(config, "notes"))
        {
            services.AddSingleton<IKnowledgeSyncParticipant>(
                sp => sp.GetRequiredService<NotesKnowledgeService>());
        }

        using var sp = services.BuildServiceProvider();

        // До Б2 регистрация была безусловной — резолв коллекции валился с
        // InvalidOperationException на NotesKnowledgeService, роняя ВСЕ участники синка знаний
        // (Memory/Dossiers/ProjectKnowledgeSync), а не только заметки.
        var act = () => sp.GetServices<IKnowledgeSyncParticipant>().ToList();
        act.Should().NotThrow();
        act().Should().BeEmpty("при выключенной подсистеме форвардер не должен регистрироваться");
    }

    // Контрибьютор секции «recall-notes»: гейт СТРУКТУРНЫЙ, а не предикатный.
    // `NotesRecallContributor` тянет `NotesKnowledgeService` и регистрируется внутри
    // `NotesSubsystem.Register` (Этап 5, шаг 6 — инверсия контрибьюторов), а `AddSubsystems`
    // при `Subsystems:Notes:Enabled=false` `Register` не зовёт вовсе. Поэтому ни предиката
    // `isEnabled` в `AddPromptSectionContributors`, ни пост-хок удаления дескрипторов из
    // Program.cs больше нет: гейт держит сама композиция подсистем.
    //
    // Тест ходит через настоящий `AddSubsystems` (не копию снипета): убери гейт там —
    // и ветка enabled=false зарегистрирует контрибьютора, тест покраснеет.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotesRecallContributor_RegisteredOnlyWhenSubsystemEnabled(bool enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:Notes:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSubsystems(config, new NotesSubsystem());

        // Пара дескрипторов от AddPromptSectionContributor<T>: конкретный тип + форвардер
        // интерфейса через ImplementationFactory. В этой коллекции кроме Notes подсистем нет,
        // поэтому любой IPromptSectionContributor здесь — именно контрибьютор заметок.
        services.Any(d => d.ServiceType == typeof(NotesRecallContributor)).Should().Be(enabled,
            $"NotesRecallContributor регистрируется в NotesSubsystem.Register (Enabled={enabled})");
        services.Any(d => d.ServiceType == typeof(IPromptSectionContributor)).Should().Be(enabled,
            $"форвардер IPromptSectionContributor едет тем же вызовом (Enabled={enabled})");
        // Шов `INoteAccessor` живёт в том же `Register` — гейт у него структурный, как у всего
        // остального в подсистеме.
        services.Any(d => d.ServiceType == typeof(INoteAccessor)).Should().Be(enabled,
            $"шов INoteAccessor регистрируется в NotesSubsystem.Register (Enabled={enabled})");
        // Второй Core-шов (`INoteSemanticIndex`) — тем же структурным гейтом.
        services.Any(d => d.ServiceType == typeof(INoteSemanticIndex)).Should().Be(enabled,
            $"шов INoteSemanticIndex регистрируется в NotesSubsystem.Register (Enabled={enabled})");
        // Третий Core-шов (`INoteTaskSync` — обратная запись Main → Notes) — тем же гейтом.
        services.Any(d => d.ServiceType == typeof(INoteTaskSync)).Should().Be(enabled,
            $"шов INoteTaskSync регистрируется в NotesSubsystem.Register (Enabled={enabled})");

        if (!enabled)
        {
            // Ключевой инвариант: при выключенной подсистеме перечисление набора не только
            // пусто, но и НЕ БРОСАЕТ — сироты-форвардера на неразрешимый NotesKnowledgeService
            // в коллекции не остаётся (именно на этом валился пост-хок вариант удаления
            // дескрипторов по ImplementationType).
            using var sp = services.BuildServiceProvider();
            var act = () => sp.GetServices<IPromptSectionContributor>().ToList();
            act.Should().NotThrow();
            act().Should().BeEmpty();
        }
        else
        {
            // Шов — фабрика на уже зарегистрированный синглтон `NotesService`, а не
            // `AddSingleton<INoteAccessor, NotesService>`: вторая регистрация типа дала бы
            // ВТОРОЙ экземпляр сервиса, и резолвом это не поймать (оба варианта разрешаются,
            // разница — в числе экземпляров). Поэтому проверяем форму дескриптора. Граф
            // заметок здесь намеренно не поднимаем (см. шапку класса).
            services.Single(d => d.ServiceType == typeof(INoteAccessor))
                .ImplementationFactory.Should().NotBeNull();
            // И шов `INoteSemanticIndex` — тоже фабрика на тот же синглтон, а не второй экземпляр.
            services.Single(d => d.ServiceType == typeof(INoteSemanticIndex))
                .ImplementationFactory.Should().NotBeNull();
            // И шов `INoteTaskSync` — фабрика на `NoteTaskSyncService`, а не второй экземпляр.
            services.Single(d => d.ServiceType == typeof(INoteTaskSync))
                .ImplementationFactory.Should().NotBeNull();
        }
    }

    // Б1 (UnifiedSearchService) в динамике: реальная сборка с notes=null, kb=null не должна
    // падать, а должна тихо деградировать до поиска только по задачам.
    [Fact]
    public async Task UnifiedSearchService_NotesDisabled_FallsBackToTasksOnly()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "notes-gate-" + Guid.NewGuid().ToString("N"), "projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = dataPath })
                .Build();
            var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UserStore>.Instance);
            var ownerId = users.GetFirst()!.Id;
            var appSettings = new AppSettingsService(config);
            var projects = new ProjectManager(config, users, appSettings);
            var tasks = new ClaudeHomeServer.Services.Tasks.TaskManager(config);

            // notes/kb намеренно не передаём — конструктор обязан принять их отсутствие
            var sut = new UnifiedSearchService(tasks, projects);

            var act = () => sut.SearchAsync(ownerId, "ничего", topK: 5);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dataPath)!, recursive: true);
        }
    }
}
