using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты RemoteReadingSource (Ш2b): фейковый HttpMessageHandler (образец LlamaServerMetricsSourceTests)
/// на недоверенном агенте узла. Любой сбой — статус, исключение наружу не выходит: не-2xx/сеть/
/// тайм-аут/кривое тело → down; v=99 → incompatible; ageMs за порогом ADR (10 с) → stale.
/// Тайм-аут 1,5 с — часовой (TimeProvider.CreateTimer гасит связанный CTS): тест гоняет его на
/// фейковых часах без Task.Delay. Тело — потоковое чтение с потолком 256 КБ + 1 байт. Параллельные вызовы
/// независимы: у источника нет общего изменяемого состояния (буфер тела — локальная переменная).
/// </summary>
public class RemoteReadingSourceTests
{
    private static readonly Uri Base = new("http://10.0.0.5:8080");

    // Минимальный валидный срез v1 (тот же набор обязательных полей, что ValidSliceJson в NodeSliceTests):
    // файл hwmon + ссылка, одна карта nvidia-smi (21 полей), gddr6-обёртка, одна точка statvfs.
    private static string SliceJson(long ageMs = 840, int v = 1) =>
        $$"""
        {"v":{{v}},"ageMs":{{ageMs}},"clockMs":1763283600123,
         "files":{"/sys/class/hwmon/hwmon2/name":"k10temp\n","/proc/meminfo":"MemTotal: 65587456 kB\n"},
         "links":{"/sys/class/hwmon/hwmon4/device":"../../nvme0"},
         "gpuCsv":"0,GPU-abcdef,NVIDIA GeForce RTX 3090,50,40,1000,24576,60,30,1500,1900,9000,P0,4,16,120.5,285.0,Not Active,Not Active,Not Active,00000000:0B:00.0",
         "gddr6":"{\"v\":1,\"at\":1763283600000,\"gpus\":[]}",
         "statvfs":[{"mount":"/","total":1999421571072,"free":812345671680}]}
        """;

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // Фейковый handler: отдаёт тело как 200 OK (или ответ по шаблону — для теста гонки).
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public FakeHandler(string body) : this(_ => Ok(body)) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    private static RemoteReadingSource SourceOf(HttpMessageHandler handler, TimeProvider? time = null) =>
        new(new HttpClient(handler), Base, time ?? TimeProvider.System);

    // ─── c1: статусы недоверенного ответа ─────────────────────────────────────────

    // 200 + валидный срез v1 → ok; содержимое среза доходит до сэмплера без потерь:
    // файлы/ссылки/statvfs — форма SliceFileSource, карты — разбор gpuCsv, gddr6 — текст дословно.
    [Fact]
    public async Task ReadAsync_ValidV1_OkWithSliceContents()
    {
        var reading = await SourceOf(new FakeHandler(SliceJson())).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Ok);
        reading.AgeMs.Should().Be(840);
        reading.Files.ReadText("/sys/class/hwmon/hwmon2/name").Should().Be("k10temp\n");
        reading.Files.LinkTarget("/sys/class/hwmon/hwmon4/device").Should().Be("../../nvme0");
        reading.Files.FsSize("/").Should().Be((1999421571072L, 812345671680L));
        reading.Gddr6.Should().Contain("\"at\"");
        reading.Gpus.Should().ContainSingle().Which.Uuid.Should().Be("GPU-abcdef");
        reading.Gpus[0].Name.Should().Be("NVIDIA GeForce RTX 3090");
        reading.Gpus[0].Temperature.Should().Be(60);
    }

    // 503 («среза ещё нет», ADR-029) и любой не-2xx — down без исключения.
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReadAsync_Non2xx_Down(HttpStatusCode code)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(code) { Content = new StringContent("{}") });

        var reading = await SourceOf(handler).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // Тело 256 КБ + 1 байт — down (поток прочитан с потолком, дальше NodeSliceParser отбраковывает по размеру).
    [Fact]
    public async Task ReadAsync_BodyOver256K_Down()
    {
        var body = new string('x', NodeSliceLimits.MaxBodyBytes + 1);

        var reading = await SourceOf(new FakeHandler(body)).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // v=99 или нет ключа v (версия 0) — не срез принимаемой версии: incompatible
    // (не down: это другой протокол, не отказ узла; тот же разбор, что NodeSliceTests).
    [Theory]
    [InlineData(99)]
    [InlineData(0)] // {} — корень объект, ключа v нет → версия 0 → Incompatible
    public async Task ReadAsync_VersionNotAccepted_Incompatible(int v)
    {
        var body = v == 0 ? "{}" : SliceJson(v: v);

        var reading = await SourceOf(new FakeHandler(body)).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Incompatible);
    }

    // Сеть/коннект (ECONNREFUSED): down без исключения наружу (упавший узел не роняет хост).
    [Fact]
    public async Task ReadAsync_NetworkError_Down()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));

        var reading = await SourceOf(handler).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // ageMs выше порога ADR (10 с = порог локального status:"stale", HardwareSampler.StaleAfter):
    // срез принят, но протух — stale, содержимое при этом целое (сэмплер решит, показывать ли).
    [Fact]
    public async Task ReadAsync_AgeMsOverThreshold_Stale()
    {
        var reading = await SourceOf(new FakeHandler(SliceJson(ageMs: 10_001))).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Stale);
        reading.AgeMs.Should().Be(10_001);
        reading.Files.ReadText("/sys/class/hwmon/hwmon2/name").Should().NotBeNull();
    }

    // Ровно на пороге — ещё не stale (ADR: stale только при ageMs > 10 000, граница принадлежит ok).
    [Fact]
    public async Task ReadAsync_AgeMsExactlyAtThreshold_Ok()
    {
        var reading = await SourceOf(new FakeHandler(SliceJson(ageMs: 10_000))).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Ok);
    }

    // Кривое тело (не JSON / пустое / нет обязательных полей) — down: это не срез, а отказ узла.
    [Theory]
    [InlineData("это не JSON {{{")]
    [InlineData("""{"v":1}""")]
    [InlineData("")]
    public async Task ReadAsync_InvalidBody_Down(string body)
    {
        var reading = await SourceOf(new FakeHandler(body)).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // ─── c2: тайм-аут 1,5 с на фейковых часах ─────────────────────────────────────

    // Фейковый TimeProvider: таймер не заводится на реальных часах — тест держит колбэк
    // и сам «стреляет» им (тайм-аут без реальных 1,5 с и без Task.Delay).
    private sealed class ManualTimerTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new NoopTimer();
        }

        // «Срабатывание» таймера на фейковых часах.
        public void Fire() => _callback!(_state);

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    // Handler висит до отмены токена: ответа нет никогда — живёт только отменой.
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            using var _ = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        }
    }

    // Тайм-аут: висящий ответ отбрасывается отменой, вызов возвращает down и не ждёт сеть.
    // Мутация «убрать catch TaskCanceledException» — краснеет именно этот тест.
    [Fact]
    public async Task ReadAsync_Timeout_DownWithoutThrow()
    {
        var time = new ManualTimerTime();
        var readTask = SourceOf(new HangingHandler(), time).ReadAsync();

        time.Fire(); // таймер тайм-аута на фейковых часах
        var reading = await readTask;

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // Опоздавший таймер: ответ уже пришёл, ReadAsync вернулся и освободил свой CTS, а колбэк
    // тайм-аута, поставленный в очередь пула до Dispose таймера, срабатывает только теперь
    // (ITimer.Dispose не ждёт колбэк в полёте). Колбэк не должен бросать: исключение в потоке
    // таймера — необработанное и гасит весь процесс (инцидент прода 16.11.2026: load 368,
    // голодный пул, ObjectDisposedException из Cancel() → SIGABRT бэкенда).
    // Мутация «Cancel() на освобождённом связанном CTS из своего колбэка» — краснеет здесь.
    [Fact]
    public async Task ReadAsync_TimerFiresAfterReturn_DoesNotThrow()
    {
        var time = new ManualTimerTime();
        var reading = await SourceOf(new FakeHandler(SliceJson()), time).ReadAsync();
        reading.Status.Should().Be(NodeReadingStatus.Ok);

        var fireLate = () => time.Fire(); // колбэк после возврата и Dispose
        fireLate.Should().NotThrow();
    }

    // Отмена ct вызывающего — не сбой узла: OperationCanceledException уходит вызывающему
    // (тот же контракт, что у VllmMetricsSource), а не статус.
    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var readTask = SourceOf(new HangingHandler()).ReadAsync(cts.Token);

        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => readTask);
    }

    // Фаза чтения тела (её в прошлом покрытии не было): заголовки пришли, тело читается.
    // Поток бросает простой OperationCanceledException — так делает отмену базовый
    // Stream.ReadAsync и любые обёртки (GZip/декомпрессия), а не TaskCanceledException.
    // Токен вызывающего цел (отменён только связанный CTS часового таймера) → фильтр-catch
    // не срабатывает → down, исключение наружу не выходит (инвариант ADR-029).
    // Мутация «убрать catch OperationCanceledException» — краснеет именно этот тест.
    [Fact]
    public async Task ReadAsync_TimeoutDuringBodyRead_DownWithoutThrow()
    {
        var time = new ManualTimerTime();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelThrowingStream()),
        });
        var readTask = SourceOf(handler, time).ReadAsync();

        // Чтение тела уже началось (только с ним поток и встретится), теперь стреляет таймер.
        await Task.Delay(30);
        time.Fire();
        var reading = await readTask;

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }

    // Поток тела на одном чтении: ждёт отмены токена и бросает простой OperationCanceledException
    // (не TaskCanceledException) — поведение базового Stream.ReadAsync / обёрток декомпрессии.
    private sealed class CancelThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set { } }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var _ = ct.Register(() => tcs.TrySetResult(true));
            await tcs.Task.ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ─── c3: независимость одновременных вызовов ──────────────────────────────────

    // Сценарный handler: оба ответа — «медленные» (первые половины сразу, вторые — с задержкой),
    // второй стартует на 25 мс позже: чтение тел двух вызовов перекрывается по времени.
    // ageMs стоит в начале JSON (в первой половине), поэтому при shared-буфере первый вызов
    // гарантированно дочитывает чужую первую половину.
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private int _arrived;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _arrived);
            var body = n == 1 ? SliceJson(ageMs: 100) : SliceJson(ageMs: 200);
            var delay = n == 1 ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(25);
            // StreamContent не даёт длины (TryComputeLength=false), поэтому не буферизуется:
            // ReadAsStream отдаёт наш медленный поток напрямую — чтение идёт по его задержкам.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(body), delay)),
            });
        }
    }

    // Два ReadAsync параллельно: общего состояния у источника нет, каждый вызов получает
    // свой срез — даже когда тела читались одновременно.
    // Мутация «буфер тела — поле класса вместо локальной переменной» краснеет здесь:
    // вызовы делят один буфер, первый получает чужую первую половину (ageMs=200 вместо 100).
    [Fact]
    public async Task ParallelCalls_EachGetsOwnResponse()
    {
        var source = SourceOf(new ScriptedHandler());

        var first = source.ReadAsync();
        var second = source.ReadAsync();
        var a = await first;
        var b = await second;

        a.Status.Should().Be(NodeReadingStatus.Ok);
        b.Status.Should().Be(NodeReadingStatus.Ok);
        // Каждый вызов получил свой срез: возраст ответов — по своему ответу (100 и 200).
        new[] { a.AgeMs, b.AgeMs }.Should().BeEquivalentTo(new long[] { 100, 200 },
            "параллельные вызовы не делят буфер: каждый ответ доходит до своего вызова");
    }

    // Поток-половинами: первый Read — первая половина сразу, второй — вторая после delay
    // (сон на потоке: ReadAsync уходит в пул, reading-тред блокается — чтение двух ответов
    // перекрывается по времени детерминированно).
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly TimeSpan _delay;
        private readonly int _half;
        private int _pos;

        public ChunkedStream(byte[] bytes, TimeSpan delay)
        {
            _bytes = bytes;
            _delay = delay;
            _half = bytes.Length / 2;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _pos; set { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _bytes.Length)
                return 0;
            if (_pos == 0)
                return Copy(buffer, offset, count, _half);
            Thread.Sleep(_delay);
            return Copy(buffer, offset, count, _bytes.Length);
        }

        private int Copy(byte[] buffer, int offset, int count, int end)
        {
            var n = Math.Min(Math.Min(count, end - _pos), _bytes.Length - _pos);
            Array.Copy(_bytes, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
