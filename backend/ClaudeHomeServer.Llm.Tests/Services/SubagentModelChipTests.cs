using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чип модели на карточке персоны-сабагента (спека «Чип модели…»): состояние — функция
// пары (персона, сессия), считается ModelAssignmentResolver.SubagentChipFor. Покрывает
// все состояния таблицы: пин-тир в Claude-чате, «модель чата» без пина, слоты провайдера
// (основная/средняя/быстрая) в стороннем чате — строго по env-маппингу BuildCliEnv.
public class SubagentModelChipTests
{
    private const string HintTier = "Модель персоны · выбрана слотом «Сабагенты-консультанты»";
    private const string HintChatModel = "Своей модели у персоны нет — идёт на модели этого чата";
    private const string HintProvider =
        "У персоны модель стороннего провайдера — в сабагенте она не применяется, ход идёт на модели этого чата";

    private static Persona MakePersona(string? model = null) => new()
    {
        Name = "Гефест",
        Handle = "gefest",
        Model = model,
    };

    private static ModelAssignmentResolver BuildResolver(
        Dictionary<string, string?>? settings = null, string? mediumSlot = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var d = settings ?? new Dictionary<string, string?>();
        d["DataPath"] = Path.Combine(dir, "projects.json");
        var config = TestConfig.Build(d);
        var appSettings = new AppSettingsService(config);
        if (mediumSlot is not null)
            appSettings.Save(new AppSettings { ModelTierMedium = mediumSlot });
        var providers = new LlmProviderRegistry(config);
        return new ModelAssignmentResolver(appSettings, providers: providers);
    }

    // Конфиг стороннего CLI-провайдера (паттерн — PersonaAgentFileGeneratorTests)
    private static Dictionary<string, string?> GlmProvider(string? medium = null, string? small = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["LlmProviders:glm:DisplayName"] = "GLM",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://example.test/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        };
        if (medium is not null) d["LlmProviders:glm:MediumModel"] = medium;
        if (small is not null) d["LlmProviders:glm:SmallModel"] = small;
        return d;
    }

    [Fact]
    public void ClaudeЧат_ЯвнаяМодельПерсоны_ПинТиром()
    {
        var chip = BuildResolver().SubagentChipFor(MakePersona("claude-opus-4-8"), sessionModel: null, "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindTier);
        chip.Label.Should().Be("opus");
        chip.Hint.Should().Be(HintTier);
    }

    [Fact]
    public void ClaudeЧат_ПерсонаБезМодели_ПинИзСлотаМеста()
    {
        // Место «сабагенты-консультанты» — тир medium: без своей модели персона пинится
        // слотом «средняя» (та же формула, что пинит frontmatter .md в PersonaAgentFileSync)
        var chip = BuildResolver(mediumSlot: "claude-sonnet-5")
            .SubagentChipFor(MakePersona(), sessionModel: null, "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindTier);
        chip.Label.Should().Be("sonnet");
        chip.Hint.Should().Be(HintTier);
    }

    [Fact]
    public void ClaudeЧат_БезПина_МодельЧата()
    {
        // Ни модели персоны, ни слота места — пина нет, сабагент идёт на модели чата
        var chip = BuildResolver().SubagentChipFor(MakePersona(), sessionModel: null, "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindChatModel);
        chip.Label.Should().Be("модель чата");
        chip.Hint.Should().Be(HintChatModel);
    }

    [Fact]
    public void ClaudeЧат_МодельПерсоныСторонняя_ПинаНет_МодельЧата()
    {
        // ModelTierAlias пинит только Claude-модели: сторонняя модель персоны в Claude-чате
        // пина не даёт — сабагент идёт на модели чата
        var chip = BuildResolver(GlmProvider())
            .SubagentChipFor(MakePersona("glm-5.2"), sessionModel: null, "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindChatModel);
        chip.Label.Should().Be("модель чата");
    }

    [Fact]
    public void СтороннийЧат_ПинOpus_ОсновнаяПровайдера()
    {
        var chip = BuildResolver(GlmProvider(medium: "glm-air", small: "glm-flash"))
            .SubagentChipFor(MakePersona("claude-opus-4-8"), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderMain);
        chip.Label.Should().Be("провайдер: основная");
        chip.Hint.Should().Be(HintProvider);
    }

    [Fact]
    public void СтороннийЧат_ПинSonnet_СредняяПровайдера()
    {
        var chip = BuildResolver(GlmProvider(medium: "glm-air"))
            .SubagentChipFor(MakePersona("claude-sonnet-5"), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderMedium);
        chip.Label.Should().Be("провайдер: средняя");
        chip.Hint.Should().Be(HintProvider);
    }

    [Fact]
    public void СтороннийЧат_ПинSonnetБезMediumModel_СворачиваетсяВОсновную()
    {
        // BuildCliEnv: medium = MediumModel ?? main — незаданный средний слот это та же основная
        var chip = BuildResolver(GlmProvider())
            .SubagentChipFor(MakePersona("claude-sonnet-5"), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderMain);
        chip.Label.Should().Be("провайдер: основная");
    }

    [Fact]
    public void СтороннийЧат_ПинHaiku_БыстраяПровайдера()
    {
        var chip = BuildResolver(GlmProvider(small: "glm-flash"))
            .SubagentChipFor(MakePersona("claude-haiku-4-5-20251001"), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderFast);
        chip.Label.Should().Be("провайдер: быстрая");
        chip.Hint.Should().Be(HintProvider);
    }

    [Fact]
    public void СтороннийЧат_БезПина_БыстраяПровайдера()
    {
        // Без пина сабагент уходит на CLAUDE_CODE_SUBAGENT_MODEL = small
        var chip = BuildResolver(GlmProvider(small: "glm-flash"))
            .SubagentChipFor(MakePersona(), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderFast);
        chip.Label.Should().Be("провайдер: быстрая");
    }

    [Fact]
    public void СтороннийЧат_БезПинаИБезSmallModel_СворачиваетсяВОсновную()
    {
        // BuildCliEnv: small = SmallModel ?? main — незаданный быстрый слот это та же основная
        var chip = BuildResolver(GlmProvider())
            .SubagentChipFor(MakePersona(), sessionModel: "glm-5.2", "u1");

        chip.Kind.Should().Be(ModelAssignmentResolver.SubagentModelChip.KindProviderMain);
        chip.Label.Should().Be("провайдер: основная");
    }
}
