using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.RemoteCommands;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.Services.RemoteCommands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Controllers;

// Замки пульта на уровне HTTP. Фича по умолчанию выключена конфигом — ровно в этом состоянии
// код приезжает на чужую машину: пункта меню нет, мутирующие эндпоинты отвечают 404, а GET
// всё равно 200 (его зовёт шапка при монтировании, и 404 шумел бы ошибкой в консоли).
public class RemoteCommandsControllerTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetStatus_Аноним_401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/remote-commands");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStatus_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.GetAsync("/api/admin/remote-commands");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.PostAsync("/api/admin/remote-commands/demo/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetStatus_ФичаВыключена_200СПризнакомEnabledFalse()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/remote-commands");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        body.GetProperty("actions").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("refresh")]
    public async Task Мутирующие_ФичаВыключена_404(string operation)
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync($"/api/admin/remote-commands/demo/{operation}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Output_ФичаВыключена_404()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/remote-commands/demo/output");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// Тот же контроллер, но на включённом пульте: реестр действий приходит из конфигурации,
// шелл подменён фейком — настоящие команды в тестах не исполняются.
public class RemoteCommandsControllerEnabledTests : IDisposable
{
    private readonly FakeShellCommandRunner _runner = new();
    private readonly TestWebApplicationFactory _factory = new();

    public RemoteCommandsControllerEnabledTests()
    {
        _factory.ExtraConfig["RemoteCommands:Enabled"] = "true";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Key"] = "demo";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Title"] = "Демо";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Mode"] = "oneshot";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Start"] = "старт";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Stop"] = "стоп";
        _factory.ExtraConfig["RemoteCommands:Actions:0:Status"] = "статус";
        _factory.ExtraConfig["RemoteCommands:Actions:0:StatusRunningPattern"] = "RUNNING";
        _runner.Handler = cmd => Task.FromResult(ShellRunResult.Exited(0, cmd == "статус" ? "RUNNING" : ""));
        _factory.ExtraServices = services => services.AddSingleton<IShellCommandRunner>(_runner);
    }

    [Fact]
    public async Task GetStatus_ОтдаётДействияБезТекстовКоманд()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/remote-commands");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("старт").And.NotContain("статус");

        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        var action = body.GetProperty("actions").EnumerateArray().Single();
        action.GetProperty("key").GetString().Should().Be("demo");
        action.GetProperty("state").GetString().Should().Be("unknown", "до первой проверки состояние неизвестно");
        action.GetProperty("checkedAt").ValueKind.Should().Be(JsonValueKind.Null);
        _runner.Ran.Should().BeEmpty("список не исполняет ни одной команды");
    }

    [Fact]
    public async Task Start_ИзвестныйКлюч_200СоСостоянием()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/admin/remote-commands/demo/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("state").GetString().Should().Be("running");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("refresh")]
    public async Task Мутирующие_НеизвестныйКлюч_404(string operation)
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync($"/api/admin/remote-commands/нет-такого/{operation}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_ВОтветЕдутВремяПроверкиИКодВозврата()
    {
        // Фронт объявляет и рисует checkedAt/lastExitCode: без них под свежим бейджем
        // «Запущен» висело бы «Ещё не проверялось».
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/admin/remote-commands/demo/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("state").GetString().Should().Be("running");
        body.GetProperty("checkedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        body.GetProperty("lastExitCode").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Start_ОборвавшийсяЗапрос_ДереваКомандыНеУбивает()
    {
        // Прокси рвёт соединение по своему лимиту (60 с) раньше, чем отработает `docker
        // compose up -d` с таймаутом 180 с. Токен запроса в команду не едет вовсе —
        // единственный рубильник прерывания это TimeoutSeconds из конфига.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async cmd =>
        {
            if (cmd != "старт") return ShellRunResult.Exited(0, cmd == "статус" ? "RUNNING" : "");
            entered.TrySetResult();
            await release.Task; // команда «идёт» на машине, пока тест не отпустит
            return ShellRunResult.Exited(0, "");
        };

        var client = _factory.CreateAuthenticatedClient();
        using var aborted = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/remote-commands/demo/start");

        var send = client.SendAsync(request, aborted.Token);
        await entered.Task;                 // команда уже исполняется
        await aborted.CancelAsync();        // ...и тут клиент уходит
        release.TrySetResult();             // а команда на машине доделывает своё
        try { await send; } catch (OperationCanceledException) { /* клиент ушёл — это и проверяем */ }

        // Операция обязана дойти до конца: следом за стартом идёт проверка состояния
        await WaitForAsync(() => _runner.Ran.Contains("статус"));
        _runner.Tokens.Should().OnlyContain(t => !t.CanBeCanceled,
            "в команду прокинут отменяемый токен запроса — обрыв соединения убил бы дерево процесса");
    }

    // Ждём событие, а не паузу: на слабом CI фиксированная задержка флакает
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("команда так и не доехала до раннера");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Output_ИзвестныйКлюч_ОтдаётНакопленныйВывод()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/remote-commands/demo/output");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("text").ValueKind.Should().Be(JsonValueKind.String);
    }

    // Регистрация из Program.cs: hosted-сервис обязан быть ТЕМ ЖЕ экземпляром, что и синглтон.
    // AddHostedService<T>() создал бы второй, и StopAsync гасил бы daemon-детей у пустышки,
    // оставляя настоящих сиротами при каждом рестарте.
    [Fact]
    public void Program_РегистрируетПульт_HostedЭтоТотЖеЭкземпляр()
    {
        var sp = _factory.Services;
        var service = sp.GetRequiredService<RemoteCommandsService>();

        var hosted = sp.GetServices<IHostedService>().OfType<RemoteCommandsService>().ToList();

        hosted.Should().ContainSingle();
        ReferenceEquals(hosted[0], service).Should().BeTrue(
            "иначе остановка сервера гасит daemon-детей у второго, пустого экземпляра");
    }

    public void Dispose() => _factory.Dispose();
}
