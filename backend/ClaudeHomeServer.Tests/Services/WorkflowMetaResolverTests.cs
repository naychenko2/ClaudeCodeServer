using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class WorkflowMetaResolverTests : IDisposable
{
    private readonly string _dir;

    public WorkflowMetaResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wfmeta_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteScript(string name, string content)
    {
        var path = Path.Combine(_dir, name + ".js");
        File.WriteAllText(path, content);
        return path;
    }

    private const string PanelScript = """
        export const meta = {
          name: 'panel-of-experts',
          description: 'Многоагентная дискуссия',
          phases: [
            { title: 'Раунд 1' },
            { title: 'Финальный синтез' },
          ],
        }

        const topic = args.topic
        phase('Раунд 1')
        """;

    [Fact]
    public void Вырезает_meta_блок_с_фазами()
    {
        // Уникальное имя, чтобы не поймать одноимённый скрипт из claude-defaults-фолбэка
        WriteScript("panel-test-unique", PanelScript);

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir], "panel-test-unique");

        block.Should().NotBeNull();
        block.Should().StartWith("export const meta");
        block.Should().Contain("phases:");
        block.Should().Contain("Финальный синтез");
        // Тело скрипта после meta-блока в вырезку не попадает
        block.Should().NotContain("phase('Раунд 1')");
    }

    [Fact]
    public void Нет_файла_возвращает_null()
    {
        WorkflowMetaResolver.TryGetMetaBlock([_dir], "не-существует-xyz").Should().BeNull();
    }

    [Fact]
    public void Скрипт_без_meta_возвращает_null()
    {
        WriteScript("no-meta-unique", "const x = 1\nphase('a')\n");
        WorkflowMetaResolver.TryGetMetaBlock([_dir], "no-meta-unique").Should().BeNull();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Небезопасное_имя_отклоняется(string name)
    {
        WorkflowMetaResolver.TryGetMetaBlock([_dir], name).Should().BeNull();
    }

    [Fact]
    public void Первый_каталог_приоритетнее()
    {
        var dir2 = Path.Combine(_dir, "second");
        Directory.CreateDirectory(dir2);
        WriteScript("dup-unique", "export const meta = { name: 'first' }");
        File.WriteAllText(Path.Combine(dir2, "dup-unique.js"), "export const meta = { name: 'second' }");

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir, dir2], "dup-unique");

        block.Should().Contain("first");
        block.Should().NotContain("second");
    }
}
