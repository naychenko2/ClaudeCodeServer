using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Решение владельца 2026-09-27 (ADR-016 §7): списка «кому доверяем руки» нет, руки доступны
// любому провайдеру, ход с руками не сужает цепочку фолбэка. Единственный фильтр по провайдеру —
// зрение (HandsTurnTests.ПровайдерБезЗрения_МаркерБезЗрения). Сторожа не дают вернуть список
// тихо: ни в модели пользователя, ни в контексте хода, ни в фолбэке, ни состоянием бейджа.
public class HandsAnyProviderTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "cc_hands_any_provider_" + Guid.NewGuid().ToString("N"));

    public HandsAnyProviderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore BuildStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
    }

    [Fact]
    public void СпискаПровайдеровНетНиВМодели_НиВКонтекстеХода_НиВФолбэке()
    {
        typeof(User).GetProperty("HandsProviders").Should().BeNull();
        typeof(LlmSessionContext).GetProperty("HandsProviders").Should().BeNull();
        typeof(FallbackLlmSessionAdapter).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.Name!.StartsWith("hands", StringComparison.OrdinalIgnoreCase),
                "ход с руками не сужает цепочку фолбэка");
        typeof(HandsChatStates).GetFields()
            .Select(f => f.GetValue(null)).OfType<string>()
            .Should().NotContain("provider-not-allowed", "отказа «провайдеру руки не доверены» больше нет");
        typeof(UserStore).Assembly.GetType("ClaudeHomeServer.Controllers.MyHandsProvidersController")
            .Should().BeNull("ручка /api/me/hands-providers удалена");
    }

    // Старые users.json несут поле списка — оно просто игнорируется: пользователь читается,
    // а следующее сохранение поле уже не пишет
    [Fact]
    public void СтарыйUsersJsonСПолемHandsProviders_ЧитаетсяБезОшибки()
    {
        var user = BuildStore().Add("u1", "password123", "user");
        var path = Path.Combine(_tempDir, "users.json");
        var json = File.ReadAllText(path);
        json.Should().Contain("\"Username\"");
        File.WriteAllText(path, json.Replace("\"Username\"",
            "\"HandsProviders\": [\"claude\", \"deepseek\"], \"handsProviders\": [\"glm\"], \"Username\"", StringComparison.Ordinal));

        var reloaded = BuildStore();
        reloaded.GetById(user.Id).Should().NotBeNull();
        reloaded.GetById(user.Id)!.Username.Should().Be("u1");
        Directory.GetFiles(_tempDir, "*.corrupt*").Should().BeEmpty("файл не должен уйти в карантин");

        reloaded.SetModelTiers(user.Id, "opus", null, null).Should().BeTrue();
        File.ReadAllText(path).Should().NotContainEquivalentOf("handsProviders");
    }
}
