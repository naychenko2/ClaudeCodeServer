using ClaudeHomeServer.Models.Plugins;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Валидатор пакета плагина (ADR-031 §2.3, шаг 1.3): allow-list файлов пакета.
// На каждый запрещённый вход — отдельный тест (мутация проверки даёт красный);
// позитив — валидный пакет (манифест + skills + panel) даёт IsValid и sha256.
//
// Тесты живут в ClaudeHomeServer.Tests (не в отдельной ClaudeHomeServer.Plugins.Tests):
// вертикаль — чистая библиотека под Web SDK, стенду не нужен Program из Main.
public class PluginPackageValidatorTests
{
    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), "ccs-pluginpkg-" + Guid.NewGuid().ToString("N"));

    // Пакет-корень с файлами: rel — относительный путь (через «/»), создаёт подкаталоги.
    private static string NewPackage(params (string Rel, string Content)[] files)
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        foreach (var (rel, content) in files)
            WriteFile(root, rel, content);
        return root;
    }

    private static void WriteFile(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void WriteBytes(string root, string rel, long size)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    private static void MakeSymlink(string root, string linkRel, string target)
    {
        var link = Path.Combine(root, linkRel);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
    }

    private static PluginPackageValidationResult Validate(string root) =>
        new PluginPackageValidator().Validate(root);

    private static bool Has(PluginPackageValidationResult r, string code) =>
        r.Problems.Any(p => p.Code == code);

    // --- Позитив: валидный пакет (манифест + skills + panel) ---

    [Fact]
    public void ВалидныйПакет_МанифестSkillsPanel_ДаетIsValidИSha256()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/myskill/SKILL.md", "# My skill\nТекст навыка"),
            ("skills/myskill/extra.md", "Вспомогательный .md"),
            ("commands/myskill.md", "Slash-команда"),
            ("panel/index.html", "<html><body>Панель</body></html>"));

        var r = Validate(root);

        r.IsValid.Should().BeTrue($"проблемы: {string.Join("; ", r.Problems.Select(p => p.Code + ": " + p.Message))}");
        r.Sha256.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Sha256_Детерминирован_ДляОдинаковогоПакета()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md", "текст"),
            ("panel/index.html", "<html></html>"));

        var first = Validate(root);
        var second = Validate(root);

        first.Sha256.Should().NotBeNullOrEmpty();
        first.Sha256.Should().Be(second.Sha256);
    }

    // --- Запрещённые файлы (RCE/обход) ---

    [Fact]
    public void Hooks_ДаетDenyHooks()
    {
        var root = NewPackage(("hooks/hooks.json", """{"hooks":{}}"""));
        Validate(root).Problems.Should().Contain(p => p.Code == "deny-hooks");
    }

    [Fact]
    public void McpJson_ДаетDenyMcpJson()
    {
        var root = NewPackage((".mcp.json", """{"mcpServers":{}}"""));
        Validate(root).Problems.Should().Contain(p => p.Code == "deny-mcp-json");
    }

    [Fact]
    public void Agents_ДаетDenyAgents()
    {
        var root = NewPackage(("agents/my-agent.md", "# Agent"));
        Validate(root).Problems.Should().Contain(p => p.Code == "deny-agents");
    }

    [Fact]
    public void Bin_ДаетDenyBin()
    {
        var root = NewPackage(("bin/tool.sh", "#!/bin/sh\necho hi"));
        Validate(root).Problems.Should().Contain(p => p.Code == "deny-bin");
    }

    [Fact]
    public void SettingsJson_ДаетDenySettings()
    {
        var root = NewPackage(("settings.json", """{"model":"haiku"}"""));
        Validate(root).Problems.Should().Contain(p => p.Code == "deny-settings");
    }

    // --- Символическая ссылка ---

    [Fact]
    public void СимволическаяСсылка_ДаетNoSymlink()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        WriteFile(root, "panel/real.html", "<html></html>");
        MakeSymlink(root, "panel/link.html", Path.Combine(root, "panel/real.html"));

        Validate(root).Problems.Should().Contain(p => p.Code == "no-symlink");
    }

    // --- Выход за корень (symlink, чей target вне корня) ---

    [Fact]
    public void СсылкаНаФайлВнеКорня_ДаетWithinRoot()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-" + Path.GetRandomFileName());
        Directory.CreateDirectory(outside);
        WriteFile(outside, "esc.md", "вне корня");
        MakeSymlink(root, "esc-link.md", Path.Combine(outside, "esc.md"));

        Validate(root).Problems.Should().Contain(p => p.Code == "within-root");
    }

    // --- Лимит общего размера пакета ---

    [Fact]
    public void ПакетБольшелимита_ДаетSizeLimit()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 6 файлов по 4 МБ = 24 МБ > лимита 20 МБ; каждый < 5 МБ (file-size-limit не срабатывает).
        for (var i = 0; i < 6; i++)
            WriteBytes(root, $"panel/big{i}.bin", 4 * 1024 * 1024);

        Validate(root).Problems.Should().Contain(p => p.Code == "size-limit");
    }

    // --- Лимит числа файлов ---

    [Fact]
    public void ФайловБольшеЛимита_ДаетFileCountLimit()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 501 файл > лимита 500.
        for (var i = 0; i < PluginPackageValidator.MaxFileCount + 1; i++)
            WriteFile(root, $"panel/f{i}.txt", "x");

        Validate(root).Problems.Should().Contain(p => p.Code == "file-count-limit");
    }

    // --- Лимит размера отдельного файла ---

    [Fact]
    public void ФайлБольшеЛимита_ДаетFileSizeLimit()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 6 МБ > лимита 5 МБ.
        WriteBytes(root, "panel/huge.bin", 6 * 1024 * 1024);

        Validate(root).Problems.Should().Contain(p => p.Code == "file-size-limit");
    }
}
