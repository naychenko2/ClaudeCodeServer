using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

// Модельная часть десктопного агента (ADR-008 о десктопном агенте): признак чата,
// включение грани в проекте, событие статуса сеанса рук и фич-флаг.
// Главное, что здесь сторожится, — аддитивность полей: сторы sessions.json/projects.json
// читаются старыми записями без миграции, поэтому BackupSchema.Version не двигается.
public class DesktopChatModelTests
{
    // Те же опции, что у сторов сессий/проектов (SessionManager._jsonOpts): enum строками
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void ДесктопныйЧат_ПоУмолчаниюВыключен()
    {
        new Session().DesktopChat.Should().BeFalse();
        new Project().DesktopAgentEnabled.Should().BeFalse();
    }

    [Fact]
    public void СтараяЗаписьСессииБезПоля_ЧитаетсяКакОбычныйЧат()
    {
        // Запись sessions.json, сделанная до фичи: поля desktopChat в ней нет вовсе
        const string legacy = """{"id":"s1","provider":"claude","model":"sonnet"}""";
        var session = JsonSerializer.Deserialize<Session>(legacy, StoreJson)!;
        session.DesktopChat.Should().BeFalse("аддитивное поле с дефолтом формат стора не ломает");
    }

    [Fact]
    public void СтараяЗаписьПроектаБезПоля_ГраньВыключена()
    {
        const string legacy = """{"id":"p1","name":"Проект","rootPath":"/tmp/p1"}""";
        var project = JsonSerializer.Deserialize<Project>(legacy, StoreJson)!;
        project.DesktopAgentEnabled.Should().BeFalse();
    }

    [Fact]
    public void ПризнакиПереживаютКругСериализации()
    {
        var session = JsonSerializer.Deserialize<Session>(
            JsonSerializer.Serialize(new Session { DesktopChat = true }, StoreJson), StoreJson)!;
        session.DesktopChat.Should().BeTrue();

        var project = JsonSerializer.Deserialize<Project>(
            JsonSerializer.Serialize(new Project { DesktopAgentEnabled = true }, StoreJson), StoreJson)!;
        project.DesktopAgentEnabled.Should().BeTrue();
    }

    [Fact]
    public void ФлагDesktopAgent_ЕстьВКаталогеИВыключенПоУмолчанию()
    {
        var flag = FeatureFlagCatalog.All.Single(f => f.Key == FeatureFlagKeys.DesktopAgent);
        flag.Key.Should().Be("desktop-agent", "ключ дублируется в lib/featureFlags.ts — при переименовании править оба места");
        flag.Default.Should().BeFalse("фича коммитится выключенной (dark launch)");
        flag.Stage.Should().Be("dev");
        FeatureFlagCatalog.Exists(FeatureFlagKeys.DesktopAgent).Should().BeTrue();
    }

    [Fact]
    public void ФлагDesktopAgent_ОписаниеБезОбещанийБезопасности()
    {
        // Решение ADR: единственный предохранитель — человек у машины, слов «второй фактор»
        // и «изоляцию заменяет подтверждение» в интерфейсе быть не должно.
        var flag = FeatureFlagCatalog.All.Single(f => f.Key == FeatureFlagKeys.DesktopAgent);
        flag.Description.Should().NotContainAny("второй фактор", "безопасн", "изоляцию заменя");
    }

    [Fact]
    public void СобытиеСеансаРук_ТипИПоля()
    {
        var started = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        var msg = new DesktopSessionMessage(true, "home", "s1", "Разобрать почту", started, started.AddHours(2))
        {
            SessionId = "s1",
        };
        msg.Type.Should().Be("desktop_session");
        msg.Active.Should().BeTrue();
        msg.DeviceName.Should().Be("home");
        msg.Reason.Should().BeNull("у активного сеанса причины гашения нет");

        var off = new DesktopSessionMessage(false, "home", "s1", Reason: "idle");
        off.Active.Should().BeFalse();
        off.DeviceName.Should().Be("home", "бейджу нужно знать, ЧЬИ руки отпустили");
        off.Reason.Should().Be("idle");
    }
}
