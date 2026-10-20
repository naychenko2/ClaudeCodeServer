using System.Text;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Черновики build-it-yourself (ADR-031, шаг 4.1): создание из шаблона (ccs-plugin.json +
// .claude-plugin/plugin.json, name = slug), запись/удаление файлов с теми же кодами проблем,
// что у валидатора (../, абсолютный путь, symlink, deny-list, allow-list, лимиты), валидация
// (шаблон проходит без проблем) и сводка (без секретов и хостовых путей).
public class PluginDraftServiceTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginDraftService _service;

    public PluginDraftServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_drafts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _service = new PluginDraftService(_store, new PluginPackageValidator());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    private string DraftDir(string owner, string slug) => _store.DraftDir(owner, slug);

    // ── draft_create ───────────────────────────────────────────────────────────

    [Fact]
    public void Create_Шаблон_СоздаётМанифесты()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var dir = DraftDir(Owner, "my-plugin");
        File.Exists(Path.Combine(dir, "ccs-plugin.json")).Should().BeTrue("ccs-plugin.json обязателен");
        File.Exists(Path.Combine(dir, ".claude-plugin", "plugin.json")).Should().BeTrue(
            "нативный манифест обязателен (R10-1)");
        // name = slug в нативном манифесте (иначе CLI назовёт пакет именем каталога)
        var native = File.ReadAllText(Path.Combine(dir, ".claude-plugin", "plugin.json"));
        native.Should().Contain("my-plugin");
        // version 0.1.0 в ccs-plugin.json
        File.ReadAllText(Path.Combine(dir, "ccs-plugin.json")).Should().Contain("0.1.0");
    }

    [Fact]
    public void Create_Повторный_Ошибка()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var second = _service.Create(Owner, "my-plugin");
        second.Ok.Should().BeFalse("повторный create — ошибка, а не перезапись");
        second.Code.Should().Be("already-exists");
    }

    [Theory]
    [InlineData("UPPER")]     // заглавные
    [InlineData("a/b")]       // слэш (это id, а не slug)
    [InlineData("")]          // пустой
    [InlineData("a b")]       // пробел
    public void Create_НевалидныйSlug_Ошибка(string slug)
    {
        var result = _service.Create(Owner, slug);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("bad-slug");
    }

    // ── Потолок длины slug: 64 (выровнен с клиентской проверкой isDraftSlug) ─────────
    // Клиент isDraftSlug (frontend/src/features/plugins/feed/records.tsx) держит тот же 64
    // (s.length <= 64): граница не разъезжается — сервер не создаёт черновик со slug длиннее 64.
    // Мутация (критерий): убрать потолок в PluginId.IsValidSlug (Length: > 0 and <= MaxSlugLength
    // → Length: > 0) → slug 65/200 проходят IsValidSlug → Create не даёт bad-slug (создаёт) → тесты красные.

    [Fact]
    public void Create_Slug64_Принят()
    {
        var slug = new string('a', 64);
        _service.Create(Owner, slug).Ok.Should().BeTrue("slug длиной 64 — на границе, принимается");
    }

    [Theory]
    [InlineData(65)]
    [InlineData(200)]
    public void Create_SlugДлиннее64_BadSlug(int length)
    {
        var slug = new string('a', length);
        var result = _service.Create(Owner, slug);
        result.Ok.Should().BeFalse($"slug длиной {length} > 64 → bad-slug");
        result.Code.Should().Be("bad-slug");
    }

    // ── draft_write_file: путь ─────────────────────────────────────────────────

    [Fact]
    public void WriteFile_ТочкаТочка_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var result = _service.WriteFile(Owner, "my-plugin", "../escape.md", [1, 2, 3]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("within-root");
    }

    [Fact]
    public void WriteFile_АбсолютныйПуть_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var result = _service.WriteFile(Owner, "my-plugin", "/abs/path.md", [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("within-root");
    }

    [Fact]
    public void WriteFile_Symlink_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var dir = DraftDir(Owner, "my-plugin");
        // Создаём symlink-файл в черновике и пробуем в него записать
        var target = Path.Combine(_dir, "outside.txt");
        File.WriteAllText(target, "x");
        var link = Path.Combine(dir, "panel", "link.md");
        Directory.CreateDirectory(Path.Combine(dir, "panel"));
        File.Create(link).Dispose();
        File.Delete(link);
        CreateSymlink(target, link);
        var result = _service.WriteFile(Owner, "my-plugin", "panel/link.md", [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("no-symlink");
    }

    // ── draft_write_file: запрещённые места (deny-list, allow-list) ─────────────

    [Theory]
    [InlineData("hooks/pre.sh", "deny-hooks")]
    [InlineData("agents/x.md", "deny-agents")]
    [InlineData("bin/run", "deny-bin")]
    [InlineData(".mcp.json", "deny-mcp-json")]
    [InlineData("settings.json", "deny-settings")]
    public void WriteFile_Запрещённые_Отказ(string path, string code)
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var result = _service.WriteFile(Owner, "my-plugin", path, [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be(code);
    }

    [Fact]
    public void WriteFile_ВнеAllowList_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // README.md — не в allow-list (только манифесты, skills/, commands/, panel/)
        var result = _service.WriteFile(Owner, "my-plugin", "README.md", [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("not-allowed");
    }

    [Fact]
    public void WriteFile_Разрешённые_Ок()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/my-skill/SKILL.md", [1]).Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "commands/greet.md", [1]).Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "panel/index.html", [1]).Ok.Should().BeTrue();
        // Перезапись существующего файла допустима (так агент правит)
        _service.WriteFile(Owner, "my-plugin", "skills/my-skill/SKILL.md", [2]).Ok.Should().BeTrue();
    }

    // ── draft_write_file: лимиты ───────────────────────────────────────────────

    [Fact]
    public void WriteFile_ЛимитРазмераФайла_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var big = new byte[PluginPackageValidator.MaxSingleFileSizeBytes + 1];
        var result = _service.WriteFile(Owner, "my-plugin", "panel/big.bin", big);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("file-size-limit");
    }

    // ── draft_write_file: panel-file-too-large (QA-фикс H, ADR-031 §3.6) ─────────────
    // Мутация: убрать проверку MaxPanelFileBytes в WriteFile → этот тест падает.

    [Fact]
    public void WriteFile_ПанельФайлБольше256КиБ_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var big = new byte[PluginPackageValidator.MaxPanelFileBytes + 1];
        var result = _service.WriteFile(Owner, "my-plugin", "panel/big.html", big);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("panel-file-too-large");
    }

    [Fact]
    public void WriteFile_ПанельФайлРовно256КиБ_Ок()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var exact = new byte[PluginPackageValidator.MaxPanelFileBytes];
        var result = _service.WriteFile(Owner, "my-plugin", "panel/exact.html", exact);
        result.Ok.Should().BeTrue("ровно потолок → запись проходит");
    }

    [Fact]
    public void WriteFile_ЛимитОбщегоРазмера_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // Шаблон уже несёт ~240 байт; 3 файла по 5 МБ (15 МБ) проходят, 4-й (20 МБ + шаблон)
        // выталкивает пакет за 20 МБ → size-limit. Файлы под skills/a/ (не panel/), чтобы
        // не задеть panel-file-too-large (256 КиБ).
        const long FiveMb = 5 * 1024 * 1024;
        for (var i = 0; i < 3; i++)
            _service.WriteFile(Owner, "my-plugin", $"skills/a/f{i}.md", new byte[FiveMb]).Ok.Should().BeTrue();
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/f3.md", new byte[FiveMb]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("size-limit");
    }

    [Fact]
    public void WriteFile_ЛимитЧислаФайлов_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // Шаблон уже несёт 2 файла (ccs-plugin.json, .claude-plugin/plugin.json); допишем до
        // лимита, следующий → file-count-limit.
        for (var i = 0; i < PluginPackageValidator.MaxFileCount - 2; i++)
            _service.WriteFile(Owner, "my-plugin", $"panel/f{i}.bin", [1]).Ok.Should().BeTrue();
        var result = _service.WriteFile(Owner, "my-plugin", "panel/one-more.bin", [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("file-count-limit");
    }

    // ── draft_write_file: проверки содержимого (rework D1) ─────────────────────
    // Тот же метод CheckContent, что у валидатора: запись отбивается с тем же кодом,
    // файл на диск не пишется.

    [Fact]
    public void WriteFile_AllowedTools_ВSkill_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        const string content = """
        ---
        name: a
        allowed-tools: [Bash]
        ---
        Текст навыка
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-allowed-tools");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "a", "SKILL.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_BacktickExec_ВSkill_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        const string content = """
        ---
        name: a
        ---
        Навык: !`bash -c "echo hi"`
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-backtick-exec");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "a", "SKILL.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_FencedBacktick_ВCommand_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        const string content = """
        ---
        name: a
        ---
        ```!
        bash -c 'echo hi'
        ```
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "commands/cmd.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-backtick-exec");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "commands", "cmd.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_Аргументы_ВSkill_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        const string content = """
        ---
        name: a
        ---
        Навык: !$ARGUMENTS
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-backtick-exec");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "a", "SKILL.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_ОбратныйСлеш_ВоФронтматтере_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // allowed-\x74ools: Contains «allowed-tools» не срабатывает (t экранирован),
        // срабатывает запрет обратного слеша во фронтматтере (M10.2).
        const string content = """
        ---
        name: a
        allowed-\x74ools: Bash
        ---
        Текст навыка
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-allowed-tools");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "a", "SKILL.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_ЗапрещённоеПоле_ВМанифесте_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // Записываем наш манифест с запрещённым полем hooks → deny-manifest-field.
        var result = _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            Encoding.UTF8.GetBytes("""{"name":"P","version":"0.1.0","hooks":{}}"""));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-manifest-field");
        // Файл на диске не обновлён: запрещённого поля в записанном (шаблонном) манифесте нет.
        File.ReadAllText(Path.Combine(DraftDir(Owner, "my-plugin"), "ccs-plugin.json"))
            .Should().NotContain("hooks", "манифест на диске не переписан");
    }

    // ── minor 14 (D2): ВСЕ проблемы содержимого в отказе, не только первая ─────────

    [Fact]
    public void WriteFile_ДвеПроблемыСодержимого_ОбаКодаВОтказе()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // SKILL.md с allowed-tools (M10.1) И !`cmd` (M10.3) одновременно → две проблемы:
        // code — первой, message — склейка «код: текст» каждой.
        const string content = """
        ---
        name: a
        allowed-tools: [Bash]
        ---
        Навык: !`bash -c "echo hi"`
        """;
        var result = _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md", Encoding.UTF8.GetBytes(content));
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("deny-allowed-tools", "код — первой проблемы");
        result.Message!.Should().Contain("deny-allowed-tools", "код первой проблемы в сообщении");
        result.Message!.Should().Contain("deny-backtick-exec", "код второй проблемы в сообщении");
        File.Exists(Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "a", "SKILL.md"))
            .Should().BeFalse("файл на диск не записан");
    }

    [Fact]
    public void WriteFile_201_Каталог_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var dir = DraftDir(Owner, "my-plugin");
        // 200 каталогов (лимит) + файл в 201-й → dir-count-limit, файла на диске нет.
        for (var i = 0; i < PluginPackageValidator.MaxDirCount; i++)
            Directory.CreateDirectory(Path.Combine(dir, "panel", $"d{i}"));
        var result = _service.WriteFile(Owner, "my-plugin", "panel/d200/file.md", [1]);
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("dir-count-limit");
        File.Exists(Path.Combine(dir, "panel", "d200", "file.md")).Should().BeFalse("файл на диск не записан");
    }

    // ── draft_delete_file ──────────────────────────────────────────────────────

    [Fact]
    public void DeleteFile_Существующий_Удаляет()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/s/SKILL.md", [1]).Ok.Should().BeTrue();
        var path = Path.Combine(DraftDir(Owner, "my-plugin"), "skills", "s", "SKILL.md");
        File.Exists(path).Should().BeTrue();
        _service.DeleteFile(Owner, "my-plugin", "skills/s/SKILL.md").Ok.Should().BeTrue();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void DeleteFile_НетФайла_Отказ()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var result = _service.DeleteFile(Owner, "my-plugin", "nope.md");
        result.Ok.Should().BeFalse();
        result.Code.Should().Be("not-found");
    }

    // ── draft_validate: единый арбитр с установкой (ValidateFull: файлы + манифест) ─────
    // Мутация (критерий): убрать общий вызов ValidateFull (вернуть _validator.Validate) →
    // манифестные коды (no-contribution/unknown-permission) исчезают из проверки черновика →
    // тесты ниже краснеют.

    // Шаблон из Create: contributes пуст → no-contribution (тот же код, что при установке).
    // Раньше (до фикса) шаблон проходил валидацию «без проблем», а установка падала no-contribution.
    [Fact]
    public void Validate_Шаблон_NoContribution()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeFalse("шаблон без вклада → no-contribution");
        validation.Problems.Should().Contain(p => p.Code == "no-contribution");
    }

    // Черновик с вкладом (skill) и неизвестным правом → unknown-permission (тот же код, что
    // при установке). Манифест переписываем через WriteFile (разрешённые поля id/name/version/
    // contributes/permissions → deny-manifest-field не срабатывает).
    [Fact]
    public void Validate_НеизвестноеПраво_UnknownPermission()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md",
            System.Text.Encoding.UTF8.GetBytes("# Skill A\n")).Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            System.Text.Encoding.UTF8.GetBytes(
                """{"id":"local-x/my-plugin","name":"my-plugin","version":"0.1.0","contributes":{"skills":["skills/a"]},"permissions":{"requested":["unknown:perm"]}}"""))
            .Ok.Should().BeTrue();
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeFalse();
        validation.Problems.Should().Contain(p => p.Code == "unknown-permission");
    }

    // Битый манифест: запрещённое топ-уровневое поле (hooks) → deny-manifest-field (тот же
    // код, что при установке; ловится файловым валидатором через CheckContent).
    [Fact]
    public void Validate_БитыйМанифест_DenyManifestField()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md",
            System.Text.Encoding.UTF8.GetBytes("# Skill A\n")).Ok.Should().BeTrue();
        // hooks — запрещённое поле манифеста (RCE/обход) → deny-manifest-field.
        _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            System.Text.Encoding.UTF8.GetBytes(
                """{"id":"local-x/my-plugin","name":"my-plugin","version":"0.1.0","contributes":{"skills":["skills/a"]},"hooks":{}}"""))
            .Ok.Should().BeFalse("запись с запрещённым полем отбивается deny-manifest-field");
        // Записали манифест напрямую (WriteFile его отклонил), чтобы валидатор сам его нашёл.
        File.WriteAllText(Path.Combine(DraftDir(Owner, "my-plugin"), "ccs-plugin.json"),
            """{"id":"local-x/my-plugin","name":"my-plugin","version":"0.1.0","contributes":{"skills":["skills/a"]},"hooks":{}}""");
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeFalse();
        validation.Problems.Should().Contain(p => p.Code == "deny-manifest-field");
    }

    // name != slug: ccs-манифест с name, отличным от slug (из id) → deny-name-not-equal-slug
    // (тот же код, что при установке; файловая проверка CheckNameEqualsSlug).
    [Fact]
    public void Validate_NameNotEqualSlug_DenyNameNotEqualSlug()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md",
            System.Text.Encoding.UTF8.GetBytes("# Skill A\n")).Ok.Should().BeTrue();
        // name = "Другое имя" ≠ slug "my-plugin" (из id local-x/my-plugin) → deny.
        _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            System.Text.Encoding.UTF8.GetBytes(
                """{"id":"local-x/my-plugin","name":"Другое имя","version":"0.1.0","contributes":{"skills":["skills/a"]}}"""))
            .Ok.Should().BeTrue();
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeFalse();
        validation.Problems.Should().Contain(p => p.Code == "deny-name-not-equal-slug");
    }

    // Хороший черновик: вклад (skill) + известное право + name = slug + нативный манифест →
    // валиден (и при установке, и при проверке черновика — один арбитр).
    [Fact]
    public void Validate_ХорошийЧерновик_Прохходит()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md",
            System.Text.Encoding.UTF8.GetBytes("# Skill A\n")).Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            System.Text.Encoding.UTF8.GetBytes(
                """{"id":"local-x/my-plugin","name":"my-plugin","version":"0.1.0","contributes":{"skills":["skills/a"]},"permissions":{"requested":["notes:read"]}}"""))
            .Ok.Should().BeTrue();
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeTrue(
            $"хороший черновик обязан проходить: {string.Join("; ", validation.Problems.Select(p => p.Code + ": " + p.Message))}");
    }

    [Fact]
    public void Validate_Невалидный_Проблемы()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // Файл вне allow-list: WriteFile его бы отклонил (not-allowed), поэтому пишем напрямую,
        // чтобы валидатор сам его нашёл и отклонил.
        var dir = DraftDir(Owner, "my-plugin");
        File.WriteAllBytes(Path.Combine(dir, "README.md"), [1]);
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeFalse();
        validation.Problems.Should().Contain(p => p.Code == "not-allowed");
    }

    // ── draft_preview: без секретов и хостовых путей ────────────────────────────

    [Fact]
    public void Preview_БезСекретовИХостовыхПутей()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/s/SKILL.md", [1]).Ok.Should().BeTrue();
        var preview = _service.Preview(Owner, "my-plugin");
        // Хостовых путей нет: все пути файлов — относительные (без ведущего '/' и без полного пути)
        foreach (var f in preview.Files)
            f.Path.Should().NotStartWith("/", $"путь {f.Path} обязан быть относительным");
        // Секретов в шаблоне нет (contributes пуст)
        preview.Secrets.Should().BeEmpty();
        // Дерево файлов содержит записанный файл
        preview.Files.Should().Contain(f => f.Path == "skills/s/SKILL.md");
    }

    // ── Изоляция владельца ─────────────────────────────────────────────────────

    [Fact]
    public void Изоляция_ЧужойВладелецНеВидитЧерновик()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        // Чужой владелец: черновика нет (DraftDir другого владельца), Exists = false
        _service.Exists(Stranger, "my-plugin").Should().BeFalse();
        // Запись чужим владельцем — в другой каталог
        _service.Create(Stranger, "my-plugin").Ok.Should().BeTrue();
        _service.Exists(Owner, "my-plugin").Should().BeTrue("черновик владельца A на месте");
    }

    // ── reject / restore / 30-дневное хранение (шаг 4.3c) ──────────────────────

    // Часы с ручным ходом (TimeProvider): тест 30-дневного хранения без Task.Delay.
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void Reject_Переносит_ВRejected_ИЗаписываетВремя()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var op = _service.Reject(Owner, "my-plugin");
        op.Ok.Should().BeTrue();
        Directory.Exists(DraftDir(Owner, "my-plugin")).Should().BeFalse("черновик убран из _drafts");
        Directory.Exists(_store.RejectedDir(Owner, "my-plugin")).Should().BeTrue("черновик перенесён в _rejected");
        _service.RejectedMeta(Owner, "my-plugin").Should().NotBeNull("время отклонения записано");
    }

    [Fact]
    public void Reject_ЧужойВладелец_НеНайден()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var op = _service.Reject(Stranger, "my-plugin");
        op.Ok.Should().BeFalse();
        op.Code.Should().Be("not-found");
    }

    [Fact]
    public void Restore_Возвращает_ВDrafts()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.Reject(Owner, "my-plugin").Ok.Should().BeTrue();
        var op = _service.Restore(Owner, "my-plugin");
        op.Ok.Should().BeTrue();
        Directory.Exists(DraftDir(Owner, "my-plugin")).Should().BeTrue("черновик возвращён в _drafts");
        Directory.Exists(_store.RejectedDir(Owner, "my-plugin")).Should().BeFalse("_rejected пуст");
    }

    [Fact]
    public void Restore_КонфликтSlug_Ошибка()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.Reject(Owner, "my-plugin").Ok.Should().BeTrue();
        // Новый черновик того же slug занял _drafts/my-plugin.
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        var op = _service.Restore(Owner, "my-plugin");
        op.Ok.Should().BeFalse();
        op.Code.Should().Be("conflict");
    }

    [Fact]
    public void Restore_НеОтклонён_НеНайден()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue(); // активный, не отклонён
        var op = _service.Restore(Owner, "my-plugin");
        op.Ok.Should().BeFalse();
        op.Code.Should().Be("not-found");
    }

    // c5afd409: «Вернуть» убирает служебный _rejected.json (метаданные отклонения) из
    // восстановленного черновика — иначе валидатор отбивает его not-allowed и «Принять»
    // заблокировано. Мутация: не удалять файл (оставить в Restore) → тест красный.
    // Черновик делаем валидным (вклад + известное право), чтобы единственной потенциальной
    // проблемой после restore был именно служебный _rejected.json (no no-contribution).
    [Fact]
    public void Restore_УбираетСлужебныйRejectedJson_ЧерновикВалиден()
    {
        _service.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "skills/a/SKILL.md",
            System.Text.Encoding.UTF8.GetBytes("# Skill A\n")).Ok.Should().BeTrue();
        _service.WriteFile(Owner, "my-plugin", "ccs-plugin.json",
            System.Text.Encoding.UTF8.GetBytes(
                """{"id":"local-x/my-plugin","name":"my-plugin","version":"0.1.0","contributes":{"skills":["skills/a"]},"permissions":{"requested":["notes:read"]}}"""))
            .Ok.Should().BeTrue();

        _service.Reject(Owner, "my-plugin").Ok.Should().BeTrue();
        _service.Restore(Owner, "my-plugin").Ok.Should().BeTrue();

        // Служебный _rejected.json (метаданные отклонения) не остаётся в восстановленном черновике.
        var dir = DraftDir(Owner, "my-plugin");
        File.Exists(Path.Combine(dir, "_rejected.json")).Should().BeFalse(
            "служебный _rejected.json не остаётся в восстановленном черновике");

        // Черновик валиден (no not-allowed): «Принять» доступно.
        var validation = _service.Validate(Owner, "my-plugin");
        validation.IsValid.Should().BeTrue(
            $"восстановленный черновик обязан быть валиден: {string.Join("; ", validation.Problems.Select(p => p.Code + ": " + p.Message))}");
    }

    [Fact]
    public void List_Отклонённые_ПризнакRejected_ИExpiresAt()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var svc = new PluginDraftService(_store, new PluginPackageValidator(), clock);
        svc.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        svc.Reject(Owner, "my-plugin").Ok.Should().BeTrue();

        var item = svc.List(Owner).Single(s => s.Slug == "my-plugin");
        item.Rejected.Should().BeTrue("отклонённый черновик помечен rejected: true");
        item.ExpiresAt.Should().NotBeNull("дата исчезновения задана");
        item.ExpiresAt!.Value.Should().Be(
            new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc).AddDays(PluginDraftService.RejectedRetentionDays),
            "expiresAt = время отклонения + 30 дней");
    }

    [Fact]
    public void List_ПросроченныеОтклонённые_УдаляютсяЛениво()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var svc = new PluginDraftService(_store, new PluginPackageValidator(), clock);
        svc.Create(Owner, "my-plugin").Ok.Should().BeTrue();
        svc.Reject(Owner, "my-plugin").Ok.Should().BeTrue();

        // 29 дней — ещё не просрочено: черновик в списке.
        clock.Advance(TimeSpan.FromDays(29));
        svc.List(Owner).Should().Contain(s => s.Slug == "my-plugin" && s.Rejected);

        // 31 день — просрочено: ленивое удаление при перечислении.
        clock.Advance(TimeSpan.FromDays(2));
        svc.List(Owner).Should().NotContain(s => s.Slug == "my-plugin");
        Directory.Exists(_store.RejectedDir(Owner, "my-plugin")).Should().BeFalse("просроченный отклонённый удалён");
    }

    // ── Хелпер: symlink (кроссплатформенно) ─────────────────────────────────────

    private static void CreateSymlink(string target, string link)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (PlatformNotSupportedException)
        {
            // Fallback: обычный файл (на Linux CreateSymbolicLink работает; на Windows без
            // привилегий — нет, и тест symlink тогда не создаёт ссылки, но ReparsePoint всё равно
            // проверяется валидатором).
            File.Copy(target, link);
        }
    }
}
