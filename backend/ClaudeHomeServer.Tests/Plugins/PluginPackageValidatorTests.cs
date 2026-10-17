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

    // Каталог-symlink (C4): ссылка на каталог, не на файл.
    private static void MakeDirSymlink(string root, string linkRel, string target)
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

    // --- C4: symlink-каталог → no-symlink (не только symlink-файл) ---

    [Fact]
    public void СсылкаНаКаталог_ДаетNoSymlink()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // Реальный каталог с 10 файлами (вне пакета).
        var realDir = Path.Combine(Path.GetDirectoryName(root)!, "real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(realDir);
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(realDir, $"f{i}.txt"), "x");
        // Каталог-symlink на него, внутри пакета.
        MakeDirSymlink(root, "panel/link-dir", realDir);

        Validate(root).Problems.Should().Contain(p => p.Code == "no-symlink");
    }

    // --- M9: лимит MaxFileCount прерывает обход (1001 файл, не все перечислены) ---

    [Fact]
    public void ЛимитЧислаФайлов_ПрерываетОбход()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 1001 файл > лимита 500: обход прерывается на 501-м.
        var total = PluginPackageValidator.MaxFileCount + 501;
        for (var i = 0; i < total; i++)
            WriteFile(root, $"panel/f{i}.txt", "x");

        var r = Validate(root);
        r.Problems.Should().Contain(p => p.Code == "file-count-limit");
        // Обрыв: перечислено ≤ лимита, а не все 1001.
        r.ScannedFileCount.Should().BeLessThan(total);
    }

    // --- M9: лимит MaxTotalSizeBytes прерывает обход, до чтения байтов последнего файла ---

    [Fact]
    public void ЛимитРазмера_ПрерываетОбходДоПоследнегоФайла()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 10 файлов по 3 МБ = 30 МБ > лимита 20 МБ; каждый < 5 МБ (file-size-limit нет).
        // Лимит срабатывает на 7-м файле (21 МБ); файлы 8-10 не перечислены.
        for (var i = 0; i < 10; i++)
            WriteBytes(root, $"panel/big{i}.bin", 3 * 1024 * 1024);

        var r = Validate(root);
        r.Problems.Should().Contain(p => p.Code == "size-limit");
        // Обрыв: перечислено < 10 файлов (лимит на 7-м), не все 10.
        r.ScannedFileCount.Should().BeLessThan(10);
    }

    // --- minor: EscapesRoot — «..» в ИМЕНИ файла (не отдельный компонент) НЕ выход за корень ---

    [Fact]
    public void ДвеТочкиВИмениФайла_НеВыходЗаКорень()
    {
        // Файл с «..» в имени (panel/..file.txt): «..» не отдельный компонент пути.
        var root = NewPackage(("panel/..file.txt", "x"));

        Validate(root).Problems.Should().NotContain(p => p.Code == "within-root");
    }

    // --- minor: битая symlink (target не существует) → читаемая, не 500 ---

    [Fact]
    public void БитаяСсылка_ВалидацияНеПадает()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        WriteFile(root, "panel/real.html", "<html></html>");
        // Битая ссылка: target вне пакета и не существует.
        var missing = Path.Combine(Path.GetDirectoryName(root)!, "missing-" + Path.GetRandomFileName());
        MakeSymlink(root, "panel/broken.html", missing);

        var r = Validate(root); // не бросает (FileInfo.Length / File.OpenRead ловятся)
        r.Problems.Should().Contain(p => p.Code == "no-symlink");
    }

    // --- M10: allowed-tools во фронтматтере SKILL.md → deny-allowed-tools (Bash без запроса) ---

    [Fact]
    public void M10_AllowedTools_ВSkillFrontmatter_ДаетDenyAllowedTools()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                allowed-tools: [Bash]
                ---
                Текст навыка
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-allowed-tools");
    }

    // --- M10: allowed-tools во фронтматтере commands/*.md → deny-allowed-tools ---

    [Fact]
    public void M10_AllowedTools_ВCommandFrontmatter_ДаетDenyAllowedTools()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("commands/cmd.md",
                """
                ---
                allowed-tools: Bash, Write
                ---
                Тело команды
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-allowed-tools");
    }

    // --- M10: кавычечный ключ "allowed-tools" → deny-allowed-tools (CLI видит ключ в кавычках) ---

    [Fact]
    public void M10_AllowedTools_КавычечныйКлюч_ДаетDenyAllowedTools()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                "allowed-tools": Bash
                ---
                Текст навыка
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-allowed-tools");
    }

    // --- M10: Allowed-Tools (другой регистр) → deny-allowed-tools (сравнение без учёта регистра) ---

    [Fact]
    public void M10_AllowedTools_ДругойРегистр_ДаетDenyAllowedTools()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                Allowed-Tools: Bash
                ---
                Текст навыка
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-allowed-tools");
    }

    // --- M10: allowed-tools среди прочего текста (описание, не декларация) → НЕ отбивается ---

    [Fact]
    public void M10_AllowedTools_СредиПрочегоТекста_НеОтбивается()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                description: uses allowed-tools for X
                ---
                Просто текст навыка.
                """));

        // «allowed-tools» в описании (без двоеточия после ключа) — не декларация.
        Validate(root).Problems.Should().NotContain(p => p.Code == "deny-allowed-tools");
    }

    // --- M10: !`cmd` в теле SKILL.md → deny-backtick-exec (исполняет shell) ---

    [Fact]
    public void M10_Backtick_ВSkillBody_ДаетDenyBacktickExec()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                ---
                Навык: !`bash -c "echo hi"`
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-backtick-exec");
    }

    // --- M10: !`cmd` в теле commands/*.md → deny-backtick-exec ---

    [Fact]
    public void M10_Backtick_ВCommandBody_ДаетDenyBacktickExec()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("commands/cmd.md",
                """
                Тело: !`bash -c "echo hi"`
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-backtick-exec");
    }

    // --- M10: fenced ```! (блок-команда) → deny-backtick-exec (старый парсер фронтматтера
    // не видел fenced-блок; теперь проверяется весь текст) ---

    [Fact]
    public void M10_FencedBacktick_ВSkillBody_ДаетDenyBacktickExec()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                ---
                ```!
                bash -c 'echo hi'
                ```
                """));

        Validate(root).Problems.Should().Contain(p => p.Code == "deny-backtick-exec");
    }

    // --- M10: `---` посреди строки (не граница фронтматтера) → без флагов, зелёный ---

    [Fact]
    public void M10_ТриТочкиПосредиСтроки_БезФлагов()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                description: разделитель --- посреди строки
                ---
                Текст: a --- b, без allowed-tools и backtick.
                """));

        var r = Validate(root);
        r.Problems.Should().NotContain(p => p.Code == "deny-allowed-tools");
        r.Problems.Should().NotContain(p => p.Code == "deny-backtick-exec");
    }

    // --- M10: чистый .md (без allowed-tools и !`) → без M10-проблем ---

    [Fact]
    public void M10_ЧистыйMd_БезM10Проблем()
    {
        var root = NewPackage(
            ("ccs-plugin.json", """{"id":"local-x/pkg","name":"P","version":"0.1.0"}"""),
            ("skills/a/SKILL.md",
                """
                ---
                name: a
                description: обычный навык
                ---
                Просто текст навыка, без backtick и allowed-tools.
                """),
            ("commands/cmd.md", "Просто команда, без frontmatter-ловушек."));

        var r = Validate(root);
        r.Problems.Should().NotContain(p => p.Code == "deny-allowed-tools");
        r.Problems.Should().NotContain(p => p.Code == "deny-backtick-exec");
    }

    // --- minor 8: лимит числа каталогов (пустые не считаются в лимит файлов) ---

    [Fact]
    public void Minor8_КаталоговБольшеЛимита_ДаетDirCountLimit()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        // 201 каталог (пустых) > лимита 200; файлов нет (лимит файлов не срабатывает).
        for (var i = 0; i < PluginPackageValidator.MaxDirCount + 1; i++)
            Directory.CreateDirectory(Path.Combine(root, $"panel/d{i}"));

        Validate(root).Problems.Should().Contain(p => p.Code == "dir-count-limit");
    }
}
