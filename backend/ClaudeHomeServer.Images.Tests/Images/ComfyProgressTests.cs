using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Этап 3 прогресса операций: настоящие шаги семплера по WebSocket ComfyUI. Живого ComfyUI
// на машине нет — события идут записанной последовательностью (формат server.py ComfyUI)
// и через фейковый WS-сервер: обрыв, переподключение, чужой prompt_id, недоступный сокет
public class ComfyProgressTests : IDisposable
{
    private const string Ours = "prompt-ours";
    private const string Foreign = "prompt-foreign";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "comfy_progress_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private static string Progress(string promptId, int value, int max, string node = "ks") =>
        $$$"""{"type":"progress","data":{"value":{{{value}}},"max":{{{max}}},"prompt_id":"{{{promptId}}}","node":"{{{node}}}"}}""";

    private static string Executing(string promptId, string? node) =>
        $$$"""{"type":"executing","data":{"node":{{{(node is null ? "null" : $"\"{node}\"")}}},"prompt_id":"{{{promptId}}}"}}""";

    private static ComfyProgressTracker OursOnly() => new(id => id == Ours);

    // ─── Трекер на записанной последовательности ─────────────────────────────

    [Fact]
    public void Трекер_ЗаписаннаяПоследовательность_ШагиЭтапыИКонец()
    {
        var tracker = OursOnly();
        tracker.Handle("""{"type":"status","data":{"status":{"exec_info":{"queue_remaining":1}}},"sid":"ccs-local-media"}""");
        tracker.Handle($$$"""{"type":"execution_start","data":{"prompt_id":"{{{Ours}}}","timestamp":1}}""");
        tracker.Handle(Executing(Ours, "enc"));
        tracker.Get(Ours).Should().BeNull("до первого progress шагов не знаем — карточка на оценке");

        tracker.Handle(Executing(Ours, "ks"));
        tracker.Handle(Progress(Ours, 1, 20));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(1, 20, 1));
        tracker.Handle(Progress(Ours, 12, 20));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(12, 20, 1));
        tracker.Get(Ours)!.Percent.Should().Be(60);

        // Второй семплер видео: свой счёт шагов, этап 2
        tracker.Handle(Executing(Ours, "ks2"));
        tracker.Handle(Progress(Ours, 2, 8, node: "ks2"));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(2, 8, 2));

        tracker.Handle(Executing(Ours, null));
        tracker.Get(Ours).Should().BeNull("executing с node=null — прогон закончен");
    }

    [Fact]
    public void Трекер_ЧужойPromptId_НеКопитсяИНашНеТрогает()
    {
        var tracker = OursOnly();
        tracker.Handle(Progress(Ours, 4, 20));

        tracker.Handle(Progress(Foreign, 19, 20));
        tracker.Handle(Progress(Foreign, 3, 30, node: "other"));
        tracker.Get(Foreign).Should().BeNull("чужие прогоны стенда не копятся");

        tracker.Handle(Executing(Foreign, null));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(4, 20, 1), "конец чужого прогона наш не снимает");
    }

    [Fact]
    public void Трекер_ОшибкаИПрерывание_СнимаютЗапись_МусорНеРоняет()
    {
        var tracker = OursOnly();
        foreach (var end in new[] { "execution_error", "execution_interrupted", "execution_success" })
        {
            tracker.Handle(Progress(Ours, 5, 20));
            tracker.Handle($$$"""{"type":"{{{end}}}","data":{"prompt_id":"{{{Ours}}}"}}""");
            tracker.Get(Ours).Should().BeNull(end);
        }

        tracker.Handle("не json");
        tracker.Handle("[1,2]");
        tracker.Handle("""{"type":"progress"}""");
        tracker.Handle(Progress(Ours, 3, 0));
        tracker.Handle(Progress(Ours, -1, 20));
        tracker.Get(Ours).Should().BeNull("несостоятельные значения не дают шагов");

        tracker.Handle(Progress(Ours, 25, 20));
        tracker.Get(Ours)!.Step.Should().Be(20, "шаг не больше всего");
        tracker.Get(Ours)!.Percent.Should().Be(99, "«готово» говорит только история");
    }

    [Fact]
    public void Трекер_ЭкзотическийJson_НеРветСессию()
    {
        var tracker = OursOnly();
        tracker.Handle(Progress(Ours, 4, 20));

        // Дубли ключей JsonObject отвергает исключением не из семейства JsonException
        var act = () => tracker.Handle($$$"""{"type":"progress","type":"progress","data":{"prompt_id":"{{{Ours}}}"}}""");

        act.Should().NotThrow("экзотика чужого формата пропускается, а не рвёт сокет со всеми шагами");
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(4, 20, 1));
    }

    // Сокет жив, но молчит (ComfyUI забыл его без Close): замёрзшие шаги не выдаются за точные
    [Fact]
    public void Трекер_ПрогрессМолчитДольшеСрока_ЗаписьСнимается()
    {
        var time = new ManualTime();
        var tracker = new ComfyProgressTracker(id => id == Ours, time);
        tracker.Handle(Progress(Ours, 4, 20));

        time.Advance(ComfyProgressTracker.StaleAfter);
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(4, 20, 1), "на самой границе срока ещё верим");

        time.Advance(TimeSpan.FromSeconds(1));
        tracker.Get(Ours).Should().BeNull("progress молчит дольше срока — прогресс уходит в оценку");

        tracker.Handle(Progress(Ours, 5, 20));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(5, 20, 1), "заговорил снова — снова точные шаги");
    }

    // Шаг видеосемплера идёт 30–100 с: порог подстраивается под темп, и полоса не мигает
    // точная ↔ оценка на каждом шаге, а этап не сбрасывается
    [Fact]
    public void Трекер_ШагиРазВ40Секунд_ПолосаОстаётсяТочной()
    {
        var time = new ManualTime();
        var tracker = new ComfyProgressTracker(id => id == Ours, time);
        var step = TimeSpan.FromSeconds(40);

        tracker.Handle(Progress(Ours, 1, 8));
        // До второго шага темпа не знаем — после 15 с честнее оценка
        time.Advance(TimeSpan.FromSeconds(20));
        tracker.Get(Ours).Should().BeNull();
        time.Advance(step - TimeSpan.FromSeconds(20));

        for (var value = 2; value <= 8; value++)
        {
            tracker.Handle(Progress(Ours, value, 8));
            time.Advance(step - TimeSpan.FromSeconds(1));
            tracker.Get(Ours).Should().Be(new ComfyStepProgress(value, 8, 1), $"шаг {value}: темп известен, полоса точная");
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    // VAE-декод между семплерами молчит дольше порога: полоса временно на оценке, но второй
    // семплер остаётся вторым этапом, а не выдаётся за первый
    [Fact]
    public void Трекер_ПаузаМеждуНодамиДольшеСрока_ВторойЭтапОстаётсяВторым()
    {
        var time = new ManualTime();
        var tracker = new ComfyProgressTracker(id => id == Ours, time);
        for (var value = 1; value <= 20; value++)
        {
            tracker.Handle(Progress(Ours, value, 20));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        time.Advance(TimeSpan.FromSeconds(30));
        tracker.Get(Ours).Should().BeNull("между нодами progress молчит — карточка на оценке");

        tracker.Handle(Progress(Ours, 1, 8, node: "ks2"));
        tracker.Get(Ours).Should().Be(new ComfyStepProgress(1, 8, 2));
    }

    // Вытеснение посреди медленного прогона: сокет молчит дольше адаптивного порога — откат на оценку
    [Fact]
    public void Трекер_МолчитДольшеАдаптивногоПорога_УходитВОценку()
    {
        var time = new ManualTime();
        var tracker = new ComfyProgressTracker(id => id == Ours, time);
        var step = TimeSpan.FromSeconds(40);
        tracker.Handle(Progress(Ours, 1, 8));
        time.Advance(step);
        tracker.Handle(Progress(Ours, 2, 8));

        time.Advance(step * 3);
        tracker.Get(Ours).Should().NotBeNull("три интервала — ещё в пределах порога");

        time.Advance(TimeSpan.FromSeconds(1));
        tracker.Get(Ours).Should().BeNull("молчит дольше трёх интервалов — замёрзшие шаги не выдаются за точные");
    }

    [Fact]
    public void Адрес_СокетаТогоЖеКлиентаЧтоИПостановка()
    {
        ComfyProgressListener.SocketUri("http://127.0.0.1:8188/").ToString()
            .Should().Be("ws://127.0.0.1:8188/ws?clientId=" + ComfyClient.ClientId);
        ComfyProgressListener.SocketUri("https://comfy.local").Scheme.Should().Be("wss");
        // ComfyUI держит на sid один сокет: общий id дева и боя на одном стенде вытеснял бы
        // сокет соседа без Close
        ComfyClient.ClientId.Should().MatchRegex("^ccs-local-media-[0-9a-f]{32}$",
            "clientId свой у каждого процесса");
    }

    // ─── Слушатель на фейковом WS-сервере ────────────────────────────────────

    private (ComfyProgressListener Listener, ComfyProgressTracker Tracker, LocalMediaJobStore Store, ListLogger Log)
        BuildListener(string comfyUrl, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null, bool withJob = true,
            TimeProvider? time = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = comfyUrl,
        }).Build();
        var store = new LocalMediaJobStore(config);
        if (withJob)
            store.Add(new LocalMediaJob { Id = "lm_" + new string('a', 32), OwnerId = "o", PromptId = Ours, Status = LocalMediaStatuses.Running });
        var tracker = new ComfyProgressTracker(store, time);
        var log = new ListLogger();
        var listener = new ComfyProgressListener(tracker, store, config, log)
        {
            IdleDelay = TimeSpan.FromMilliseconds(50),
            MinRetryDelay = TimeSpan.FromMilliseconds(20),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        };
        // Без подмены — настоящий ClientWebSocket к фейковому серверу
        if (connect is not null) listener.Connect = connect;
        return (listener, tracker, store, log);
    }

    // Ждать состояния трекера по его событию, а не сном
    private static async Task WaitFor(ComfyProgressTracker tracker, Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (condition()) done.TrySetResult(); }
        tracker.Changed += Check;
        try
        {
            Check();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            tracker.Changed -= Check;
        }
    }

    [Fact]
    public async Task Слушатель_ШагиПоСокету_ПревьюНеМешает_ClientIdПостановки()
    {
        await using var server = await FakeComfySocket.StartAsync();
        var (listener, tracker, _, log) = BuildListener(server.Url);
        await listener.StartAsync(default);
        try
        {
            var conn = await server.NextAsync();
            conn.ClientId.Should().Be(ComfyClient.ClientId);

            await conn.SendBinaryAsync(new byte[4096]); // превью latent2rgb
            await conn.SendTextAsync(Progress(Foreign, 9, 10));
            await conn.SendTextAsync(Progress(Ours, 6, 30));

            await WaitFor(tracker, () => tracker.Get(Ours) is not null);
            tracker.Get(Ours).Should().Be(new ComfyStepProgress(6, 30, 1));
            tracker.Get(Foreign).Should().BeNull();
        }
        finally
        {
            await listener.StopAsync(default);
        }
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Слушатель_Обрыв_ЗнаниеСбрасывается_ПереподключаетсяИСноваСлышит()
    {
        await using var server = await FakeComfySocket.StartAsync();
        var (listener, tracker, _, log) = BuildListener(server.Url);
        await listener.StartAsync(default);
        try
        {
            var first = await server.NextAsync();
            await first.SendTextAsync(Progress(Ours, 10, 20));
            await WaitFor(tracker, () => tracker.Get(Ours) is not null);

            first.Abort();
            await WaitFor(tracker, () => tracker.Get(Ours) is null);

            var second = await server.NextAsync();
            await second.SendTextAsync(Progress(Ours, 11, 20));
            await WaitFor(tracker, () => tracker.Get(Ours) is not null);
            tracker.Get(Ours).Should().Be(new ComfyStepProgress(11, 20, 1));
        }
        finally
        {
            await listener.StopAsync(default);
        }
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "обрыв — штатное событие опциональной зависимости");
        listener.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Слушатель_СерверЗакрылСокет_Переподключается()
    {
        await using var server = await FakeComfySocket.StartAsync();
        var (listener, tracker, _, _) = BuildListener(server.Url);
        await listener.StartAsync(default);
        try
        {
            var first = await server.NextAsync();
            await first.SendTextAsync(Progress(Ours, 1, 4));
            await WaitFor(tracker, () => tracker.Get(Ours) is not null);
            await first.CloseAsync();

            var second = await server.NextAsync();
            second.ClientId.Should().Be(ComfyClient.ClientId);
        }
        finally
        {
            await listener.StopAsync(default);
        }
    }

    // Вытеснение: ComfyUI забыл наш сокет без Close — связь жива, но progress больше не идёт.
    // Сессия не рвётся, сброса нет, и только сторож трекера уводит прогресс в оценку
    [Fact]
    public async Task Слушатель_СокетЖивНоМолчит_ПрогрессУходитВОценку()
    {
        var time = new ManualTime();
        await using var server = await FakeComfySocket.StartAsync();
        var (listener, tracker, _, _) = BuildListener(server.Url, time: time);
        await listener.StartAsync(default);
        try
        {
            var conn = await server.NextAsync();
            await conn.SendTextAsync(Progress(Ours, 12, 30));
            await WaitFor(tracker, () => tracker.Get(Ours) is not null);

            time.Advance(ComfyProgressTracker.StaleAfter + TimeSpan.FromSeconds(1));

            tracker.Get(Ours).Should().BeNull("замёрзшие шаги не выдаются за точные — карточка на оценке");
            conn.ClientGone.IsCompleted.Should().BeFalse("сокет жив: откат делает сторож, а не обрыв");
        }
        finally
        {
            await listener.StopAsync(default);
        }
    }

    [Fact]
    public async Task Слушатель_ЗадачиКончились_СокетЗакрывается()
    {
        await using var server = await FakeComfySocket.StartAsync();
        var (listener, _, store, _) = BuildListener(server.Url);
        await listener.StartAsync(default);
        try
        {
            var conn = await server.NextAsync();
            store.Update("lm_" + new string('a', 32), "o", j => j.Status = LocalMediaStatuses.Completed);

            await conn.ClientGone.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await listener.StopAsync(default);
        }
    }

    [Fact]
    public async Task Слушатель_ComfyUIНедоступен_ТихиеПовторы_ОпросНаОценке()
    {
        var attempts = 0;
        var thirdAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (listener, tracker, _, log) = BuildListener("http://127.0.0.1:1", (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) >= 3) thirdAttempt.TrySetResult();
            throw new WebSocketException("connection refused");
        });

        await listener.StartAsync(default);
        await thirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await listener.StopAsync(default);

        listener.ExecuteTask!.IsFaulted.Should().BeFalse("недоступный сокет не роняет слушателя");
        tracker.Get(Ours).Should().BeNull("шагов нет — прогресс карточки остаётся на оценке по ETA");
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "выключенный ComfyUI — без Error-спама");
    }

    [Fact]
    public async Task Слушатель_НетЗадач_НеПодключается()
    {
        var attempts = 0;
        var (listener, tracker, store, _) = BuildListener("http://127.0.0.1:1", (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new WebSocketException("connection refused");
        }, withJob: false);
        await listener.StartAsync(default);
        var firstReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Changed += () => firstReset.TrySetResult();
        // «Ничего не произошло» событием не дождёшься: несколько холостых проходов цикла
        // (IdleDelay 50 мс). Ложного падения сон не даёт — в худшем случае пропуск
        await Task.Delay(300);
        Volatile.Read(ref attempts).Should().Be(0, "незачем держать сокет к GPU-стенду, пока нечего ждать");

        store.Add(new LocalMediaJob { Id = "lm_" + new string('b', 32), OwnerId = "o", PromptId = Ours, Status = LocalMediaStatuses.Queued });
        await firstReset.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await listener.StopAsync(default);

        Volatile.Read(ref attempts).Should().BeGreaterThan(0, "задача появилась — слушатель подключается");
    }

    // ─── Фейковый WebSocket ComfyUI ──────────────────────────────────────────

    private sealed class FakeComfySocket : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<Connection> _connections = Channel.CreateUnbounded<Connection>();

        private FakeComfySocket(WebApplication app) => _app = app;

        public string Url { get; private set; } = "";

        public static async Task<FakeComfySocket> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            // Ни appsettings из каталога тестов, ни переменных окружения: оттуда приезжали
            // Kestrel-эндпоинты продукта (HTTPS), и сервер не стартовал
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var server = new FakeComfySocket(app);
            app.UseWebSockets();
            app.Map("/ws", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                var conn = new Connection(socket, ctx.Request.Query["clientId"]);
                server._connections.Writer.TryWrite(conn);
                await conn.RunAsync();
            });
            await app.StartAsync();
            server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return server;
        }

        public async Task<Connection> NextAsync() =>
            await _connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class Connection(WebSocket socket, string? clientId)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _clientGone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _send = new(1, 1);

        public string? ClientId => clientId;

        // Клиент закрыл сокет или связь пропала
        public Task ClientGone => _clientGone.Task;

        // Держит запрос открытым и слушает клиента, пока тест не оборвёт или не закроет связь
        public async Task RunAsync()
        {
            var buffer = new byte[1024];
            var receive = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(buffer, default);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch (Exception) { /* связь оборвана */ }
                _clientGone.TrySetResult();
            });
            await Task.WhenAny(_released.Task, receive);
        }

        public Task SendTextAsync(string text) => SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

        public Task SendBinaryAsync(byte[] bytes) => SendAsync(bytes, WebSocketMessageType.Binary);

        private async Task SendAsync(byte[] bytes, WebSocketMessageType type)
        {
            await _send.WaitAsync();
            try
            {
                await socket.SendAsync(bytes, type, endOfMessage: true, default);
            }
            finally
            {
                _send.Release();
            }
        }

        // Обрыв без закрывающего рукопожатия — как падение процесса ComfyUI
        public void Abort()
        {
            socket.Abort();
            _released.TrySetResult();
        }

        public async Task CloseAsync()
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
            _released.TrySetResult();
        }
    }

    // Управляемые часы для сторожа трекера: время двигает тест, а не сон
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ListLogger : ILogger<ComfyProgressListener>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
