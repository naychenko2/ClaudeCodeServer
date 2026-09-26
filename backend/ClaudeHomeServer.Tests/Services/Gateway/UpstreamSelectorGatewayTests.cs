using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Выбор upstream шлюза LLM (ADR-016): сторожа G2 (тумблер AllowSubscriptions) и G11
/// (только setup-token, без тихого перехода на API-ключ и интерактивный логин).
/// </summary>
public class UpstreamSelectorGatewayTests
{
    // ---- Тумблер шлюза ----

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("deepseek-v4-pro")]
    public void ШлюзВыключен_ОтказДоВыдачиТокена_ПричинаВТексте(string model)
    {
        using var kit = new GatewayTestKit(("st", "tok-st", null));
        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = false, AllowSubscriptions = true };
        var tokens = new TurnTokenService(new TurnEventBus());

        var start = kit.Selector.StartTurn(tokens, "owner", "chat", "device", model);

        start.Token.Should().BeNull("ход не стартует");
        start.FailureText.Should().Be(TurnFailureText.GatewayDisabled);
        start.FailureText.Should().Contain("LlmGateway:Enabled");
        tokens.ActiveCount.Should().Be(0);
        kit.Pool.PickSetupTokenCalls.Should().Be(0);

        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = true };
        kit.Selector.StartTurn(tokens, "owner", "chat", "device", model).Token
            .Should().NotBeNull("тумблер читается живьём, без рестарта");
    }

    // ---- G2 ----

    [Fact]
    public void G2_ПодпискиВыключены_ОтказНаСтарте_ПулНеСпрашивается()
    {
        using var kit = new GatewayTestKit(("st", "tok-st", null));
        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = false };

        var d = kit.Selector.SelectRoute("claude-opus-5-5");

        d.Route.Should().BeNull();
        d.FailureText.Should().Be(TurnFailureText.GatewaySubscriptionsDisabled);
        kit.Pool.PickCalls.Should().Be(0);
        kit.Pool.PickSetupTokenCalls.Should().Be(0);
    }

    [Fact]
    public void G2_ВыключениеПосредиХода_ОтказНаЗапросе_ВТомЧислеНаРотации_БезРестарта()
    {
        using var kit = new GatewayTestKit(("st1", "t1", null), ("st2", "t2", null));
        var route = kit.Selector.SelectRoute("sonnet").Route!;
        kit.Pool.PickSetupTokenCalls = 0;

        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = false };
        kit.Pool.MarkExhausted(route.SubscriptionKey!); // запрос хотел бы ротацию

        var d = kit.Selector.ResolveUpstream(route);
        d.Upstream.Should().BeNull();
        d.FailureStatus.Should().Be(403);
        d.FailureText.Should().Be(TurnFailureText.GatewaySubscriptionsDisabled);
        kit.Pool.PickCalls.Should().Be(0);
        kit.Pool.PickSetupTokenCalls.Should().Be(0, "ротация тумблер не обходит");

        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = true };
        kit.Selector.ResolveUpstream(route).Upstream.Should().NotBeNull("включение тоже без рестарта");
    }

    [Fact]
    public void G2_СтороннийПровайдер_ТумблерПодписокНеМешает()
    {
        using var kit = new GatewayTestKit();
        kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = false };

        var route = kit.Selector.SelectRoute("deepseek-v4-pro").Route!;
        route.Kind.Should().Be(GatewayUpstreamKind.Provider);
        var up = kit.Selector.ResolveUpstream(route).Upstream!;
        up.Credential.Should().Be("ds-key");
        kit.Pool.PickSetupTokenCalls.Should().Be(0);
    }

    // ---- G11 ----

    public static TheoryData<string, (string, string?, string?)[]> NoSetupTokenPools => new()
    {
        { "пустой пул — интерактивный логин", [] },
        { "только API-ключ", [("api", null, "sk-ant-x")] },
        { "токен вместе с ключом — это ключ", [("both", "tok", "sk-ant-x")] },
    };

    [Theory]
    [MemberData(nameof(NoSetupTokenPools))]
    public void G11_НетSetupToken_ХодНеСтартует_ПричинаВОтказе_PickНеЗовётся(string _, (string, string?, string?)[] subs)
    {
        using var kit = new GatewayTestKit(subs);
        var tokens = new TurnTokenService(new TurnEventBus());

        var start = kit.Selector.StartTurn(tokens, "owner", "chat", "device", "claude-opus-5-5");

        start.Token.Should().BeNull("ход не стартует");
        start.FailureText.Should().Be(TurnFailureText.LocalProjectsNeedSetupToken);
        start.FailureText.Should().Contain("claude setup-token");
        tokens.ActiveCount.Should().Be(0);
        kit.Pool.PickCalls.Should().Be(0, "ни PrimaryKey, ни API-ключ не запрашиваются");
    }

    [Fact]
    public void G11_ВсеSetupTokenИсчерпаныИлиAuthDead_Отказ()
    {
        using var kit = new GatewayTestKit(("st1", "t1", null), ("st2", "t2", null), ("api", null, "sk"));
        kit.Pool.MarkExhausted("st1", DateTime.UtcNow.AddHours(1));
        kit.Pool.MarkAuthDead("st2");

        var d = kit.Selector.SelectRoute("sonnet");

        d.Route.Should().BeNull();
        d.FailureText.Should().Be(TurnFailureText.LocalProjectsNeedSetupToken);
        kit.Pool.PickCalls.Should().Be(0);
    }

    [Fact]
    public void G11_Смесь_ВыбранSetupToken_ТокенСтрогоИзКонфига_НеИзПрофиля()
    {
        using var kit = new GatewayTestKit(("api", null, "sk-ant-x"), ("st", "tok-from-config", null));
        // Профиль подписки с живым .credentials.json — шлюз туда не смотрит.
        var profile = Path.Combine(kit.TempDir, "claude-profiles", "sub-st");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"tok-from-profile"}}""");
        var tokens = new TurnTokenService(new TurnEventBus());

        var start = kit.Selector.StartTurn(tokens, "owner", "chat", null, "opus");

        start.Token!.Grant.Route!.SubscriptionKey.Should().Be("st");
        var up = kit.Selector.ResolveUpstream(start.Token.Grant.Route).Upstream!;
        up.Credential.Should().Be("tok-from-config");
        up.BaseUrl.Should().Be("https://anthropic.test");
        kit.Pool.PickCalls.Should().Be(0);
    }

    [Fact]
    public void G11_РотацияПосредиХода_ТолькоВнутриSetupToken_ИсчерпалсяНабор_Отказ()
    {
        using var kit = new GatewayTestKit(("st1", "t1", null), ("st2", "t2", null), ("api", null, "sk"));
        var route = kit.Selector.SelectRoute("sonnet").Route!;
        var first = route.SubscriptionKey!;
        var second = first == "st1" ? "st2" : "st1";

        kit.Pool.MarkExhausted(first);
        var rotated = kit.Selector.ResolveUpstream(route).Upstream!;
        rotated.Route.SubscriptionKey.Should().Be(second);

        kit.Pool.MarkAuthDead(second);
        var d = kit.Selector.ResolveUpstream(rotated.Route);
        d.Upstream.Should().BeNull("API-ключ в ротации не участвует");
        d.FailureText.Should().Be(TurnFailureText.LocalProjectsNeedSetupToken);
        kit.Pool.PickCalls.Should().Be(0);
    }

    [Fact]
    public void G11_ВШлюзеНетВызововPick_ТолькоPickSetupToken()
    {
        var dir = Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer.Llm", "Gateway");
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty();
        var pick = new Regex(@"\bPick\s*\(", RegexOptions.Compiled);
        foreach (var f in files)
            File.ReadAllLines(f)
                .Select((line, i) => (line, i))
                .Where(x => pick.IsMatch(x.line))
                .Should().BeEmpty($"в {Path.GetFileName(f)} подписку выбирает только PickSetupToken");
    }

    // ---- модель ----

    [Fact]
    public void Модель_ФоновыйHaikuУСтороннегоUpstream_Переписывается()
    {
        using var kit = new GatewayTestKit();
        var route = kit.Selector.SelectRoute("deepseek-v4-pro").Route!;

        kit.Selector.RewriteModel(route, "claude-haiku-4-5-20251001").Should().Be("deepseek-v4-flash");
        kit.Selector.RewriteModel(route, "claude-opus-5-5").Should().Be("deepseek-v4-pro", "клиентская модель — подсказка");
        kit.Selector.RewriteModel(route, "deepseek-v4-flash").Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void Модель_Подписка_РодныеIdОстаются_ЧужиеЗаменяются()
    {
        using var kit = new GatewayTestKit(("st", "t", null));
        var route = kit.Selector.SelectRoute("claude-opus-5-5").Route!;

        kit.Selector.RewriteModel(route, "claude-haiku-4-5-20251001").Should().Be("claude-haiku-4-5-20251001");
        kit.Selector.RewriteModel(route, "deepseek-v4-pro").Should().Be("claude-opus-5-5");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "backend", "ClaudeHomeServer.Llm")))
                return dir.FullName;
        throw new InvalidOperationException("Корень репозитория не найден");
    }
}
