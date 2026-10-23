using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Tests.Plugins;

// Мост панели v1 (ADR-031 §3, шаг 3.1): диспетчер IPluginCapability + валидация
// сообщений моста. Тесты:
//   - диспетчер: нет обработчика → unavailable (503), нет права → denied (403),
//     есть право → вызывает обработчик;
//   - отказ по недопустому домену net называет хост запроса (дефект «Нет права: net:»):
//     без сетевых прав → «Нет права: net:{хост}»; без url → ключ-префикс «Нет права: net:»;
//     право на другой хост → отказ прокси «Нет права: net:{хост}» без изменений;
//   - все тексты отказа — в формате «Нет права: {key}» (deniedKeyOf на фронте):
//     схема url — только http/https, как у прокси (ftp:// → «Некорректный url», не хост);
//     доменная ветка параметризованного права — «Нет права: {префикс}», не свободный текст;
//   - валидация: source=ccs-plugin + v=1 → валидно; чужой source → невалидно;
//     другая версия → невалидно;
//   - мутации (критерий): ЧужойSource_Прошёл, ДругаяВерсия_Прошёл.
public class PluginBridgeTests
{
    // --- Диспетчер: нет обработчика → unavailable (503) ---

    [Fact]
    public async Task Dispatch_НетОбработчика_ОтдаётUnavailable()
    {
        var bridge = new PluginBridge([]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var result = await bridge.DispatchAsync(ctx, "notes.read", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("unavailable");
        result.Error.Message.Should().Contain("notes");
    }

    // --- Диспетчер: operations storage (шаг B1) — обработчика на сервере нет → unavailable, не исключение ---

    [Fact]
    public async Task Dispatch_StorageGet_ОтдаётUnavailable()
    {
        var bridge = new PluginBridge([]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var result = await bridge.DispatchAsync(ctx, "storage.get", null, ["storage"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("unavailable");
        result.Error.Message.Should().Contain("Обработчик не зарегистрирован");
    }

    // --- Диспетчер: нет права в granted → denied (403) ---

    [Fact]
    public async Task Dispatch_НетПрава_ОтдаётDenied()
    {
        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        // granted пуст — право notes:read не выдано.
        var result = await bridge.DispatchAsync(ctx, "notes.read", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        result.Error.Message.Should().Contain("notes:read");
    }

    // --- Диспетчер: есть право → вызывает обработчик ---

    [Fact]
    public async Task Dispatch_ЕстьПрава_ВызываетОбработчик()
    {
        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var args = JsonDocument.Parse("{\"id\":\"n1\"}").RootElement;
        var result = await bridge.DispatchAsync(ctx, "notes.read", args, ["notes:read"], CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.CalledWith.Should().Be("notes.read");
    }

    // --- Отказ по недопустому домену net: называет хост запроса (дефект «Нет права: net:») ---
    // Мутация: вернуть старый текст $"Нет права: {handler.Permission}" без хоста → первый
    // тест красный (в нём нет хоста).

    [Fact]
    public async Task Dispatch_НетСетевыхПрав_ОтказНазываетХост()
    {
        var bridge = new PluginBridge([new StubCapability("net:")]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var args = JsonDocument.Parse("""{"url":"https://api.open-meteo.com/v1/forecast"}""").RootElement;

        var result = await bridge.DispatchAsync(ctx, "net.fetch", args, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        result.Error.Message.Should().Be("Нет права: net:api.open-meteo.com");
    }

    [Fact]
    public async Task Dispatch_НетСетевыхПрав_БезUrl_КлючПрефикса()
    {
        var bridge = new PluginBridge([new StubCapability("net:")]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);

        var result = await bridge.DispatchAsync(ctx, "net.fetch", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        // Хоста нет — ключом остаётся префикс права: текст проходит deniedKeyOf.
        result.Error.Message.Should().Be("Нет права: net:");
    }

    // --- Схема url: право net:{host} — про http/https (как в PluginNetProxy.HandleAsync).
    // Мутация: убрать проверку схемы (Uri.TryCreate без сравнения scheme) → тест красный:
    // отказ назовёт «Нет права: net:example.com».
    [Fact]
    public async Task Dispatch_FtpСхема_ОтказПоФормеUrlАНеПоХосту()
    {
        var bridge = new PluginBridge([new StubCapability("net:")]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var args = JsonDocument.Parse("""{"url":"ftp://example.com/x"}""").RootElement;

        var result = await bridge.DispatchAsync(ctx, "net.fetch", args, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("invalid");
        result.Error.Message.Should().Be("Некорректный url: ftp://example.com/x");
        result.Error.Message.Should().NotContain("Нет права: net:example.com");
    }

    // --- Доменная ветка параметризованного права: текст в формате «Нет права: {key}».
    // Сейчас такой домена, кроме net, нет — ветка публичная: следующий домен её разбудит,
    // и плашка/«Выдать…» должны работать с первого раза.
    // Мутация: вернуть $"Нет права на операции домена: {domain}" → тест красный (нет ключа).
    [Fact]
    public async Task Dispatch_ДоменБезПрава_ТекстВФорматеКлюча()
    {
        var bridge = new PluginBridge([new StubCapability("weather:")]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);

        var result = await bridge.DispatchAsync(ctx, "weather.get", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        result.Error.Message.Should().Be("Нет права: weather:");
    }

    // Право на ДРУГОЙ хост выдано: мост пропускает вызов (covered), отказ формурует сам
    // прокси — «Нет права: net:{хост}», без изменений. Сквозной путь через настоящий
    // PluginNetProxy: отказ на сверке хоста, транспорт не вызывается.
    [Fact]
    public async Task Dispatch_ПравоНаДругойХост_ПроксиОтказываетПоХосту()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_bridge_net_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(dir, "projects.json"),
                })
                .Build();
            var proxy = new PluginNetProxy(new PluginSecretsStore(config), new StubHttpHandler());
            var bridge = new PluginBridge([proxy]);
            var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
            var args = JsonDocument.Parse("""{"url":"https://api.open-meteo.com/v1/forecast"}""").RootElement;

            var result = await bridge.DispatchAsync(ctx, "net.fetch", args, ["net:api.github.com"], CancellationToken.None);

            result.Ok.Should().BeFalse();
            result.Error!.Code.Should().Be("denied");
            result.Error.Message.Should().Be("Нет права: net:api.open-meteo.com");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // --- Валидация: корректный frame message → валидно ---

    [Fact]
    public void Валидация_КорректныйFrameMessage_Проходит()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-plugin", V = 1, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeTrue();
    }

    // --- Мутация: чужой source → невалидно ---
    // Мутация: разрешаем любой source (IsValidFrameMessage → true) → этот тест падает.

    [Fact]
    public void ЧужойSource_Прошёл()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-evil", V = 1, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeFalse();
    }

    // --- Мутация: другая версия → невалидно ---
    // Мутация: разрешаем любую версию (IsValidFrameMessage → true) → этот тест падает.

    [Fact]
    public void ДругаяВерсия_Прошёл()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-plugin", V = 2, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeFalse();
    }

    // --- Валидация: корректный host message → валидно ---

    [Fact]
    public void Валидация_КорректныйHostMessage_Проходит()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-host", V = 1, Type = "init" };
        PluginBridgeMessage.IsValidHostMessage(msg).Should().BeTrue();
    }

    // --- Stub IPluginCapability: фиксирует, какой op получил ---

    private sealed class StubCapability : IPluginCapability
    {
        public string Permission { get; }
        public string? CalledWith;

        public StubCapability(string permission) => Permission = permission;

        public Task<PluginCapabilityResult> HandleAsync(
            PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
        {
            CalledWith = op;
            return Task.FromResult(new PluginCapabilityResult(Ok: true, Data: "ok"));
        }
    }

    // Транспорт для тестов моста: до него не доходит (отказ на сверке хоста), но нужен
    // конструктору PluginNetProxy.
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
