using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm.DeepSeek;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты инструментов DeepSeek: защита от path traversal, семантика edit_file, обрезка вывода.
/// </summary>
public class DeepSeekToolsTests : IDisposable
{
    private readonly string _root;
    private readonly DeepSeekToolRegistry _registry;

    public DeepSeekToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ds_tools_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _registry = new DeepSeekToolRegistry(_root, new FileService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Args(object anon) =>
        JsonDocument.Parse(JsonSerializer.Serialize(anon)).RootElement;

    private Task<DsToolResult> RunAsync(string tool, object args) =>
        _registry.Get(tool)!.ExecuteAsync(Args(args), CancellationToken.None);

    [Fact]
    public void Registry_СодержитВесьНабор()
    {
        _registry.All.Select(t => t.Name).Should().BeEquivalentTo(
            ["read_file", "list_dir", "grep_search", "write_file", "edit_file", "run_command"]);
    }

    [Fact]
    public void Registry_БезShell_НеСодержитRunCommand()
    {
        var registry = new DeepSeekToolRegistry(_root, new FileService(), enableShell: false);

        registry.Get("run_command").Should().BeNull();
    }

    [Fact]
    public void BuildToolsJson_OpenAiФормат()
    {
        var json = _registry.BuildToolsJson();

        json.Should().HaveCount(6);
        var fn = json[0]!["function"]!;
        json[0]!["type"]!.GetValue<string>().Should().Be("function");
        fn["name"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        fn["parameters"]!["type"]!.GetValue<string>().Should().Be("object");
    }

    [Fact]
    public async Task RunCommand_ВыполняетКомандуИВозвращаетВывод()
    {
        var result = await RunAsync("run_command", new { command = "echo привет-из-оболочки" });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("привет-из-оболочки").And.Contain("[exit code: 0]");
    }

    [Fact]
    public async Task RunCommand_НенулевойКодВыхода_IsError()
    {
        var result = await RunAsync("run_command", new { command = "exit 3" });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("[exit code: 3]");
    }

    [Fact]
    public void RunCommand_КлассExecute()
    {
        _registry.Get("run_command")!.PermissionClass.Should().Be(ToolPermissionClass.Execute);
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    public async Task PathTraversal_НеВыходитЗаКореньПроекта(string tool)
    {
        var result = await RunAsync(tool, new { path = "../../secret.txt", content = "x" });

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ReadFile_ВозвращаетСтрокиСНомерами()
    {
        File.WriteAllLines(Path.Combine(_root, "a.txt"), ["первая", "вторая"]);

        var result = await RunAsync("read_file", new { path = "a.txt" });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("1\tпервая").And.Contain("2\tвторая");
    }

    [Fact]
    public async Task ReadFile_НесуществующийФайл_Ошибка()
    {
        var result = await RunAsync("read_file", new { path = "нет.txt" });

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task WriteFile_СоздаётФайл()
    {
        var result = await RunAsync("write_file", new { path = "новый/файл.txt", content = "привет" });

        result.IsError.Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "новый", "файл.txt")).Should().Be("привет");
    }

    [Fact]
    public async Task EditFile_ЗаменяетУникальныйФрагмент()
    {
        File.WriteAllText(Path.Combine(_root, "e.txt"), "раз два три");

        var result = await RunAsync("edit_file", new { path = "e.txt", old_string = "два", new_string = "2" });

        result.IsError.Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "e.txt")).Should().Be("раз 2 три");
    }

    [Fact]
    public async Task EditFile_ФрагментНеНайден_Ошибка()
    {
        File.WriteAllText(Path.Combine(_root, "e.txt"), "раз два три");

        var result = await RunAsync("edit_file", new { path = "e.txt", old_string = "четыре", new_string = "4" });

        result.IsError.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "e.txt")).Should().Be("раз два три");
    }

    [Fact]
    public async Task EditFile_НеуникальныйФрагмент_Ошибка()
    {
        File.WriteAllText(Path.Combine(_root, "e.txt"), "два два");

        var result = await RunAsync("edit_file", new { path = "e.txt", old_string = "два", new_string = "2" });

        result.IsError.Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "e.txt")).Should().Be("два два");
    }

    [Fact]
    public async Task GrepSearch_НаходитСовпаденияСПутёмИНомером()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllLines(Path.Combine(_root, "src", "code.cs"), ["var x = 1;", "var игла = 2;"]);

        var result = await RunAsync("grep_search", new { pattern = "игла" });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("src/code.cs:2:");
    }

    [Fact]
    public async Task GrepSearch_ИгнорируетСлужебныеПапки()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep.js"), "игла");

        var result = await RunAsync("grep_search", new { pattern = "игла" });

        result.Content.Should().Be("Совпадений не найдено");
    }

    [Fact]
    public async Task ListDir_ПоказываетФайлыИПапки()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        var result = await RunAsync("list_dir", new { });

        result.Content.Should().Contain("docs/").And.Contain("a.txt");
    }

    [Fact]
    public void Truncate_ДлинныйВывод_ОбрезаетсяСПометкой()
    {
        var text = new string('ы', DeepSeekToolRegistry.MaxResultChars + 100);

        var result = DeepSeekToolRegistry.Truncate(text);

        result.Length.Should().BeLessThan(text.Length);
        result.Should().EndWith("…(вывод обрезан)");
    }
}
