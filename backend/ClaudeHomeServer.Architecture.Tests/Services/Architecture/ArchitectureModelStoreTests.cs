using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Хранилище модели раздела «Архитектура»: файл docs/architecture/model.viaduct.json
// в проекте, запись байт-в-байт, версия SHA-256 → конфликт; контроллер — владение проектом.
public class ArchitectureModelStoreTests : IDisposable
{
    private const string ModelV1 = """{"state":{"model":{"systems":[],"viewLevel":"system"}},"version":0}""";
    private const string ModelV2 = """{"state":{"model":{"systems":[{"id":"s1","name":"CCS"}],"viewLevel":"system"}},"version":0}""";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly ArchitectureModelStore _store = new();

    public ArchitectureModelStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arch_store_tests_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string ModelPath => Path.Combine(_root, "docs", "architecture", "model.viaduct.json");

    // Срыв подмены (на месте файла — каталог): ошибка пробрасывается, а .tmp в проекте не остаётся.
    // Оба пути атомарной записи — хранилища (редактор/meta) и генератора («Собрать из кода»).
    [Fact]
    public async Task Срыв_атомарной_записи__tmp_не_остаётся()
    {
        Directory.CreateDirectory(ModelPath);

        var store = () => ArchitectureModelStore.WriteAtomicAsync(ModelPath, [1, 2, 3], CancellationToken.None);
        await store.Should().ThrowAsync<Exception>();
        File.Exists(ModelPath + ".tmp").Should().BeFalse();

        var generator = () => ArchitectureModelGenerator.WriteAtomicAsync(ModelPath, "{}", CancellationToken.None);
        await generator.Should().ThrowAsync<Exception>();
        File.Exists(ModelPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Модели_нет__чтение_отдаёт_пустое_без_версии()
    {
        var snap = await _store.ReadAsync(_root, default);

        snap.Content.Should().BeNull();
        snap.Version.Should().BeNull();
    }

    [Fact]
    public async Task Запись_байт_в_байт_и_чтение_тем_же_содержимым_с_той_же_версией()
    {
        var saved = await _store.WriteAsync(_root, ModelV1, null, "Гриша", default);

        saved.Saved.Should().BeTrue();
        File.ReadAllText(ModelPath).Should().Be(ModelV1);
        var read = await _store.ReadAsync(_root, default);
        read.Content.Should().Be(ModelV1);
        read.Version.Should().Be(saved.Current.Version).And.HaveLength(64);
        read.Author.UpdatedBy.Should().Be("Гриша");
        read.Author.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Устаревшая_версия__конфликт_и_файл_не_тронут()
    {
        var first = await _store.WriteAsync(_root, ModelV1, null, "вкладка 1", default);
        // Вторая вкладка открыла ту же версию и успела сохранить первой
        var second = await _store.WriteAsync(_root, ModelV2, first.Current.Version, "вкладка 2", default);
        second.Saved.Should().BeTrue();

        var stale = await _store.WriteAsync(_root, ModelV1 + " ", first.Current.Version, "вкладка 1", default);

        stale.Saved.Should().BeFalse();
        stale.Current.Version.Should().Be(second.Current.Version);
        stale.Current.Content.Should().Be(ModelV2);
        stale.Current.Author.UpdatedBy.Should().Be("вкладка 2");
        File.ReadAllText(ModelPath).Should().Be(ModelV2);
    }

    [Fact]
    public async Task Файл_есть__а_клиент_считает_что_модели_нет__конфликт()
    {
        await _store.WriteAsync(_root, ModelV1, null, "a", default);

        var outcome = await _store.WriteAsync(_root, ModelV2, null, "b", default);

        outcome.Saved.Should().BeFalse();
        File.ReadAllText(ModelPath).Should().Be(ModelV1);
    }

    [Fact]
    public async Task Правка_файла_мимо_хранилища__меняет_версию()
    {
        var first = await _store.WriteAsync(_root, ModelV1, null, "a", default);
        // Персона/генератор/git checkout переписали файл напрямую
        File.WriteAllText(ModelPath, ModelV2);

        var outcome = await _store.WriteAsync(_root, ModelV1, first.Current.Version, "a", default);

        outcome.Saved.Should().BeFalse();
        outcome.Current.Content.Should().Be(ModelV2);
    }

    [Fact]
    public async Task Файл_удалили_мимо_хранилища__правка_от_старой_версии_его_не_воскрешает()
    {
        var first = await _store.WriteAsync(_root, ModelV1, null, "a", default);
        // Файлы модели удалили руками/через git, а холст ещё держит прежнюю версию
        File.Delete(ModelPath);
        File.Delete(Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json"));

        var outcome = await _store.WriteAsync(_root, ModelV2, first.Current.Version, "a", default);

        outcome.Saved.Should().BeFalse();
        outcome.Current.Content.Should().BeNull();
        outcome.Current.Version.Should().BeNull();
        File.Exists(ModelPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("[1,2]")]
    [InlineData("\"строка\"")]
    public async Task Не_JSON_объект__отказ_без_записи(string content)
    {
        var act = () => _store.WriteAsync(_root, content, null, "a", default);

        await act.Should().ThrowAsync<ArchitectureModelInvalidException>();
        File.Exists(ModelPath).Should().BeFalse();
    }

    [Fact]
    public async Task Метаданные_генератора_сохраняются_при_записи_из_редактора()
    {
        var metaPath = Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, """{"generator":"ccs-code-v1","graphBuiltAt":"2026-09-25T10:00:00Z","generatedAt":"2026-09-25T10:00:00Z"}""");

        await _store.WriteAsync(_root, ModelV1, null, "Гриша", default);

        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        meta["graphBuiltAt"]!.GetValue<string>().Should().Be("2026-09-25T10:00:00Z");
        meta["updatedBy"]!.GetValue<string>().Should().Be("Гриша");
        (await _store.ReadAsync(_root, default)).Author.UpdatedBy.Should().Be("Гриша");
    }

    [Fact]
    public async Task Свежая_сборка_из_кода__автор_сборка()
    {
        var metaPath = Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, """{"generatedAt":"2099-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","updatedBy":"Гриша"}""");

        var snap = await _store.ReadAsync(_root, default);

        snap.Author.UpdatedBy.Should().Be(ArchitectureModelStore.GeneratorAuthor);
    }

    [Fact]
    public async Task Запись_не_выходит_за_корень_проекта_и_не_задевает_соседа_с_тем_же_префиксом()
    {
        var sibling = _root + "2";
        Directory.CreateDirectory(sibling);

        await _store.WriteAsync(_root, ModelV1, null, "a", default);

        Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Should().BeEquivalentTo("docs/architecture/model.viaduct.json", "docs/architecture/model.viaduct.meta.json");
        Directory.EnumerateFileSystemEntries(sibling).Should().BeEmpty();
    }

    // ── Контроллер: владение проектом и коды ответов ──

    private (ArchitectureController Controller, ProjectManager Projects) Controller(string userId,
        ArchitectureModelGenerator? generator = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = _tempDir,
            })
            .Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var controller = new ArchitectureController(projects, users,
            NullLogger<ArchitectureController>.Instance,
            generator ?? new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance), _store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
                },
            },
        };
        return (controller, projects);
    }

    [Fact]
    public async Task Чужой_проект__403_и_файл_не_создан()
    {
        var (controller, projects) = Controller("mallory");
        var project = projects.Create("Alice", _root, "alice", "alice", createDirectory: false);

        (await controller.GetModel(project.Id, default)).Should().BeOfType<ForbidResult>();
        (await controller.PutModel(project.Id, new ArchitectureModelPutRequest(ModelV1, null), default))
            .Should().BeOfType<ForbidResult>();
        File.Exists(ModelPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("нет-такого")]
    [InlineData("../../etc")]
    [InlineData("..%2F..%2Fproj")]
    public async Task Неизвестный_или_кривой_projectId__404(string projectId)
    {
        var (controller, _) = Controller("alice");

        (await controller.PutModel(projectId, new ArchitectureModelPutRequest(ModelV1, null), default))
            .Should().BeOfType<NotFoundResult>();
        File.Exists(ModelPath).Should().BeFalse();
    }

    [Fact]
    public async Task Две_вкладки__вторая_получает_409_с_текущим_состоянием()
    {
        var (controller, projects) = Controller("alice");
        var project = projects.Create("Alice", _root, "alice", "alice", createDirectory: false);

        var first = await controller.PutModel(project.Id, new ArchitectureModelPutRequest(ModelV1, null), default);
        first.Should().BeOfType<OkObjectResult>();
        var baseVersion = (await _store.ReadAsync(_root, default)).Version;
        (await controller.PutModel(project.Id, new ArchitectureModelPutRequest(ModelV2, baseVersion), default))
            .Should().BeOfType<OkObjectResult>();

        var stale = await controller.PutModel(project.Id, new ArchitectureModelPutRequest(ModelV1 + " ", baseVersion), default);

        var conflict = stale.Should().BeOfType<ConflictObjectResult>().Subject;
        var json = JsonSerializer.SerializeToElement(conflict.Value);
        json.GetProperty("code").GetString().Should().Be("version_conflict");
        json.GetProperty("current").GetProperty("content").GetString().Should().Be(ModelV2);
    }

    // Выключенная подсистема до контроллера не доходит (модуль не загружается) —
    // проверяется на полном хосте в ArchitectureModuleEndpointTests.

    // Шов снимка CodeGraph не зарегистрирован (подсистема CodeGraph выключена) — сборка
    // из кода честно отвечает 503 graph_unavailable, а файл модели не трогается
    [Fact]
    public async Task Сборка_без_шва_CodeGraph__свой_проект__503_graph_unavailable()
    {
        var (controller, projects) = Controller("alice",
            new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance));
        var project = projects.Create("Alice", _root, "alice", "alice", createDirectory: false);

        var result = await controller.Generate(project.Id, null, default);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(503);
        JsonSerializer.SerializeToElement(obj.Value).GetProperty("code").GetString().Should().Be("graph_unavailable");
        File.Exists(ModelPath).Should().BeFalse();
    }

    // Владение проверяется раньше шва: чужой проект получает 403, а не 503 —
    // иначе по коду ответа можно было бы узнать о существовании чужого проекта
    [Fact]
    public async Task Сборка_без_шва_CodeGraph__чужой_проект__403()
    {
        var (controller, projects) = Controller("mallory",
            new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance));
        var project = projects.Create("Alice", _root, "alice", "alice", createDirectory: false);

        (await controller.Generate(project.Id, null, default)).Should().BeOfType<ForbidResult>();
        File.Exists(ModelPath).Should().BeFalse();
    }
}
