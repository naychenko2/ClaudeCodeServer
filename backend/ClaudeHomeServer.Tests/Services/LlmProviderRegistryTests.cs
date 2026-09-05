using System.Text.Json.Nodes;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Models;
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

    // ─── ADR-014 / docs/research/local-vllm-provider.md: локальный провайдер ────────────

    // Хелпер: конфиг с локальным провайдером vLLM. Провайдер задаётся ТОЛЬКО этими полями;
    // при изменении состава секции (добавление полей, переименование) правки придётся
    // протащить и сюда.
    private static LlmProviderRegistry CreateLocal(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:local-qwen:DisplayName"] = "Локальная Qwen3.8-27B",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:8080",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:SupportedEfforts:0"] = "low",
            ["LlmProviders:local-qwen:SupportedEfforts:1"] = "medium",
            ["LlmProviders:local-qwen:SupportedEfforts:2"] = "xhigh",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen38-27b",
            ["LlmProviders:local-qwen:Models:0:DisplayName"] = "Qwen3.8-27B (локальная)",
            ["LlmProviders:local-qwen:Models:0:ContextWindow"] = "57344",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var config = TestConfig.Build(settings);
        return new LlmProviderRegistry(config);
    }

    [Fact]
    public void Enabled_ЛокальныйБезApiKey_Включен()
    {
        CreateLocal().GetByKey("local-qwen")!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_БезIsLocalИБезApiKey_Выключен()
    {
        // Защита от регресса: glm без ключа и без IsLocal — НЕ enabled.
        // Это и есть причина, по которой признак IsLocal был введён.
        Create().GetByKey("glm")!.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Enabled_ЛокальныйБезБазовогоАдреса_Выключен()
    {
        // AnthropicBaseUrl пуст — нет точки входа, провайдер не рабочий
        var reg = CreateLocal();
        reg.GetByKey("local-qwen")!.AnthropicBaseUrl = "";
        reg.GetByKey("local-qwen")!.Enabled.Should().BeFalse();
    }

    [Fact]
    public void BuildCliEnv_Локальный_СтавитБазовыйАдресИОкноКонтекста()
    {
        var env = CreateLocal().BuildCliEnv("qwen38-27b")!;
        env["ANTHROPIC_BASE_URL"].Should().Be("http://127.0.0.1:8080");
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("57344");
        // Заглушка для локального провайдера: пустая строка отбивается CLI как «Not logged in»
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be(LlmProviderRegistry.LocalNoAuthToken);
        env["ANTHROPIC_API_KEY"].Should().Be(LlmProviderRegistry.LocalNoAuthToken);
    }

    // Явный ApiKey у локального провайдера (напр. прокси с реальной авторизацией) — не
    // перетираем заглушкой. Без этого ход уехал бы на локальный эндпоинт с фиктивным токеном
    [Fact]
    public void BuildCliEnv_ЛокальныйСApiKey_НеПеретираетТокен()
    {
        var reg = CreateLocal(new() { ["LlmProviders:local-qwen:ApiKey"] = "real-proxy-token" });
        var env = reg.BuildCliEnv("qwen38-27b")!;
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be("real-proxy-token");
        env["ANTHROPIC_API_KEY"].Should().Be("real-proxy-token");
    }

    // У не-локального провайдера пустой ключ означает «не настроен» — BuildCliEnv кидает
    // исключение раньше, чем доходит до env-сборки. Защита от случайной подмены заглушкой
    [Fact]
    public void BuildCliEnv_НеЛокальныйБезApiKey_ИсключениеНеЗаглушка()
    {
        var act = () => Create().BuildCliEnv("glm-5.2");
        act.Should().Throw<InvalidOperationException>().WithMessage("*не настроен*");
    }

    [Fact]
    public void ResolveByModel_qwen_ВозвращаетЛокальныйПровайдер()
    {
        CreateLocal().ResolveByModel("qwen38-27b")!.Key.Should().Be("local-qwen");
    }

    [Theory]
    // Поддерживаемые уровни не трогаем
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("xhigh", "xhigh")]
    // Незнакомый уровень — ближайший снизу по шкале low<medium<high<xhigh<max.
    // high не поддерживается провайдером, ближайший снизу — medium.
    // max не поддерживается, ближайший снизу — xhigh.
    [InlineData("high", "medium")]
    [InlineData("max", "xhigh")]
    public void EffortFor_Локальный_ПодменяетПоСписку(string input, string expected)
    {
        CreateLocal().EffortFor("qwen38-27b", input).Should().Be(expected);
    }

    /// <summary>
    /// Контракт, на который опирается боевая секция local-qwen: SupportedEfforts=["low"] —
    /// единственный уровень, и правило «ближайший снизу» схлопывает в него ЛЮБОЙ запрошенный.
    /// Ради этого у провайдера сознательно НЕ заводится EffortMap: карта была бы второй точкой
    /// правды об одном и том же (§7б local-vllm-provider.md, грабля 2 — сервер отвечает 400 на
    /// high, а на medium/xhigh модель размышляет минутами).
    /// </summary>
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void EffortFor_ЕдинственныйУровеньLow_СхлопываетЛюбойЗапрос(string input)
    {
        var reg = CreateLocal(new()
        {
            // Перечисление заменяем целиком: конфигурация тестов складывается из ключей,
            // и лишние индексы SupportedEfforts:1/2 иначе остались бы от хелпера
            ["LlmProviders:local-qwen:SupportedEfforts:1"] = null,
            ["LlmProviders:local-qwen:SupportedEfforts:2"] = null,
        });
        reg.EffortFor("qwen38-27b", input).Should().Be("low");
    }

    [Theory]
    // Совершенно незнакомый CLI уровень (CLI заведёт новый): ближайший снизу — самый
    // лёгкий поддерживаемый (low), иначе уедет как есть и вернёт 400.
    [InlineData("minimal")]
    [InlineData("super")]
    public void EffortFor_НезнакомыйУровень_СамыйЛёгкийПоддерживаемый(string input)
    {
        CreateLocal().EffortFor("qwen38-27b", input).Should().Be("low");
    }

    [Theory]
    // Защита от регресса: у glm/kimi/minimax пустой SupportedEfforts — поведение не меняется
    [InlineData("deepseek-v4-pro", "high", "high")]
    [InlineData("glm-5.2", "high", "high")]
    [InlineData("kimi-k3", "max", "max")]
    public void EffortFor_ПровайдерБезСписка_НеТрогает(string model, string effort, string expected)
    {
        var reg = Create(new()
        {
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["LlmProviders:kimi:ApiKey"] = "kimi-key",
            ["LlmProviders:kimi:Models:0:Id"] = "kimi-k3",
        });
        reg.EffortFor(model, effort).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("opus")]
    [InlineData("claude-opus-4-8")]
    [InlineData("sonnet[1m]")]
    public void EffortFor_РоднойClaude_НеТрогает(string? model)
    {
        Create().EffortFor(model, "high").Should().Be("high");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EffortFor_ПустойEffort_ЛокальныйВозвращаетСамыйЛёгкий(string? effort)
    {
        // qwen3.8-27b имеет SupportedEfforts = [low, medium, xhigh]. Пустой effort — не
        // «оставь как есть» (иначе CLI подставит «high» → 400 на vLLM), а «подбери
        // самый лёгкий поддерживаемый» — здесь «low».
        CreateLocal().EffortFor("qwen38-27b", effort).Should().Be("low");
    }

    // Локальный провайдер с единственным уровнем «medium» — пустой effort даёт «medium».
    // Контракт «самый лёгкий из SupportedEfforts», не «первый элемент списка».
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EffortFor_ПустойEffort_ЛокальныйТолькоMedium(string? effort)
    {
        var reg = CreateLocal(new()
        {
            ["LlmProviders:local-qwen:SupportedEfforts:0"] = "medium",
            ["LlmProviders:local-qwen:SupportedEfforts:1"] = null,
            ["LlmProviders:local-qwen:SupportedEfforts:2"] = null,
        });
        reg.EffortFor("qwen38-27b", effort).Should().Be("medium");
    }

    // Провайдер без SupportedEfforts (glm/kimi/minimax по §7б) — пустой effort возвращает
    // null, флаг --effort НЕ ставится. Это fail-open: пусть CLI берёт свой дефолт, как
    // было до подмены.
    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("glm-5.2")]
    [InlineData("kimi-k3")]
    public void EffortFor_ПустойEffort_ПровайдерБезСписка_Null(string model)
    {
        var reg = Create(new()
        {
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["LlmProviders:kimi:ApiKey"] = "kimi-key",
            ["LlmProviders:kimi:Models:0:Id"] = "kimi-k3",
        });
        reg.EffortFor(model, null).Should().BeNull();
        reg.EffortFor(model, "").Should().BeNull();
        reg.EffortFor(model, "   ").Should().BeNull();
    }

    // Родной Claude (модель не резолвится ни в какого провайдера) — пустой effort даёт null.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opus")]
    [InlineData("claude-opus-4-8")]
    [InlineData("sonnet[1m]")]
    public void EffortFor_ПустойEffort_РоднойClaude_Null(string? model)
    {
        Create().EffortFor(model, null).Should().BeNull();
    }

    // EffortMap приоритетнее SupportedEfforts. Конфиг декларативнее правила «ближайший снизу»:
    // «high → medium» явно видно при разборе инцидентов и в git diff конфига.
    [Theory]
    [InlineData("high", "medium")]
    [InlineData("max", "xhigh")]
    public void EffortFor_ЯвныйEffortMap_ПриоритетнееАлгоритма(string input, string expected)
    {
        // Без карты: high через SupportedEfforts [low,medium,xhigh] дал бы medium через
        // «ближайший снизу» — этот тест проверяет, что EffortMap даёт тот же результат
        // ЯВНО, без вычислений. Для max карта даёт xhigh напрямую, а алгоритм дал бы тот же
        // xhigh через «ближайший снизу» — тут проверка, что карта не подменяется алгоритмом.
        var reg = CreateLocal(new()
        {
            ["LlmProviders:local-qwen:EffortMap:high"] = "medium",
            ["LlmProviders:local-qwen:EffortMap:max"] = "xhigh",
        });
        reg.EffortFor("qwen38-27b", input).Should().Be(expected);
    }

    // EffortMap в карте есть ключ, но в карте нет значения (пустая строка) — откатываемся на алгоритм.
    [Theory]
    [InlineData("high", "medium")]  // high в SupportedEfforts нет → ближайший снизу = medium
    public void EffortFor_ПустоеЗначениеВКарте_ОткатНаАлгоритм(string input, string expected)
    {
        var reg = CreateLocal(new()
        {
            ["LlmProviders:local-qwen:EffortMap:high"] = "",  // пустое значение — fail-open
        });
        reg.EffortFor("qwen38-27b", input).Should().Be(expected);
    }

    [Fact]
    public void BuildCliEnv_Локальный_ПрокидываетMaxThinkingTokens()
    {
        var env = CreateLocal(new()
        {
            ["LlmProviders:local-qwen:MaxThinkingTokens"] = "5000",
        }).BuildCliEnv("qwen38-27b")!;
        env["MAX_THINKING_TOKENS"].Should().Be("5000");
        // Сторож имени: CLAUDE_CODE_MAX_THINKING_TOKENS в бинарнике CLI не существует (0 вхождений
        // в claude.exe 2.1.241) — такой ключ молча ничего не делал бы.
        env.Keys.Should().NotContain("CLAUDE_CODE_MAX_THINKING_TOKENS");
    }

    [Fact]
    public void BuildCliEnv_БезMaxThinkingTokens_НеСтавитКлюч()
    {
        // null/0/нет-поля — ключ НЕ ставится (fail-open для остальных провайдеров:
        // glm/kimi/minimax оставляют дефолт CLI — не лезем в их поведение).
        CreateLocal().BuildCliEnv("qwen38-27b")!.Keys.Should().NotContain("MAX_THINKING_TOKENS");
    }

    [Fact]
    public void BuildCliEnv_MaxThinkingTokensНоль_НеСтавитКлюч()
    {
        var env = CreateLocal(new()
        {
            ["LlmProviders:local-qwen:MaxThinkingTokens"] = "0",
        }).BuildCliEnv("qwen38-27b")!;
        env.Keys.Should().NotContain("MAX_THINKING_TOKENS");
    }

    [Fact]
    public void ProviderEnvKeys_ВключаетMaxThinkingTokens()
    {
        // Реестр должен вычищать MAX_THINKING_TOKENS из унаследованного env на
        // КАЖДОМ запуске CLI — иначе мастер-рубильник на машине уедет на наш эндпоинт.
        LlmProviderRegistry.ProviderEnvKeys.Should().Contain("MAX_THINKING_TOKENS");
    }

    [Fact]
    public void ComputeCost_qwenБезЦен_Null()
    {
        // Цены не заданы — расход нулевой, в отчётах «Использования» строка не появляется
        var reg = CreateLocal();
        var usage = new UsageInfo(1000, 0, 1000, 0);
        reg.ComputeCost("qwen38-27b", usage).Should().BeNull();
    }

    [Fact]
    public void ComputeCost_qwen_НеТащитOpenRouter()
    {
        // Защита от регресса при добавлении qwen-моделей в OpenRouter: резолв по каталогу
        // должен брать локального провайдера, а не облачного агрегатора
        var reg = CreateLocal(new Dictionary<string, string?>
        {
            ["LlmProviders:openrouter:DisplayName"] = "OpenRouter",
            ["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api",
            ["LlmProviders:openrouter:ApiKey"] = "sk-or",
            ["LlmProviders:openrouter:Models:0:Id"] = "qwen38-27b",
        });
        reg.ResolveByModel("qwen38-27b")!.Key.Should().Be("local-qwen");
    }

    // ─── ADR-015 §3: манифест доставки .sync-manifest.json ──────────────────────

    // Хелпер: поднимает временный layout (host-профиль + профиль провайдера) и
    // возвращает корни для теста. Усыновление/расчёт выполняются поверх.
    private static (string tmp, string userDir, string profileDir, string defaultsDir) CreateSyncLayout(
        Dictionary<string, string?>? extra = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        var profileDir = Path.Combine(tmp, "data", "claude-profiles", "deepseek");
        var defaultsDir = Path.Combine(tmp, "defaults");
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(defaultsDir);
        return (tmp, userDir, profileDir, defaultsDir);
    }

    private static LlmProviderRegistry CreateRegistry(
        string userDir, string profileDir, string defaultsDir,
        ProfileMirrorMode mode, Dictionary<string, string?>? extra = null)
    {
        // profileDir вида tmp/data/claude-profiles/deepseek. DataPath указываем на
        // tmp/data/projects.json — Path.GetDirectoryName отдаст tmp/data, и
        // _profilesDir соберётся как tmp/data/claude-profiles (та же папка, в которой
        // лежит наш deepseek). Через GetFullPath нормализуем «..», иначе Directory.Exists
        // по неразвёрнутому пути возвращает false на свежих .NET.
        var dataPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(profileDir)!, "..", "projects.json"));
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceInHitPer1M"] = "0.1",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            ["ClaudeUserProfileDir"] = userDir,
            ["DataPath"] = dataPath,
            ["Claude:DefaultsRoot"] = defaultsDir,
            ["Claude:ProfileSync:Mirror"] = mode.ToString(),
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new LlmProviderRegistry(TestConfig.Build(settings));
    }

    [Fact]
    public void Синк_ЗаписываетМанифест_ДляКаждогоДоставленногоФайла()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            File.WriteAllText(Path.Combine(userDir, "CLAUDE.md"), "# host memory");
            Directory.CreateDirectory(Path.Combine(userDir, "rules"));
            File.WriteAllText(Path.Combine(userDir, "rules", "style.md"), "правила стиля");

            var reg = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.Off);
            reg.BuildCliEnv("deepseek-v4-pro");

            // .sync-manifest.json создан и содержит обе записи с source=host
            var manifestPath = Path.Combine(profileDir, ".sync-manifest.json");
            File.Exists(manifestPath).Should().BeTrue();

            var json = File.ReadAllText(manifestPath);
            json.Should().Contain("\"CLAUDE.md\"");
            json.Should().Contain("\"rules/style.md\"");
            json.Should().Contain("\"source\": \"host\"");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_ИзменённыйФайлВПрофиле_НеПопадаетВWouldDelete_НоВDivergent()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            File.WriteAllText(Path.Combine(userDir, "CLAUDE.md"), "# host");
            // Делаем host-файл старым, чтобы первый BuildCliEnv скопировал его в профиль
            File.SetLastWriteTimeUtc(Path.Combine(userDir, "CLAUDE.md"), DateTime.UtcNow.AddMinutes(-10));

            var reg = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.Off);
            reg.BuildCliEnv("deepseek-v4-pro");

            // Сценарий «расхождение»: host-источник удалён (mirror кандидат), но в профиле
            // файл изменился — значит его правил человек (не синк). Условие 3 нарушено:
            // профильный size/mtime больше не совпадают с манифестом → divergent, не удалять.
            File.Delete(Path.Combine(userDir, "CLAUDE.md"));
            var dst = Path.Combine(profileDir, "CLAUDE.md");
            File.WriteAllText(dst, "# ручная правка в профиле");
            File.SetLastWriteTimeUtc(dst, DateTime.UtcNow.AddDays(1));

            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            var reports = regDry.NormalizeAll();
            var report = reports.Single();
            // Регистр ключа в манифесте зависит от ОС (на Windows Path.GetRelativePath
            // иногда возвращает смешанный регистр: «claudE.md»). Сам манифест сравнивает
            // case-insensitively — поэтому и ассерт case-insensitive.
            report.WouldDelete.Should().NotContain(p => string.Equals(p, "CLAUDE.md", StringComparison.OrdinalIgnoreCase));
            report.Divergent.Should().Contain(p => string.Equals(p, "CLAUDE.md", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_ПутьНеВМанифесте_НеПопадаетВWouldDelete()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // В профиле лежит файл, но синк его туда не клал — в манифесте его нет.
            // Если бы мы считали весь профиль, попало бы в WouldDelete «удалил бы».
            Directory.CreateDirectory(Path.Combine(profileDir, "commands"));
            File.WriteAllText(Path.Combine(profileDir, "commands", "extra.md"), "ручной файл");

            // Без host-источника файлы commands не появятся в манифесте, потому что
            // CopyIfNewer копирует только новее. После NormalizeAll — манифест остаётся
            // пустым (усыновление подберёт только то, что лежит, без решений об удалении).
            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            var reports = regDry.NormalizeAll();
            // Усыновление занесло extra.md в манифест как host — тогда условие 1 выполнено.
            // Условие 2: в источнике (host/commands/extra.md) файла нет. Условие 3: размер/mtime
            // совпадают (только что записаны). Ожидаемо попадёт в WouldDelete.
            // Это правильное поведение mirror — ручной файл, не принесённый синком, удалится.
            // Но: задача говорит «путь, которого нет в манифесте, не попадает тоже».
            // Чтобы воспроизвести случай «нет в манифесте», нужна ситуация, когда мы
            // запустили NormalizeAll БЕЗ усыновления — то есть манифест уже существовал.
            // Это сценарий бэкапа: манифест восстановлен, в нём только то, что синк приносил,
            // а в профиле лежит мусор — он НЕ должен попасть в WouldDelete.
            // Проверка: extra.md НЕ в WouldDelete, потому что его нет в манифесте.
            // Дополнительно: ручной файл commands/extra.md лежит в профиле, манифест из усыновления
            // его ЗАНЁС — это поведение зеркала по условиям ADR-015 §3: «путь в манифесте».
            // Для теста нужна другая конфигурация — пустой манифест, ручной файл, источник пуст.
            // Симулируем: положим манифест руками (только запись CLAUDE.md), commands/extra.md
            // — лишнее, в WouldDelete не должно попасть.
            File.Delete(Path.Combine(profileDir, ".sync-manifest.json"));
            var manifest = new ProfileSyncManifest
            {
                Files = new Dictionary<string, ProfileSyncEntry>
                {
                    ["CLAUDE.md"] = new() { Zone = "CLAUDE.md", Source = "host", Size = 5, Mtime = DateTime.UtcNow },
                },
            };
            // camelCase — формат ProfileSyncManifestStore; иначе LoadOrEmpty не прочтёт
            // Files обратно (PascalCase) и следующий NormalizeAll сделает усыновление заново.
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            };
            File.WriteAllText(Path.Combine(profileDir, ".sync-manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(manifest, opts));

            var reports2 = regDry.NormalizeAll();
            reports2.Single().WouldDelete.Should().NotContain("commands/extra.md");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_FailSafe_БезDefaultsЗона_ИсключаетсяИзРасчёта()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // defaults каталог пуст (без workflows) — зеркало workflows должно быть выключено.
            // В профиле есть workflow-файл, которого нет в host — кандидат на удаление
            // через нормальный mirror, но с fail-safe должен быть исключён.
            Directory.CreateDirectory(Path.Combine(profileDir, "workflows"));
            File.WriteAllText(Path.Combine(profileDir, "workflows", "phantom.js"), "// нет в host");

            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            var reports = regDry.NormalizeAll();
            var report = reports.Single();

            // defaults/workflows нет → зона workflows в SkippedZones
            report.SkippedZones.Should().Contain("workflows");
            // phantom.js НЕ попал в WouldDelete (fail-safe отключил всю зону)
            report.WouldDelete.Should().NotContain("workflows/phantom.js");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_SkillsНеСчитаются_ДажеЕслиИхНетВПоставке()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // В профиле лежит skill, которого нет ни в host, ни в defaults.
            // По задаче (§9.1 ADR открыт) skills/ не участвует в расчёте.
            Directory.CreateDirectory(Path.Combine(profileDir, "skills", "ghost"));
            File.WriteAllText(Path.Combine(profileDir, "skills", "ghost", "SKILL.md"), "--");

            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            var reports = regDry.NormalizeAll();
            var report = reports.Single();

            report.WouldDelete.Should().NotContain(s => s.StartsWith("skills/"));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_DefaultsФайлыНеУдаляются()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // Поставка содержит panel-of-experts.js — кладётся в профиль через SeedDefaultWorkflows.
            Directory.CreateDirectory(Path.Combine(defaultsDir, "workflows"));
            File.WriteAllText(Path.Combine(defaultsDir, "workflows", "panel.js"), "// shipped");

            // Запускаем SyncUserProfile через BuildCliEnv — сидер запишет defaults в манифест.
            var regOff = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.Off);
            regOff.BuildCliEnv("deepseek-v4-pro");

            // Проверяем, что файл из поставки остался в манифесте с source=defaults
            // и НЕ попадает в WouldDelete даже в dryRun.
            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            var reports = regDry.NormalizeAll();
            var report = reports.Single();

            File.Exists(Path.Combine(profileDir, "workflows", "panel.js")).Should().BeTrue();
            report.WouldDelete.Should().NotContain("workflows/panel.js");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NormalizeAll_УсыновлениеЗаполняетПустойМанифест()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // В профиле уже лежат файлы в mirror-зонах (накопились за время жизни).
            Directory.CreateDirectory(Path.Combine(profileDir, "rules"));
            File.WriteAllText(Path.Combine(profileDir, "CLAUDE.md"), "# прошлый со");
            File.WriteAllText(Path.Combine(profileDir, "rules", "old.md"), "старое правило");

            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            regDry.NormalizeAll();

            var manifestPath = Path.Combine(profileDir, ".sync-manifest.json");
            File.Exists(manifestPath).Should().BeTrue();
            var json = File.ReadAllText(manifestPath);
            json.Should().Contain("\"CLAUDE.md\"");
            json.Should().Contain("\"rules/old.md\"");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void On_УдаляетФайлы_ПрошедшиеВсеТриУсловия()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            // В профиле rules/extra.md, в host-source нет — кандидат на удаление.
            Directory.CreateDirectory(Path.Combine(profileDir, "rules"));
            File.WriteAllText(Path.Combine(profileDir, "rules", "extra.md"), "лишнее");

            var regOn = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.On);
            regOn.NormalizeAll();

            // В режиме on лишний файл удалён
            File.Exists(Path.Combine(profileDir, "rules", "extra.md")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DryRun_НичегоНеУдаляет()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            Directory.CreateDirectory(Path.Combine(profileDir, "rules"));
            File.WriteAllText(Path.Combine(profileDir, "rules", "extra.md"), "лишнее");

            var regDry = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.DryRun);
            regDry.NormalizeAll();

            // Файл должен остаться — dryRun ничего не удаляет
            File.Exists(Path.Combine(profileDir, "rules", "extra.md")).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Синк_DefaultsФайлы_ЗаписываютсяВМанифестССоответствующимSource()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            Directory.CreateDirectory(Path.Combine(defaultsDir, "workflows"));
            File.WriteAllText(Path.Combine(defaultsDir, "workflows", "panel.js"), "// shipped");

            var regOff = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.Off);
            regOff.BuildCliEnv("deepseek-v4-pro");

            var manifestPath = Path.Combine(profileDir, ".sync-manifest.json");
            var json = File.ReadAllText(manifestPath);
            // source=defaults у этой записи — иначе mirror-расчёт считал бы её кандидатом на удаление
            json.Should().Contain("\"workflows/panel.js\"");
            json.Should().Contain("\"source\": \"defaults\"");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ─── ADR-015 §5.3: корзина удалённого (SyncTrashStore) ─────────────────

    [Fact]
    public void On_УдаляемыйФайл_ПереноситсяВКорзинуССохранениемОтносительногоПути()
    {
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            Directory.CreateDirectory(Path.Combine(profileDir, "rules"));
            File.WriteAllText(Path.Combine(profileDir, "rules", "extra.md"), "лишнее");

            var regOn = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.On);
            var reports = regOn.NormalizeAll();
            var report = reports.Single();

            // Файл удалён из профиля
            File.Exists(Path.Combine(profileDir, "rules", "extra.md")).Should().BeFalse();
            // В отчёте — в Trashed
            report.Trashed.Should().Contain("rules/extra.md");

            // Корзина лежит рядом с data/, не в claude-profiles (ADR-015 §5.3):
            // вложенная папка сама была бы принята за профиль при обходе.
            var dataDir = Path.Combine(tmp, "data");
            var profileName = Path.GetFileName(profileDir);
            var trashRoot = Path.Combine(dataDir, SyncTrashStore.RootDirName, profileName);
            Directory.Exists(trashRoot).Should().BeTrue();
            var stampDirs = Directory.GetDirectories(trashRoot);
            stampDirs.Length.Should().Be(1);
            // Отметка прохода — UTC, формат без ':' (Windows не разрешает в имени файла)
            SyncTrashStore.TryParseStamp(Path.GetFileName(stampDirs[0]), out var stamp).Should().BeTrue();
            // Структура каталогов внутри отметки сохранена (rules/extra.md)
            File.Exists(Path.Combine(stampDirs[0], "rules", "extra.md")).Should().BeTrue();
            File.ReadAllText(Path.Combine(stampDirs[0], "rules", "extra.md")).Should().Be("лишнее");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void On_СбойПереносаВКорзину_ФайлОстаётсяВПрофиле()
    {
        // Блокируем создание .sync-trash файлом с тем же именем — Directory.CreateDirectory
        // на этом упадёт (на Windows и Linux: имя занято объектом файловой системы другого типа).
        var (tmp, userDir, profileDir, defaultsDir) = CreateSyncLayout();
        try
        {
            var dataDir = Path.Combine(tmp, "data");
            File.WriteAllText(Path.Combine(dataDir, SyncTrashStore.RootDirName), "blocker");

            Directory.CreateDirectory(Path.Combine(profileDir, "rules"));
            File.WriteAllText(Path.Combine(profileDir, "rules", "extra.md"), "лишнее");

            var regOn = CreateRegistry(userDir, profileDir, defaultsDir, ProfileMirrorMode.On);
            var reports = regOn.NormalizeAll();
            var report = reports.Single();

            // Кандидат в WouldDelete есть, но перенос не состоялся → файл остаётся в профиле
            report.WouldDelete.Should().Contain("rules/extra.md");
            report.Trashed.Should().NotContain("rules/extra.md");
            File.Exists(Path.Combine(profileDir, "rules", "extra.md")).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SyncTrashStore_PurgeOld_ЧиститТолькоСтарыеОтметки()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pts_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SyncTrashStore(tmp);
            var now = DateTime.UtcNow;
            // Три прохода: старый (20 дней назад), недавний (1 день), только что
            var stamps = new[]
            {
                ("old",    now.AddDays(-20)),
                ("recent", now.AddDays(-1)),
                ("fresh",  now),
            };
            foreach (var (_, stamp) in stamps)
            {
                var stampDir = Path.Combine(tmp, "deepseek", store.FormatRunStamp(stamp));
                Directory.CreateDirectory(stampDir);
                File.WriteAllText(Path.Combine(stampDir, "x.md"), "x");
            }

            var removed = store.PurgeOld(now - SyncTrashStore.Retention);
            removed.Should().Be(1);
            // Только старый ушёл
            Directory.Exists(Path.Combine(tmp, "deepseek", store.FormatRunStamp(now.AddDays(-20)))).Should().BeFalse();
            Directory.Exists(Path.Combine(tmp, "deepseek", store.FormatRunStamp(now.AddDays(-1)))).Should().BeTrue();
            Directory.Exists(Path.Combine(tmp, "deepseek", store.FormatRunStamp(now))).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ─── Живое окно из LocalEndpointProbe ──────────────────────────────────────
    // Фейковая проба: имитирует LocalEndpointProbe.TryGetKnownContextWindow без HTTP,
    // отдаёт захардкоженное max_model_len (или не отдаёт, если 0).
    private sealed class FakeLocalProbe(int knownWindow) : ILocalEndpointProbe
    {
        public Task<LocalProbeOutcome> CheckAsync(LlmProviderConfig provider, CancellationToken ct = default)
            => Task.FromResult(LocalProbeOutcome.Alive);
        public bool TryGetKnownContextWindow(string providerKey, out int window)
        {
            window = knownWindow;
            return knownWindow > 0;
        }
        public void Invalidate(string providerKey) { }
    }

    private static LlmProviderRegistry CreateWithLocal(Dictionary<string, string?> settings, ILocalEndpointProbe probe)
    {
        var config = TestConfig.Build(settings);
        return new LlmProviderRegistry(config, probe);
    }

    // Локальный провайдер (IsLocal=true) с пробой, у которой есть живое окно:
    // BuildCliEnv кладёт в CLAUDE_CODE_MAX_CONTEXT_TOKENS ЖИВОЕ значение, не каталог.
    [Fact]
    public void BuildCliEnv_ЛокальныйПровайдер_ИспользуетLiveMaxModelLenИзПробы()
    {
        // Каталог объявляет 71680 (старый CTX=long), проба знает фактические 65536 (CTX=fast).
        // Без правки BuildCliEnv прокидывал бы 71680 — CLI считал бы, что контекст влезает,
        // и не сжимал вовремя (57345 + 8192 < 71680), а сервер отбивал на одном токене.
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:DisplayName"] = "Qwen",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:local-qwen:Models:0:ContextWindow"] = "71680",
        };
        var probe = new FakeLocalProbe(knownWindow: 65536);
        var env = CreateWithLocal(settings, probe).BuildCliEnv("qwen3.8-27b")!;
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("65536");
    }

    // Проба не успела опросить стенд (knownWindow=0) — fail-open на каталог.
    [Fact]
    public void BuildCliEnv_ЛокальныйПровайдер_ПробаНеЗнает_ФолалноНаКаталог()
    {
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:DisplayName"] = "Qwen",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:local-qwen:Models:0:ContextWindow"] = "71680",
        };
        var probe = new FakeLocalProbe(knownWindow: 0);
        var env = CreateWithLocal(settings, probe).BuildCliEnv("qwen3.8-27b")!;
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("71680");
    }

    // Облачный провайдер — проба НЕ применяется, даже если бы могла отдать число.
    // Иначе отказа в локальной пробе на облачном провайдере (IsLocal=false) выдавал бы
    // ложное окно — регрессия прежнего поведения.
    [Fact]
    public void BuildCliEnv_ОблачныйПровайдер_ПробаИгнорируется()
    {
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:ContextWindow"] = "1048576",
        };
        var probe = new FakeLocalProbe(knownWindow: 99999);
        var env = CreateWithLocal(settings, probe).BuildCliEnv("deepseek-v4-pro")!;
        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("1048576");
    }
}
