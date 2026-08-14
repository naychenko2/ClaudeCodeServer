using ClaudeHomeServer.Telemetry.Alerts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тесты разбора и диффа алертов SigNoz.
///
/// Образец ответа снят с ЖИВОГО стенда (SigNoz v0.134, GET /api/v1/alerts), а не
/// придуман: схема эндпоинта не задокументирована, и две её особенности ломают
/// «очевидную» реализацию — см. комментарии к тестам про fingerprint и endsAt.
/// </summary>
public class AlertDigestTests
{
    /// <summary>Живая выдача: ОДНО правило дало ДВА алерта — по одному на контур.</summary>
    private const string LiveSample = """
    {"status":"success","data":[
      {"labels":{"alertname":"Всплеск ошибок LLM","deployment.environment":"dev",
                 "ruleId":"019fae44-96ac-74a2-b1e1-cd24e1ea0ce2","severity":"warning"},
       "annotations":{"description":"Ошибок больше порога","summary":"проба"},
       "startsAt":"2026-07-29T14:27:40.88729658Z","endsAt":"2026-07-29T14:31:40.88729658Z",
       "generatorURL":"http://localhost:8080/alerts/overview?ruleId=019fae44",
       "status":{"state":"active","silencedBy":[],"inhibitedBy":[]},
       "receivers":["default-receiver"],"fingerprint":"3e5132be5036cc01"},
      {"labels":{"alertname":"Всплеск ошибок LLM","deployment.environment":"production",
                 "ruleId":"019fae44-96ac-74a2-b1e1-cd24e1ea0ce2","severity":"warning"},
       "annotations":{"description":"Ошибок больше порога","summary":"проба"},
       "startsAt":"2026-07-29T14:27:40.88729658Z","endsAt":"2026-07-29T14:31:40.88729658Z",
       "generatorURL":"http://localhost:8080/alerts/overview?ruleId=019fae44",
       "status":{"state":"active","silencedBy":[],"inhibitedBy":[]},
       "receivers":["default-receiver"],"fingerprint":"a5043e589cfce4e9"}
    ]}
    """;

    private static SignozAlert Alert(string fingerprint, string? env = "dev",
        string name = "Тест", bool silenced = false, string? state = "active")
        => new()
        {
            Fingerprint = fingerprint,
            State = state,
            IsSilenced = silenced,
            Labels = new Dictionary<string, string>
            {
                ["alertname"] = name,
                ["deployment.environment"] = env ?? "",
            },
        };

    // ==== разбор ====

    [Fact]
    public void Parse_LiveSample_ReadsBothSeries()
    {
        var alerts = AlertDigest.Parse(LiveSample);

        alerts.Should().HaveCount(2);
        alerts.Select(a => a.Environment).Should().BeEquivalentTo(["dev", "production"]);
        alerts.Select(a => a.Fingerprint).Should().OnlyHaveUniqueItems(
            "одно правило даёт по алерту на серию — их различает только fingerprint");
        alerts[0].Name.Should().Be("Всплеск ошибок LLM");
        alerts[0].Description.Should().Be("Ошибок больше порога");
        alerts[0].Severity.Should().Be("warning");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("не json вовсе")]
    [InlineData("{\"status\":\"success\"}")]              // нет data
    [InlineData("{\"status\":\"success\",\"data\":null}")] // data не массив
    [InlineData("{\"data\":[{\"labels\":{}}]}")]           // элемент без fingerprint
    public void Parse_BadInput_ReturnsEmpty_NeverThrows(string? json)
    {
        // Фоновый опрос не должен падать из-за неожиданного ответа после обновления SigNoz
        AlertDigest.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void Parse_KeepsAlertWithoutOptionalFields()
    {
        // В схеме DTO все поля опциональны, кроме нашего требования к fingerprint
        var alerts = AlertDigest.Parse("""{"data":[{"fingerprint":"abc"}]}""");

        alerts.Should().ContainSingle();
        alerts[0].Name.Should().Be("Алерт телеметрии", "имя должно иметь фолбэк");
        alerts[0].Environment.Should().BeNull();
        alerts[0].Description.Should().BeNull();
    }

    [Fact]
    public void Actionable_SkipsSilencedAndInactive()
    {
        var alerts = new[]
        {
            Alert("live"),
            Alert("muted", silenced: true),
            Alert("suppressed", state: "suppressed"),
        };

        // ContainSingle, а не Equal(...): у Equal(params string[]) пояснение
        // подставилось бы вторым ожидаемым элементом
        AlertDigest.Actionable(alerts).Should().ContainSingle(
            "заглушённые человеком и неактивные будить не должны")
            .Which.Fingerprint.Should().Be("live");
    }

    [Fact]
    public void Actionable_FiltersByEnvironment_WhenConfigured()
    {
        // Рассылает один инстанс, а SigNoz отдаёт ему алерты обоих контуров — при желании
        // дев-шум можно отсечь, оставив только боевой
        var alerts = AlertDigest.Parse(LiveSample);

        AlertDigest.Actionable(alerts, ["production"]).Select(a => a.Environment)
            .Should().Equal("production");
    }

    [Fact]
    public void Actionable_EmptyEnvironmentList_KeepsEverything()
    {
        AlertDigest.Actionable(AlertDigest.Parse(LiveSample), []).Should().HaveCount(2);
    }

    [Fact]
    public void Actionable_AlertWithoutEnvironment_SurvivesFilter()
    {
        // Правило без разреза по среде касается инсталляции целиком — отфильтровав его,
        // мы промолчали бы о том, что важно всем контурам сразу
        var alert = new SignozAlert
        {
            Fingerprint = "f",
            Labels = new Dictionary<string, string> { ["alertname"] = "Пульс пропал" },
        };

        AlertDigest.Actionable([alert], ["production"]).Should().ContainSingle();
    }

    // ==== дифф: главная защита от превращения алертов в шум ====

    [Fact]
    public void Diff_FirstAppearance_IsReported()
    {
        var diff = AlertDigest.Diff([Alert("f1")], new HashSet<string>());

        diff.Started.Should().ContainSingle().Which.Fingerprint.Should().Be("f1");
        diff.Resolved.Should().BeEmpty();
    }

    [Fact]
    public void Diff_SameAlertOnNextTick_IsSilent()
    {
        // Алерт горит часами, опрос идёт раз в минуту. Без этого поведения
        // пользователь получал бы уведомление каждую минуту и выключил бы алерты совсем.
        var diff = AlertDigest.Diff([Alert("f1")], new HashSet<string> { "f1" });

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Diff_Disappeared_IsResolved()
    {
        var diff = AlertDigest.Diff([], new HashSet<string> { "f1" });

        diff.Resolved.Should().Equal("f1");
        diff.Started.Should().BeEmpty();
    }

    [Fact]
    public void Diff_ReappearedAfterResolution_IsReportedAgain()
    {
        // Погасло и загорелось снова — это новое событие, о нём надо сказать
        var diff = AlertDigest.Diff([Alert("f1")], new HashSet<string>());

        diff.Started.Should().ContainSingle();
    }

    [Fact]
    public void Diff_TwoSeriesOfSameRule_AreIndependent()
    {
        // Регрессия: дедупликация по alertname схлопнула бы dev и prod в одно событие,
        // и о падении боевого контура никто бы не узнал, если до этого шумел дев
        var live = AlertDigest.Parse(LiveSample);
        var known = new HashSet<string> { live[0].Fingerprint };

        var diff = AlertDigest.Diff(live, known);

        diff.Started.Should().ContainSingle()
            .Which.Environment.Should().Be("production");
    }

    [Fact]
    public void Diff_ActiveAlertWithFutureEndsAt_IsNotTreatedAsResolved()
    {
        // Ключевая ловушка формата: endsAt у ГОРЯЩЕГО алерта лежит в будущем и
        // продлевается на каждом цикле. Приняв его за «время окончания», мы бы
        // рапортовали о восстановлении, когда проблема ещё идёт.
        var live = AlertDigest.Parse(LiveSample);
        var known = live.Select(a => a.Fingerprint).ToHashSet();

        AlertDigest.Diff(live, known).Resolved.Should().BeEmpty();
    }

    // ==== тексты и ссылки ====

    [Theory]
    [InlineData("production", "Тест — прод")]
    [InlineData("dev", "Тест — dev")]
    public void Describe_PutsEnvironmentInTitle(string env, string expected)
    {
        AlertDigest.Describe(Alert("f", env)).Title.Should().Be(expected);
    }

    [Fact]
    public void Describe_WithoutEnvironment_UsesPlainName()
    {
        var alert = new SignozAlert
        {
            Fingerprint = "f",
            Labels = new Dictionary<string, string> { ["alertname"] = "Пульс пропал" },
        };

        AlertDigest.Describe(alert).Title.Should().Be("Пульс пропал");
    }

    [Fact]
    public void DescribeResolved_MarksRecovery()
    {
        AlertDigest.DescribeResolved(Alert("f", "production")).Title
            .Should().StartWith("Восстановлено:");
    }

    [Fact]
    public void RuleUrl_UsesPublicAddress_NotContainerInternalOne()
    {
        // В самих алертах generatorURL указывает на localhost:8080 — порт ВНУТРИ
        // контейнера. Дав такую ссылку в уведомление, мы отправили бы в никуда.
        var url = AlertDigest.RuleUrl("http://localhost:3301/", AlertDigest.Parse(LiveSample)[0]);

        url.Should().Be("http://localhost:3301/alerts/overview?ruleId=019fae44-96ac-74a2-b1e1-cd24e1ea0ce2");
        url.Should().NotContain("8080");
    }

    [Fact]
    public void RuleUrl_KeepsBasePathOfSignozUrl()
    {
        // С v0.134 SigNoz живёт под base-path /telemetry-proxy (SIGNOZ_GLOBAL_EXTERNAL__URL),
        // и SignozUrl настраивается с префиксом. Ссылка обязана его сохранить —
        // без префикса UI отвечает 404.
        var url = AlertDigest.RuleUrl(
            "http://localhost:3301/telemetry-proxy", AlertDigest.Parse(LiveSample)[0]);

        url.Should().Be(
            "http://localhost:3301/telemetry-proxy/alerts/overview?ruleId=019fae44-96ac-74a2-b1e1-cd24e1ea0ce2");
    }

    [Fact]
    public void RuleUrl_WithoutBaseUrl_IsNull()
    {
        AlertDigest.RuleUrl(null, Alert("f")).Should().BeNull();
        AlertDigest.RuleUrl("http://x", Alert("f")).Should().BeNull("у алерта нет ruleId");
    }
}
