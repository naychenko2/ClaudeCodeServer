using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Выбор аккаунта для локального хода (ADR-016 §2, задача 1.2а, сторож G11): только
/// setup-token; нет подходящего — null, а не интерактивный логин и не API-ключ.
/// </summary>
public class ClaudeSubscriptionPoolSetupTokenTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "subpool_st_" + Guid.NewGuid().ToString("N"));

    public ClaudeSubscriptionPoolSetupTokenTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Записи пула: key → (OAuthToken, ApiKey, Tier)
    private IConfiguration Config(params (string Key, string? OAuth, string? ApiKey, string? Tier)[] subs)
    {
        var dict = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_tempDir, "projects.json") };
        foreach (var (key, oauth, apiKey, tier) in subs)
        {
            if (oauth is not null) dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = oauth;
            if (apiKey is not null) dict[$"{ClaudeSubscriptionPool.Section}:{key}:ApiKey"] = apiKey;
            if (tier is not null) dict[$"{ClaudeSubscriptionPool.Section}:{key}:Tier"] = tier;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void ПустойПул_ИнтерактивныйЛогин_НеВыбирается()
    {
        var pool = new ClaudeSubscriptionPool(Config());

        pool.PickSetupToken().Should().BeNull("PrimaryKey — интерактивный логин, его обновляет серверный CLI");
        pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey, "серверный выбор не изменился");
    }

    [Fact]
    public void ТолькоApiКлюч_НеВыбирается()
    {
        var pool = new ClaudeSubscriptionPool(Config(("api", null, "sk-ant-x", "max20")));

        pool.PickSetupToken().Should().BeNull();
        pool.Pick().Should().Be("api");
    }

    [Fact]
    public void ТокенИКлючВОднойЗаписи_ЭтоКлюч_НеВыбирается()
    {
        var pool = new ClaudeSubscriptionPool(Config(("both", "tok", "sk-ant-x", null)));

        pool.PickSetupToken().Should().BeNull("ApiKey перебивает OAuthToken — ход ушёл бы по ключу");
        pool.SetupTokenOf("both").Should().BeNull();
    }

    [Fact]
    public void Смесь_ВыбираетсяТолькоSetupToken_ДажеПриБолееВысокомТарифеКлюча()
    {
        var pool = new ClaudeSubscriptionPool(Config(
            ("api", null, "sk-ant-x", "max20"),
            ("st", "tok-st", null, "pro"),
            (ClaudeSubscriptionPool.PrimaryKey, null, "sk-ant-y", "max20")));

        for (var i = 0; i < 20; i++)
            pool.PickSetupToken().Should().Be("st");
        pool.SetupTokenOf("st").Should().Be("tok-st");
        pool.SetupTokenOf("api").Should().BeNull();
    }

    [Fact]
    public void ВсеSetupTokenИсчерпаныИлиAuthDead_Null_АНеКтоВоскреснетРаньше()
    {
        var pool = new ClaudeSubscriptionPool(Config(
            ("st1", "t1", null, null), ("st2", "t2", null, null), ("api", null, "sk", null)));
        pool.MarkExhausted("st1", DateTime.UtcNow.AddHours(1));
        pool.MarkAuthDead("st2");

        pool.PickSetupToken().Should().BeNull();
        pool.Pick().Should().Be("api", "серверный выбор по-прежнему берёт живой ключ");
    }

    [Fact]
    public void Ротация_ПослеПометки_ОстаётсяВнутриSetupToken()
    {
        var pool = new ClaudeSubscriptionPool(Config(
            ("st1", "t1", null, "max20"), ("st2", "t2", null, "pro"), ("api", null, "sk", "max20")));

        pool.PickSetupToken().Should().Be("st1", "высший тариф среди setup-token");
        pool.MarkExhausted("st1");
        pool.PickSetupToken().Should().Be("st2");
        pool.MarkAuthDead("st2");
        pool.PickSetupToken().Should().BeNull("набор исчерпан — отказ, а не API-ключ");
    }

    [Fact]
    public void СпособностьПлана_Учитывается()
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:pro:OAuthToken"] = "t",
            [$"{ClaudeSubscriptionPool.Section}:pro:SupportsOpus"] = "false",
        };
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());

        pool.PickSetupToken("opus").Should().BeNull();
        pool.PickSetupToken("sonnet").Should().Be("pro");
    }
}
