using System.Text.Json.Nodes;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class LlmProviderRegistryTests
{
    private static LlmProviderRegistry Create(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiBaseUrl"] = "https://api.deepseek.com",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:SmallModel"] = "deepseek-v4-flash",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:DisplayName"] = "DeepSeek Pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceInHitPer1M"] = "0.1",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            ["LlmProviders:deepseek:SupportsImages"] = "false",
            // GLM без ключа — выключен
            ["LlmProviders:glm:DisplayName"] = "GLM",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
            ["LlmProviders:glm:ExtraEnv:API_TIMEOUT_MS"] = "3000000",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var config = TestConfig.Build(settings);
        return new LlmProviderRegistry(config);
    }

    [Fact]
    public void ResolveByModel_МодельИзКаталога_НаходитПровайдера()
    {
        Create().ResolveByModel("deepseek-v4-pro")!.Key.Should().Be("deepseek");
    }

    // Тир-алиас в ANTHROPIC_MODEL не резолвится CLI (уходит в API сырым id и валит ход
    // «issue with the selected model») — env-дефолты ставятся только для полных id.
    // Включая суффикс окна: opus[1m] отсекается так же, как голый opus (регресс
    // 89bb8bd5 — пока суффикс срезался раньше этого места, защита не срабатывала).
    [Theory]
    [InlineData("opus")]
    [InlineData("sonnet")]
    [InlineData("Haiku")]
    [InlineData("opus[1m]")]
    [InlineData("sonnet[1m]")]
    [InlineData("haiku[1m]")]
    public void BuildOAuthCliEnv_ТирАлиас_БезEnvМодели(string alias)
    {
        var env = Create().BuildOAuthCliEnv("second", "tok-123", model: alias)!;
        env.Should().ContainKey("CLAUDE_CODE_OAUTH_TOKEN");
        env.Should().NotContainKey("ANTHROPIC_MODEL");
        env.Should().NotContainKey("ANTHROPIC_DEFAULT_OPUS_MODEL");
    }

    // Полные id (в т.ч. с окном claude-fable-5[1m]) и модели сторонних провайдеров
    // (glm-5.2[1m]) — суффикс разбирает сам CLI, env-дефолты им нужны
    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-fable-5[1m]")]
    [InlineData("glm-5.2[1m]")]
    public void BuildOAuthCliEnv_ПолныйId_СтавитEnvМодель(string model)
    {
        var env = Create().BuildOAuthCliEnv("second", "tok-123", model: model)!;
        env["ANTHROPIC_MODEL"].Should().Be(model);
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"].Should().Be(model);
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"].Should().Be(model);
    }

    [Fact]
    public void ResolveByModel_ПоПрефиксу_НаходитПровайдера()
    {
        // Модель не из конфига (пришла из GET /models) — резолв по префиксу ключа
        Create().ResolveByModel("deepseek-v5-super")!.Key.Should().Be("deepseek");
    }

    // Агрегатор (OpenRouter) несёт id вида "deepseek/…", начинающиеся с ключа прямого
    // провайдера. Побеждать должен САМЫЙ ДЛИННЫЙ префикс, иначе ход уехал бы к DeepSeek —
    // на его эндпоинт с его ключом, но с несуществующей там моделью
    [Theory]
    [InlineData("deepseek/deepseek-v9-unknown", "openrouter")]
    [InlineData("openai/gpt-9", "openrouter")]
    [InlineData("deepseek-v5-super", "deepseek")]
    public void ResolveByModel_ПрефиксАгрегатора_ПобеждаетДлиннейший(string model, string expected)
    {
        var registry = Create(new Dictionary<string, string?>
        {
            ["LlmProviders:openrouter:DisplayName"] = "OpenRouter",
            ["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api",
            ["LlmProviders:openrouter:ApiKey"] = "sk-or-test",
            ["LlmProviders:openrouter:ModelPrefixes:0"] = "deepseek/",
            ["LlmProviders:openrouter:ModelPrefixes:1"] = "openai/",
        });
        registry.ResolveByModel(model)!.Key.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opus")]
    [InlineData("claude-sonnet-5")]
    public void ResolveByModel_РодныеМоделиClaude_Null(string? model)
    {
        Create().ResolveByModel(model).Should().BeNull();
        Create().ProviderKey(model).Should().Be("claude");
    }

    [Theory]
    // Тир-алиас + окно → базовый алиас (надёжен в любом окружении/аккаунте)
    [InlineData("opus[1m]", "opus")]
    [InlineData("OPUS[1M]", "opus")]
    [InlineData("sonnet[1m]", "sonnet")]
    [InlineData("haiku[1m]", "haiku")]
    // Базовые алиасы и обычные модели — без изменений
    [InlineData("opus", "opus")]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    // Полный id с окном и модель стороннего провайдера — НЕ трогаем
    [InlineData("claude-fable-5[1m]", "claude-fable-5[1m]")]
    [InlineData("glm-5.2[1m]", "glm-5.2[1m]")]
    public void StripClaudeWindowAlias_СводитТолькоТирАлиасы(string input, string expected)
    {
        LlmProviderRegistry.StripClaudeWindowAlias(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void StripClaudeWindowAlias_ПустоеБезИзменений(string? input)
    {
        LlmProviderRegistry.StripClaudeWindowAlias(input).Should().Be(input);
    }

    [Theory]
    // Базовые тир-алиасы с суффиксом окна — требуют проверки способности подписки (Supports1M)
    [InlineData("opus[1m]")]
    [InlineData("sonnet[1m]")]
    [InlineData("haiku[1m]")]
    [InlineData("OPUS[1M]")]
    // Не тир-алиасы: полные id и сторонние провайдеры разбирает сам CLI, без проверки пулом
    [InlineData("opus", false)]
    [InlineData("claude-fable-5[1m]", false)]
    [InlineData("glm-5.2[1m]", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsClaudeTierWindowAlias_ТолькоБазовыеТирАлиасыСОкном(string? input, bool expected = true)
    {
        LlmProviderRegistry.IsClaudeTierWindowAlias(input).Should().Be(expected);
    }

    [Fact]
    public void ResolveByModel_ВыключенныйПровайдер_ВсёРавноРезолвится()
    {
        // Иначе guard смены провайдера и сообщение «не настроен» не отличат GLM от Claude
        Create().ResolveByModel("glm-5.2")!.Key.Should().Be("glm");
    }

    [Fact]
    public void BuildCliEnv_Claude_Null()
    {
        Create().BuildCliEnv("sonnet").Should().BeNull();
    }

    [Fact]
    public void BuildCliEnv_DeepSeek_ПолныйНаборEnv()
    {
        var env = Create().BuildCliEnv("deepseek-v4-pro")!;
        env["ANTHROPIC_BASE_URL"].Should().Be("https://api.deepseek.com/anthropic");
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be("sk-test");
        env["ANTHROPIC_API_KEY"].Should().Be("sk-test");
        // Изоляция от OAuth-логина хоста: у каждого провайдера свой профиль CLI
        env["CLAUDE_CONFIG_DIR"].Should().EndWith(Path.Combine("claude-profiles", "deepseek"));
        env["ANTHROPIC_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"].Should().Be("deepseek-v4-flash");
        env["CLAUDE_CODE_SUBAGENT_MODEL"].Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void BuildCliEnv_ExtraEnv_Добавляется()
    {
        var env = Create(new() { ["LlmProviders:glm:ApiKey"] = "zai-key" }).BuildCliEnv("glm-5.2")!;
        env["API_TIMEOUT_MS"].Should().Be("3000000");
        // SmallModel не задан — haiku-слот получает основную модель
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"].Should().Be("glm-5.2");
    }

    // Средний слот разводит strong/medium у стороннего провайдера: алиас sonnet
    // (тир-пин персоны-сабагента) уходит в MediumModel, а не в модель сессии
    [Fact]
    public void BuildCliEnv_MediumModel_Задан_SonnetУходитВСреднюю()
    {
        var env = Create(new() { ["LlmProviders:deepseek:MediumModel"] = "deepseek-v4-flash" })
            .BuildCliEnv("deepseek-v4-pro")!;
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"].Should().Be("deepseek-v4-flash");
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"].Should().Be("deepseek-v4-flash");
        env["CLAUDE_CODE_SUBAGENT_MODEL"].Should().Be("deepseek-v4-flash");
    }

    // Окно контекста уходит в CLI явно: id сторонних моделей он не знает и без этого
    // держит сессию в 200k (ранний auto-compact при реальном окне до 1M)
    [Fact]
    public void BuildCliEnv_МодельИзКаталога_СтавитОкноКонтекста()
    {
        var env = Create(new() { ["LlmProviders:deepseek:Models:0:ContextWindow"] = "1048576" })
            .BuildCliEnv("deepseek-v4-pro")!;
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("1048576");
    }

    // Id с суффиксом окна (glm-5.2[1m], MiniMax-M3[1m]) — обычная запись каталога,
    // матчится как есть
    [Fact]
    public void BuildCliEnv_МодельССуффиксомОкна_СтавитОкноКонтекста()
    {
        var env = Create(new()
        {
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["LlmProviders:glm:Models:0:ContextWindow"] = "200000",
            ["LlmProviders:glm:Models:1:Id"] = "glm-5.2[1m]",
            ["LlmProviders:glm:Models:1:ContextWindow"] = "1048576",
        }).BuildCliEnv("glm-5.2[1m]")!;
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("1048576");
    }

    // Модель не из каталога (резолв по префиксу, окно неизвестно) — fail-open,
    // ключа нет и окно определяет сам CLI
    [Fact]
    public void BuildCliEnv_МодельНеИзКаталога_БезОкнаКонтекста()
    {
        var env = Create().BuildCliEnv("deepseek-reasoner-next")!;
        env["ANTHROPIC_MODEL"].Should().Be("deepseek-reasoner-next");
        env.Should().NotContainKey("CLAUDE_CODE_MAX_CONTEXT_TOKENS");
    }

    // ─── Окно контекста родного Claude (подписка) ────────────────────────────
    // Суффикс [1m] живёт только во флаге --model и внутрь сабагента не передаётся: без
    // явного объявления CLI ведёт сабагента в предполагаемых 200k, и обрывы жмутся к этой
    // границе. Значение считается по модели, которая РЕАЛЬНО уедет в --model.

    [Fact]
    public void ClaudeContextWindow_МодельССуффиксом_Окно1M()
    {
        LlmProviderRegistry.ClaudeContextWindow("opus[1m]").Should().Be(1_000_000);
        LlmProviderRegistry.ClaudeContextWindow("claude-opus-5[1m]").Should().Be(1_000_000);
        LlmProviderRegistry.ClaudeContextWindowValue("opus[1m]").Should().Be("1000000");
    }

    [Fact]
    public void ClaudeContextWindow_БезСуффикса_Штатные200k()
    {
        LlmProviderRegistry.ClaudeContextWindow("opus").Should().Be(200_000);
        LlmProviderRegistry.ClaudeContextWindow("claude-opus-5").Should().Be(200_000);
        // Модель не задана (слот пуст, решает CLI) — безопасное 200k
        LlmProviderRegistry.ClaudeContextWindow(null).Should().Be(200_000);
    }

    // Ключ уже в ProviderEnvKeys — значение с машины (мастер-рубильник, забытый setx)
    // вычищается на каждом запуске и не подменяет наше объявление
    [Fact]
    public void ProviderEnvKeys_СодержитОкноКонтекста()
    {
        LlmProviderRegistry.ProviderEnvKeys.Should().Contain("CLAUDE_CODE_MAX_CONTEXT_TOKENS");
        Create().EnvKeysToClear.Should().Contain("CLAUDE_CODE_MAX_CONTEXT_TOKENS");
    }

    [Fact]
    public void BuildCliEnv_ПровайдерБезКлюча_Исключение()
    {
        var act = () => Create().BuildCliEnv("glm-5.2");
        act.Should().Throw<InvalidOperationException>().WithMessage("*не настроен*");
    }

    [Fact]
    public void BuildCliEnv_СинкОбщихНастроекВПрофиль_БезКреденшалов()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        Directory.CreateDirectory(Path.Combine(userDir, "rules"));
        File.WriteAllText(Path.Combine(userDir, "CLAUDE.md"), "# память");
        File.WriteAllText(Path.Combine(userDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(userDir, "rules", "style.md"), "правила");
        // Креденшалы и транскрипты копироваться НЕ должны
        File.WriteAllText(Path.Combine(userDir, ".credentials.json"), "{\"oauth\":\"секрет\"}");

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            var profile = reg.BuildCliEnv("deepseek-v4-pro")!["CLAUDE_CONFIG_DIR"];

            File.Exists(Path.Combine(profile, "CLAUDE.md")).Should().BeTrue();
            File.Exists(Path.Combine(profile, "settings.json")).Should().BeTrue();
            File.Exists(Path.Combine(profile, "rules", "style.md")).Should().BeTrue();
            File.Exists(Path.Combine(profile, ".credentials.json")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // settings.json профиля мержится по ключам, а не копируется файлом: в нём живут env
    // маршрута провайдера, permissions.allow и enabledPlugins, которые File.Copy стирал
    [Fact]
    public void СинкSettingsJson_МержПоКлючам_ПрофильныеКлючиВыживают()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        var profileDir = Path.Combine(tmp, "data", "claude-profiles", "deepseek");
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(profileDir);

        var profilePath = Path.Combine(profileDir, "settings.json");
        File.WriteAllText(profilePath, """
        {
          "env": {
            "ANTHROPIC_BASE_URL": "https://api.deepseek.com/anthropic",
            "ANTHROPIC_AUTH_TOKEN": "sk-профиль"
          },
          "permissions": { "allow": ["Bash(git:*)"] },
          "enabledPlugins": { "playwright@claude-plugins-official": true },
          "model": "deepseek-v4-pro"
        }
        """);

        var hostPath = Path.Combine(userDir, "settings.json");
        File.WriteAllText(hostPath, """
        {
          "env": {
            "ANTHROPIC_BASE_URL": "https://api.anthropic.com",
            "CLAUDE_CODE_MAX_OUTPUT_TOKENS": "8192"
          },
          "permissions": { "deny": ["Read(./secrets/**)"] },
          "model": "opus",
          "cleanupPeriodDays": 30
        }
        """);
        // Источник заведомо новее приёмника — иначе синк пропустит файл по mtime
        File.SetLastWriteTimeUtc(profilePath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(hostPath, DateTime.UtcNow);

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            reg.BuildCliEnv("deepseek-v4-pro");

            var merged = JsonNode.Parse(File.ReadAllText(profilePath))!;

            // env: профильное значение сильнее хостового (оно задаёт маршрут CLI),
            // но хостовые ключи, которых в профиле нет, добавляются
            merged["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>()
                .Should().Be("https://api.deepseek.com/anthropic");
            merged["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>().Should().Be("sk-профиль");
            merged["env"]!["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]!.GetValue<string>().Should().Be("8192");

            // permissions и enabledPlugins профиля не потеряны
            merged["permissions"]!["allow"]!.AsArray()[0]!.GetValue<string>().Should().Be("Bash(git:*)");
            merged["permissions"]!["deny"]!.AsArray()[0]!.GetValue<string>().Should().Be("Read(./secrets/**)");
            merged["enabledPlugins"]!["playwright@claude-plugins-official"]!.GetValue<bool>()
                .Should().BeTrue();

            // остальные ключи — хостовые сильнее, новые добавляются
            merged["model"]!.GetValue<string>().Should().Be("opus");
            merged["cleanupPeriodDays"]!.GetValue<int>().Should().Be(30);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void СинкSettingsJson_ПрофильБезФайла_ПолучаетХостовый()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "settings.json"), """{ "model": "opus" }""");

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            var profile = reg.BuildCliEnv("deepseek-v4-pro")!["CLAUDE_CONFIG_DIR"];

            var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "settings.json")))!;
            merged["model"]!.GetValue<string>().Should().Be("opus");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // Встроенные механики едут в профиль из поставки приложения и перебивают копию с хоста:
    // хостовый ~/.claude/workflows правится вручную и однажды приехал перекодированным —
    // CLI отбивал такой скрипт по управляющим символам, и «Командный спринт» не стартовал
    [Fact]
    public void СидингМеханик_ПоставкаПеребиваетКопиюСХоста()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        Directory.CreateDirectory(Path.Combine(userDir, "workflows"));

        // Имя уникально: каталог поставки общий на сборку, параллельные тесты не должны спорить
        var name = "тест-механика-" + Guid.NewGuid().ToString("N") + ".js";
        var defaultsDir = Path.Combine(AppContext.BaseDirectory, "claude-defaults", "workflows");
        Directory.CreateDirectory(defaultsDir);
        var shipped = Path.Combine(defaultsDir, name);
        File.WriteAllText(shipped, "export const meta = { name: 'спринт', description: 'разбить и раздать' }");
        // На хосте — та же механика, но испорченная (в боевой поломке — перекодировка мимо UTF-8)
        File.WriteAllText(Path.Combine(userDir, "workflows", name), "export const meta = { name: 'битаякопия' }");

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            var profile = reg.BuildCliEnv("deepseek-v4-pro")!["CLAUDE_CONFIG_DIR"];

            var seeded = File.ReadAllText(Path.Combine(profile, "workflows", name));
            seeded.Should().Be(File.ReadAllText(shipped));
            seeded.Should().NotContain("битаякопия");
        }
        finally
        {
            try { File.Delete(shipped); } catch { }
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // Установленные плагины включаются профилю сервером: без этого скиллы oh-my-claudecode
    // отвечали «Unknown command» (механики «Автопилот», «QA-цикл», «Трассировка»…),
    // а осознанно выключенный плагин обязан таким и остаться
    [Fact]
    public void ВключениеПлагинов_УстановленныеВключаются_ЯвноВыключенныйОстаётся()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        var profileDir = Path.Combine(tmp, "data", "claude-profiles", "deepseek");
        Directory.CreateDirectory(Path.Combine(userDir, "plugins"));
        Directory.CreateDirectory(profileDir);

        File.WriteAllText(Path.Combine(userDir, "plugins", "installed_plugins.json"), """
        {
          "version": 2,
          "plugins": {
            "oh-my-claudecode@omc": [{ "scope": "user" }],
            "playwright@claude-plugins-official": [{ "scope": "user" }]
          }
        }
        """);
        File.WriteAllText(Path.Combine(profileDir, "settings.json"), """
        { "enabledPlugins": { "playwright@claude-plugins-official": false } }
        """);

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            reg.BuildCliEnv("deepseek-v4-pro");

            var enabled = JsonNode.Parse(File.ReadAllText(Path.Combine(profileDir, "settings.json")))!["enabledPlugins"]!;
            enabled["oh-my-claudecode@omc"]!.GetValue<bool>().Should().BeTrue();
            enabled["playwright@claude-plugins-official"]!.GetValue<bool>().Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ComputeCost_ПоЦенамКонфига()
    {
        // 1M miss-входа + 1M hit-кэша + 1M выхода = 0.5 + 0.1 + 1.0
        var usage = new UsageInfo(1_000_000, 1_000_000, 1_000_000, 0);
        Create().ComputeCost("deepseek-v4-pro", usage).Should().BeApproximately(1.6, 0.0001);
    }

    [Fact]
    public void ComputeCost_БезЦен_Null()
    {
        var usage = new UsageInfo(1000, 1000, 0, 0);
        Create(new() { ["LlmProviders:glm:ApiKey"] = "zai-key" })
            .ComputeCost("glm-5.2", usage).Should().BeNull();
    }

    [Fact]
    public void CapabilitiesFor_ИзКонфига()
    {
        var caps = Create().CapabilitiesFor("deepseek-v4-pro");
        caps.Provider.Should().Be("deepseek");
        caps.DisplayName.Should().Be("DeepSeek");
        caps.SupportsImages.Should().BeFalse();
        caps.SupportsPlanMode.Should().BeTrue();
        caps.SupportsCompact.Should().BeTrue();
    }

    // Резолв дефолтной модели для spend-аналитики: SpendRecord.Model никогда не должен
    // оставаться пустым — иначе в группировке копилась «Модель по умолчанию».
    [Theory]
    [InlineData(null, "claude", "default")]      // подписка, CLI не отдал modelUsage
    [InlineData("", "claude", "default")]        // то же для пустой строки
    [InlineData("   ", "claude", "default")]     // и пробельных
    [InlineData(null, null, "default")]          // провайдер неизвестен — тоже дефолт Claude
    [InlineData(null, "", "default")]
    [InlineData(null, "deepseek", "deepseek-v4-pro")] // сторонний → первая модель каталога
    [InlineData("opus", "claude", "opus")]        // явная модель не пересчитывается
    [InlineData("glm-5.2", "glm", "glm-5.2")]
    public void ResolveModelOrDefault_ПустаяРезолвитсяВДефолт(string? model, string? provider, string expected)
    {
        Create().ResolveModelOrDefault(model, provider).Should().Be(expected);
    }

    [Fact]
    public void ResolveModelOrDefault_ОбрезаетПробелы()
    {
        Create().ResolveModelOrDefault("  opus  ", "claude").Should().Be("opus");
    }

    [Fact]
    public void ResolveModelOrDefault_ДефолтClaude_СовпадаетСАлиасомКаталога()
    {
        // Маркер дефолта стабилен и совпадает с алиасом "default" из ClaudeCatalog
        LlmProviderRegistry.DefaultClaudeModel.Should().Be("default");
    }
}
