using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ClaudeHomeServer.Tests.Contract;

/// <summary>
/// Сторож G6 (ADR-016 §5, план §4): в ретрансляторе нет записи по построению. Перечень
/// операций протокола сверяется с замкнутым allow-list ниже — новая операция, поле запроса или
/// маршрут без правки этого теста красит сборку. Правка allow-list — осознанное решение с
/// ревью, а не побочный эффект.
/// </summary>
public sealed class RelayProtocolGuardTests
{
    private static readonly string[] AllowedOperations =
        ["list", "read", "stat", "search", "git-status", "git-diff", "git-log", "git-show"];

    // Поля запроса: ни одно не несёт данных для записи (содержимого, нового имени, сообщения коммита)
    private static readonly string[] AllowedRequestFields =
        ["Operation", "ProjectId", "RootPath", "Variant", "Path", "ShowHidden", "Query", "Staged", "Limit", "Branch", "Sha"];

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void G6_ОперацииПротокола_РовноAllowList()
    {
        Constants(typeof(RelayOperations)).Should().BeEquivalentTo(AllowedOperations,
            "новая операция ретранслятора — только с правкой allow-list сторожа G6");
        RelayOperations.All.Should().BeEquivalentTo(AllowedOperations);
    }

    [Fact]
    public void G6_ПоляЗапроса_РовноAllowList()
    {
        typeof(RelayRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Should().BeEquivalentTo(AllowedRequestFields, "поле запроса могло бы привезти агенту данные для записи");
    }

    [Fact]
    public void G6_МаршрутыРетранслятора_ТолькоGet_ИТолькоИзвестныеОперации()
    {
        RelayProtocol.Routes.Select(r => r.Operation).Should().OnlyContain(op => AllowedOperations.Contains(op));
        RelayProtocol.Routes.Select(r => r.Template).Should().OnlyHaveUniqueItems();

        var actions = typeof(ProjectRelayController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var routes = actions.SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()).ToList();
        routes.SelectMany(a => a.HttpMethods).Should().OnlyContain(m => m == "GET", "у ретранслятора нет ни одного пишущего метода");
        routes.Select(a => a.Template).Should().BeEquivalentTo(RelayProtocol.Routes.Select(r => r.Template),
            "контроллер обслуживает ровно маршруты протокола");
    }

    [Fact]
    public void G6_Агент_ИсполняетРовноОперацииПротокола()
    {
        var root = Path.GetTempPath();
        var git = new GitService(AgentLauncherFactory.Instance);
        var relay = new RelayHandler(new AgentProjectFiles(new FileService(git), new AgentPathPolicy(new FixedRoots(root))), git);

        relay.Operations.Should().BeEquivalentTo(AllowedOperations);
    }

    [Fact]
    public void НазначенияКанала_ТолькоRelay()
    {
        Constants(typeof(DeviceExecPurposes)).Should().Equal(DeviceExecPurposes.Relay);
    }

    private sealed class FixedRoots(string root) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = [root];
    }
}
