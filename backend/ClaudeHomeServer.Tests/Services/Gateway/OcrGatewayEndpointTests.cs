using System.Net;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// OCR-эндпоинт /gw/t/{turnId}/ocr (фича «руки без UIA», этап A, шаг A2):
/// доступ по токену хода, отказ для запрещённых рук, контракт 200/503/502/403/413/401.
/// </summary>
public sealed class OcrGatewayEndpointTests : IAsyncLifetime
{
    // --- мутабельные фейки: один app на все тесты, мутации через свойства ---

    private sealed class MutableDeviceExecChannel : IDeviceExecChannel
    {
        public DeviceExecStatus? Status { get; set; }
        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => Status;
        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class MutableProbe : ILocalEndpointProbe
    {
        public LocalProbeOutcome Outcome { get; set; } = LocalProbeOutcome.Alive;
        public Task<LocalProbeOutcome> CheckAsync(LlmProviderConfig provider, CancellationToken ct = default) =>
            Task.FromResult(Outcome);
        public bool TryGetKnownContextWindow(string providerKey, out int window) { window = 0; return false; }
        public void Invalidate(string providerKey) { }
    }

    private sealed class MutableSessionDirectory : ISessionDirectory
    {
        public Session? Session { get; set; }
        public Session? GetById(string id) => Session;
        public IReadOnlyCollection<Session> GetAll() => Session is null ? [] : [Session];
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }

    private sealed class MutableProjectManager : IProjectManager
    {
        public Project? Project { get; set; }
        public Project? GetById(string id) => Project;
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Project is null ? [] : [Project];
        public IReadOnlyCollection<Project> GetAll() => Project is null ? [] : [Project];
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => Project is null ? [] : [Project];
    }

    private sealed class FakeVllm(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    // Блокирует до отмены ct (pageCts) — для PageTimeout и BusyTimeout.
    // entered сигналит, когда запрос добрался до vLLM (= слот OCR занят): тест BusyTimeout
    // шлёт второй запрос только после этого, чтобы гонка за слотом была детерминированной.
    private sealed class SlowVllm(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Тело-поток со счётчиком прочитанных байт: ReadBytes = 0 — тело не тронули
    // (413 до чтения), > 0 — читали. Для тестов «413 до чтения тела» и chunked-лимита.
    private sealed class CountingStream(byte[] data) : Stream
    {
        private long _pos;
        public long ReadBytes { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, data.Length - _pos);
            if (n <= 0) return ValueTask.FromResult(0);
            data.AsSpan((int)_pos, n).CopyTo(buffer.Span);
            _pos += n;
            ReadBytes += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- сборка ---

    private readonly TurnEventBus _bus = new();
    private TurnTokenService _tokens = null!;
    private readonly GatewayTestDevice _device = new("owner");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableOptionsMonitor<LlmGatewayOptions> _options = null!;
    private MutableDeviceExecChannel _deviceChannel = null!;
    private MutableProbe _probe = null!;
    private MutableSessionDirectory _sessions = null!;
    private MutableProjectManager _projects = null!;

    private const string SuccessJson =
        """{"choices":[{"message":{"role":"assistant","content":"OCR_TEXT"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    public async Task InitializeAsync()
    {
        var project = new Project
        {
            Id = "proj-1",
            OwnerId = "owner",
            DeviceId = _device.Id,
            HandsEnabled = true,
        };
        var session = new Session { Id = "sess-1", ProjectId = "proj-1", OwnerId = "owner" };
        var deviceStatus = new DeviceExecStatus(
            DeviceId: _device.Id, DeviceName: "test", Online: true,
            Platform: "windows", AgentVersion: "1.0", CliVersion: "1.0",
            RequiredCliVersion: "1.0",
            Capabilities: [DeviceCapabilities.Hands, DeviceCapabilities.Exec],
            HarnessReady: true, HarnessProblem: null);

        _sessions = new MutableSessionDirectory { Session = session };
        _projects = new MutableProjectManager { Project = project };
        _deviceChannel = new MutableDeviceExecChannel { Status = deviceStatus };
        _probe = new MutableProbe();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();

        _tokens = new TurnTokenService(_bus);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton<ISessionDirectory>(_sessions);
        builder.Services.AddSingleton<IProjectManager>(_projects);
        builder.Services.AddSingleton<IDeviceExecChannel>(_deviceChannel);
        builder.Services.AddSingleton<ILocalEndpointProbe>(_probe);

        _options = new MutableOptionsMonitor<LlmGatewayOptions>(new LlmGatewayOptions
        {
            Enabled = true,
            Ocr = new OcrGatewayOptions { Enabled = true },
        });
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_options);

        var dir = Path.Combine(Path.GetTempPath(), "ccs-ocr-gw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:ApiBaseUrl"] = "http://127.0.0.1:18020/v1",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:SupportsImages"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var providers = new LlmProviderRegistry(config);
        builder.Services.AddHttpClient(LocalOcrService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FakeVllm(SuccessJson));
        builder.Services.AddSingleton<LocalOcrService>(sp =>
            new LocalOcrService(
                providers,
                sp.GetRequiredService<ILocalEndpointProbe>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>(),
                sp.GetRequiredService<ILogger<LocalOcrService>>()));

        _device.AddTo(builder.Services);

        _app = builder.Build();
        _app.MapOcrGateway();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _device.Dispose();
    }

    private IssuedTurnToken StartTurn() =>
        _tokens.Issue("owner", "sess-1", _device.Id);

    private HttpRequestMessage Req(IssuedTurnToken t, byte[]? body = null, bool signDevice = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/ocr");
        if (signDevice) _device.Sign(req);
        req.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        req.Content = new ByteArrayContent(body ?? [1, 2, 3]);
        return req;
    }

    // --- тесты ---

    [Fact]
    public async Task БезТокена_401()
    {
        var t = StartTurn();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/ocr");
        _device.Sign(req);
        req.Content = new ByteArrayContent([1, 2, 3]);

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЧужоеУстройство_401()
    {
        var t = StartTurn();
        var (_, otherToken) = _device.Register("owner", "other", GatewayTestDevice.Fingerprint);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/ocr");
        GatewayTestDevice.Sign(req, otherToken, GatewayTestDevice.Fingerprint);
        req.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        req.Content = new ByteArrayContent([1, 2, 3]);

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЗапрещённыеРуки_НетHands_403()
    {
        // Устройство без capability Hands → HandsRefusal → 403.
        _deviceChannel.Status = new DeviceExecStatus(
            DeviceId: _device.Id, DeviceName: "test", Online: true,
            Platform: "windows", AgentVersion: "1.0", CliVersion: "1.0",
            RequiredCliVersion: "1.0",
            Capabilities: [DeviceCapabilities.Exec],
            HarnessReady: true, HarnessProblem: null);

        var t = StartTurn();
        var resp = await _client.SendAsync(Req(t));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("hands_refused");
    }

    [Fact]
    public async Task Успех_200_ПоКонтракту()
    {
        var t = StartTurn();
        var resp = await _client.SendAsync(Req(t));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"text\"");
        body.Should().Contain("\"model\"");
        body.Should().Contain("\"ms\"");
        body.Should().Contain("\"promptTokens\"");
        body.Should().Contain("\"completionTokens\"");
    }

    [Fact]
    public async Task Disabled_503()
    {
        _options.CurrentValue = new LlmGatewayOptions
        {
            Enabled = true,
            Ocr = new OcrGatewayOptions { Enabled = false },
        };
        var t = StartTurn();
        var resp = await _client.SendAsync(Req(t));
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"disabled\"");
    }

    [Fact]
    public async Task LocalUnavailable_503()
    {
        // Проба Down → LocalUnavailable.
        _probe.Outcome = LocalProbeOutcome.Down;

        var t = StartTurn();
        var resp = await _client.SendAsync(Req(t));
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"local_unavailable\"");
    }

    [Fact]
    public async Task BusyTimeout_503()
    {
        // Первый запрос блокируется (SlowVllm) и занимает слот OCR; второй ждёт слот 1 с → BusyTimeout.
        // entered сигналит, когда первый добрался до vLLM (слот занят) — только тогда шлём второй.
        var entered = new TaskCompletionSource();
        await RebuildWithHandlerAsync(new SlowVllm(entered), busyWaitSec: 1);

        var t1 = StartTurn();
        var t2 = StartTurn();

        var first = _client.SendAsync(Req(t1));

        // Первый гарантированно дошёл до SlowVllm, слот точно занят. 5 с — страховка от дедлока.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await _client.SendAsync(Req(t2));
        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await second.Content.ReadAsStringAsync()).Should().Contain("\"busy_timeout\"");
    }

    [Fact]
    public async Task PageTimeout_503()
    {
        // Handler блокируется, PageTimeoutSec = 1 → таймаут. Один запрос — гонки нет, TCS одноразовый.
        await RebuildWithHandlerAsync(new SlowVllm(new TaskCompletionSource()), pageTimeoutSec: 1);

        var t = StartTurn();
        var resp = await _client.SendAsync(Req(t));
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"page_timeout\"");
    }

    [Fact]
    public async Task TooLarge_413()
    {
        var t = StartTurn();
        var bigBody = new byte[LocalOcrService.MaxImageBytes + 1];
        var resp = await _client.SendAsync(Req(t, bigBody));
        resp.StatusCode.Should().Be((HttpStatusCode)413);
    }

    // MAJOR: 413 ДО чтения тела. Content-Length = 8 МБ+1, тело — счётчик байт.
    // Если проверку Content-Length убрать, тело прочитается (счётчик > 0) → тест краснеет.
    [Fact]
    public async Task TooLarge_413_ДоЧтенияТела()
    {
        var t = StartTurn();
        var body = new CountingStream(new byte[LocalOcrService.MaxImageBytes + 1]);

        var ctx = new DefaultHttpContext { RequestServices = _app.Services };
        ctx.Items[typeof(TurnTokenGrant)] = t.Grant;
        ctx.Request.Body = body;
        ctx.Request.ContentLength = LocalOcrService.MaxImageBytes + 1;

        var ocr = _app.Services.GetRequiredService<LocalOcrService>();
        await OcrGatewayEndpoints.HandleAsync(ctx, _sessions, _projects, _deviceChannel, ocr, _options);

        ctx.Response.StatusCode.Should().Be(413);
        body.ReadBytes.Should().Be(0, "тело не читают, пока Content-Length не превышен");
    }

    // MINOR-3: chunked (без Content-Length), тело > 8 МБ. Второй рубеж — лимит накопленного:
    // 413 на превышении, тело не льётся целиком в память (счётчик < всего тела).
    [Fact]
    public async Task Chunked_БезContentLength_БольшеПотолка_413_БезЧтенияЛишнего()
    {
        var t = StartTurn();
        var body = new CountingStream(new byte[16 * 1024 * 1024]); // 16 МБ

        var ctx = new DefaultHttpContext { RequestServices = _app.Services };
        ctx.Items[typeof(TurnTokenGrant)] = t.Grant;
        ctx.Request.Body = body;
        ctx.Request.ContentLength = null; // chunked

        var ocr = _app.Services.GetRequiredService<LocalOcrService>();
        await OcrGatewayEndpoints.HandleAsync(ctx, _sessions, _projects, _deviceChannel, ocr, _options);

        ctx.Response.StatusCode.Should().Be(413);
        // Прочитано не всё тело (16 МБ), а только до потолка + один буфер.
        body.ReadBytes.Should().BeGreaterOrEqualTo(LocalOcrService.MaxImageBytes, "читали до превышения");
        body.ReadBytes.Should().BeLessThan(16 * 1024 * 1024, "тело не льётся целиком в память");
    }

    // MINOR (ревью A3): сайдкар (устройство) и серверный OCR держат один потолок тела.
    // Живёт в ClaudeHomeServer.Tests: только он видит обе сборки (DeviceAgent + Llm).
    // A4: клиент моста (OcrClient) держит тот же потолок — единая константа
    // HandsOcr.MaxImageBytes в Core/Protocol (видна и мосту, и серверу).
    [Fact]
    public void ПотолокСайдкара_СовпадаетСПотолкомOcr()
    {
        SidecarProxy.MaxOcrBodyBytes.Should().Be(LocalOcrService.MaxImageBytes);
        LocalOcrService.MaxImageBytes.Should().Be(HandsOcr.MaxImageBytes);
        SidecarProxy.MaxOcrBodyBytes.Should().Be(HandsOcr.MaxImageBytes);
    }

    // Пересборка app с заданным handler'ом (для BusyTimeout / PageTimeout).
    private async Task RebuildWithHandlerAsync(HttpMessageHandler handler, int? busyWaitSec = null, int? pageTimeoutSec = null)
    {
        await _app.DisposeAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton<ISessionDirectory>(_sessions);
        builder.Services.AddSingleton<IProjectManager>(_projects);
        builder.Services.AddSingleton<IDeviceExecChannel>(_deviceChannel);
        builder.Services.AddSingleton<ILocalEndpointProbe>(_probe);
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_options);

        var dir = Path.Combine(Path.GetTempPath(), "ccs-ocr-gw-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:ApiBaseUrl"] = "http://127.0.0.1:18020/v1",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:SupportsImages"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var providers = new LlmProviderRegistry(config);
        builder.Services.AddHttpClient(LocalOcrService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        builder.Services.AddSingleton<LocalOcrService>(sp =>
            new LocalOcrService(
                providers,
                sp.GetRequiredService<ILocalEndpointProbe>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>(),
                sp.GetRequiredService<ILogger<LocalOcrService>>()));

        _device.AddTo(builder.Services);

        _app = builder.Build();
        _app.MapOcrGateway();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        _options.CurrentValue = new LlmGatewayOptions
        {
            Enabled = true,
            Ocr = new OcrGatewayOptions
            {
                Enabled = true,
                BusyWaitSec = busyWaitSec ?? 120,
                PageTimeoutSec = pageTimeoutSec ?? 60,
            },
        };
    }
}
