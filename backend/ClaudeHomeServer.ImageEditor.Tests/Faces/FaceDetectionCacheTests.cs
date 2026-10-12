using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Faces;

// Кэш детекции лиц (ADR-027, §4, этап 2): LRU в памяти, single-flight на параллельные запросы,
// в кэш — только успешные результаты (без Error и без Busy), facesKey детерминирован по байтам.
public class FaceDetectionCacheTests
{
    private const string Owner = "u";

    private static LocalFaceDetectResult Ok(int faces = 1) =>
        new(100, 100, Enumerable.Range(1, faces).Select(n => new LocalDetectedFace(n, 10, 10, 50, 50, 0.9, false)).ToList());

    private static LocalFaceDetectResult Busy() => new(0, 0, [], null, true);
    private static LocalFaceDetectResult Err() => new(0, 0, [], "Сбой");

    // Верхний таймаут ожидания отмены вызывающего: сломанный WaitAsync(ct) оставил бы детекцию
    // висячей, и ожидание OCE зависло бы в CI. Рабочий код бросает OCE мгновенно (миллисекунды);
    // таймаут — страховка, чтобы поломка давала красный тест, а не висящий
    private static async Task WaitForCancellationAsync(Task task)
    {
        var winner = await Task.WhenAny(task, Task.Delay(2000));
        winner.Should().Be(task, "отмена вызывающего даёт OCE до таймаута, а не зависание");
        await FluentActions.Invoking(() => task).Should().ThrowAsync<OperationCanceledException>();
    }

    // Детектор-счётчик: возвращает результат из делегата, считает вызовы
    private sealed class CountingDetector
    {
        private int _calls;
        public int Calls => _calls;
        private readonly Func<byte[], LocalFaceDetectResult> _result;
        public CountingDetector(Func<byte[], LocalFaceDetectResult> result) => _result = result;
        public Task<LocalFaceDetectResult> Detect(byte[] image)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_result(image));
        }
    }

    // Детектор по сценарию: n-й вызов даёт задачу из скрипта (сбой / отмена / успех). Считает вызовы
    private sealed class ScriptedDetector
    {
        private int _calls;
        public int Calls => _calls;
        private readonly Func<int, Task<LocalFaceDetectResult>> _script;
        public ScriptedDetector(Func<int, Task<LocalFaceDetectResult>> script) => _script = script;
        public Task<LocalFaceDetectResult> Detect(byte[] image) => _script(Interlocked.Increment(ref _calls));
    }

    // Первый вызов — сбой (faulted), далее — успех
    private static ScriptedDetector FaultThenOk() =>
        new(n => n == 1
            ? Task.FromException<LocalFaceDetectResult>(new InvalidOperationException("сбой детектора"))
            : Task.FromResult(Ok()));

    // Первый вызов — отмена (canceled), далее — успех. Токен non-None: Task.FromCanceled(None)
    // бросает ArgumentOutOfRangeException синхронно, а не возвращает canceled-задачу
    private static ScriptedDetector CancelThenOk() =>
        new(n => n == 1
            ? Task.FromCanceled<LocalFaceDetectResult>(new CancellationToken(true))
            : Task.FromResult(Ok()));

    // Повторный запрос на те же байты берётся из кэша: детектор вызван один раз, результат тот же
    [Fact]
    public async Task Повтор_на_те_же_байты_детектор_один_раз()
    {
        var detector = new CountingDetector(_ => Ok());
        var cache = new FaceDetectionCache();

        var first = await cache.GetOrDetectAsync(Owner, [1, 2, 3], () => detector.Detect([1, 2, 3]));
        var second = await cache.GetOrDetectAsync(Owner, [1, 2, 3], () => detector.Detect([1, 2, 3]));

        detector.Calls.Should().Be(1);
        second.Should().BeSameAs(first);
    }

    // Другие байты — другой ключ: детектор вызывается повторно
    [Fact]
    public async Task Другие_байты_второй_вызов_детектора()
    {
        var detector = new CountingDetector(_ => Ok());
        var cache = new FaceDetectionCache();

        await cache.GetOrDetectAsync(Owner, [1, 2, 3], () => detector.Detect([1, 2, 3]));
        await cache.GetOrDetectAsync(Owner, [4, 5, 6], () => detector.Detect([4, 5, 6]));

        detector.Calls.Should().Be(2);
    }

    // Busy не кэшируется: повторный запрос запускает детектор снова (в кэш кладутся только успехи)
    [Fact]
    public async Task Busy_не_кэшируется()
    {
        var detector = new CountingDetector(_ => Busy());
        var cache = new FaceDetectionCache();

        var first = await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));

        first.Busy.Should().BeTrue();
        detector.Calls.Should().Be(2, "Busy не кэшируется — повторный запрос запускает детектор снова");
    }

    // Error не кэшируется: как и Busy, повторный запрос запускает детектор снова
    [Fact]
    public async Task Error_не_кэшируется()
    {
        var detector = new CountingDetector(_ => Err());
        var cache = new FaceDetectionCache();

        var first = await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));

        first.Error.Should().NotBeNull();
        detector.Calls.Should().Be(2, "Error не кэшируется — повторный запрос запускает детектор снова");
    }

    // LRU: при переполнении ёмкости самые старые записи вытесняются, новые — в кэше
    [Fact]
    public async Task LRU_вытесняет_старые_записи()
    {
        var detector = new CountingDetector(_ => Ok());
        var cache = new FaceDetectionCache(capacity: 2);

        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        await cache.GetOrDetectAsync(Owner, [2], () => detector.Detect([2]));
        await cache.GetOrDetectAsync(Owner, [3], () => detector.Detect([3]));
        // [1] вытеснена: повторный запрос на неё запускает детектор
        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));

        detector.Calls.Should().Be(4, "первая запись вытеснена LRU и перечитана");
    }

    // Single-flight: два параллельных запроса на один ключ — один прогон детектора
    [Fact]
    public async Task Параллельные_запросы_один_детектор()
    {
        var detector = new CountingDetector(_ => Ok());
        var cache = new FaceDetectionCache();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrDetectAsync(Owner, [7], () => detector.Detect([7])))
            .ToList();
        var results = await Task.WhenAll(tasks);

        detector.Calls.Should().Be(1, "single-flight: все параллельные запросы ждут один прогон");
        results.Should().AllBeEquivalentTo(results[0]);
    }

    // Сбой детектора не залипает: следующий вызов на те же байты запускает детектор ВТОРОЙ раз
    // (calls = 2) и успешно кэширует (третий вызов — calls остаётся 2)
    [Fact]
    public async Task Сбой_детектора_не_залипает()
    {
        var detector = FaultThenOk();
        var cache = new FaceDetectionCache();

        // Первый вызов: детектор упал — исходное исключение пробрасывается, запись из кэша удалена
        await FluentActions.Invoking(() => cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1])))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("сбой детектора");

        // Второй вызов: детектор запускается ВТОРОЙ раз (calls = 2) и успешно кэширует
        var second = await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        detector.Calls.Should().Be(2, "после сбоя детектор запускается заново");
        second.Faces.Should().NotBeEmpty();

        // Третий вызов: кэш, детектор не вызывается (calls остаётся 2)
        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        detector.Calls.Should().Be(2, "успех после сбоя кэшируется");
    }

    // Отмена детектора не залипает: как и сбой, следующий вызов запускает детектор заново и кэширует
    [Fact]
    public async Task Отмена_детектора_не_залипает()
    {
        var detector = CancelThenOk();
        var cache = new FaceDetectionCache();

        // Первый вызов: задача детектора завершена Canceled — TaskCanceledException, запись удалена
        await FluentActions.Invoking(() => cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1])))
            .Should().ThrowAsync<TaskCanceledException>();

        // Второй вызов: детектор запускается ВТОРОЙ раз (calls = 2) и успешно кэширует
        var second = await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        detector.Calls.Should().Be(2, "после отмены детектор запускается заново");
        second.Faces.Should().NotBeEmpty();

        // Третий вызов: кэш, calls остаётся 2
        await cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]));
        detector.Calls.Should().Be(2, "успех после отмены кэшируется");
    }

    // Второй ожидающий single-flight при сбое получает исключение, но запись не залипает:
    // следующий (третий) вызов запускает детектор заново. Первый прогон «висит» (TaskCompletionSource),
    // чтобы continuation не успел удалить запись до того, как второй вызов подвяжется на ту же задачу
    [Fact]
    public async Task Второй_ожидающий_при_сбое_не_залипает()
    {
        var tcs = new TaskCompletionSource<LocalFaceDetectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector(n => n == 1 ? tcs.Task : Task.FromResult(Ok()));
        var cache = new FaceDetectionCache();

        // Первый и второй вызовы параллельно на один ключ: single-flight, один прогон детектора (calls = 1)
        var first = cache.GetOrDetectAsync(Owner, [9], () => detector.Detect([9]));
        var second = cache.GetOrDetectAsync(Owner, [9], () => detector.Detect([9]));
        detector.Calls.Should().Be(1, "single-flight: один прогон детектора");

        // Первый прогон завершается сбоем: оба ожидающие получают исключение
        tcs.SetException(new InvalidOperationException("сбой детектора"));
        await FluentActions.Invoking(() => first).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => second).Should().ThrowAsync<InvalidOperationException>();

        // Третий вызов: запись после сбоя удалена — детектор запускается ВТОРОЙ раз (calls = 2)
        var third = await cache.GetOrDetectAsync(Owner, [9], () => detector.Detect([9]));
        detector.Calls.Should().Be(2, "после сбоя запись не залипает — детектор запускается заново");
        third.Faces.Should().NotBeEmpty();
    }

    // Отмена ct вызывающего во время долгой детекции: OperationCanceledException немедленно,
    // детектор НЕ отменяется (продолжает жить для остальных ожидающих)
    [Fact]
    public async Task Отмена_вызывающего_не_останавливает_детекцию()
    {
        var tcs = new TaskCompletionSource<LocalFaceDetectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector(_ => tcs.Task);
        var cache = new FaceDetectionCache();
        var cts = new CancellationTokenSource();

        // Первый вызывающий: стартует детекцию, затем отменяет свой токен
        var first = cache.GetOrDetectAsync(Owner, [1], () => detector.Detect([1]), cts.Token);
        detector.Calls.Should().Be(1, "детектор запущен");

        cts.Cancel();
        await WaitForCancellationAsync(first);

        // Детектор не отменён: задача всё ещё висячая, вызовов ровно 1
        detector.Calls.Should().Be(1);
        tcs.Task.IsCompleted.Should().BeFalse("детекция продолжает жить после отмены вызывающего");
    }

    // Второй вызывающий на тот же ключ после отмены первого: single-flight, детектор вызван один раз,
    // результат приходит после завершения детекции
    [Fact]
    public async Task Второй_вызывающий_после_отмены_первого_получает_результат()
    {
        var tcs = new TaskCompletionSource<LocalFaceDetectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector(_ => tcs.Task);
        var cache = new FaceDetectionCache();
        var cts = new CancellationTokenSource();

        // Первый: стартует, отменяется
        var first = cache.GetOrDetectAsync(Owner, [2], () => detector.Detect([2]), cts.Token);
        cts.Cancel();
        await WaitForCancellationAsync(first);

        // Второй: single-flight на тот же ключ, ждёт ту же задачу
        var second = cache.GetOrDetectAsync(Owner, [2], () => detector.Detect([2]));
        detector.Calls.Should().Be(1, "single-flight: детектор вызван ровно один раз");

        // Детекция завершается: второй получает результат
        tcs.SetResult(Ok());
        var result = await second;
        result.Faces.Should().NotBeEmpty();
    }

    // После завершения детекции результат в кэше: третий вызов не запускает детектор
    [Fact]
    public async Task После_отмены_вызывающего_результат_кэшируется()
    {
        var tcs = new TaskCompletionSource<LocalFaceDetectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector(_ => tcs.Task);
        var cache = new FaceDetectionCache();
        var cts = new CancellationTokenSource();

        // Первый: стартует, отменяется
        var first = cache.GetOrDetectAsync(Owner, [3], () => detector.Detect([3]), cts.Token);
        cts.Cancel();
        await WaitForCancellationAsync(first);

        // Детекция завершается успешно
        tcs.SetResult(Ok());

        // Третий вызов: результат уже в кэше, детектор не вызывается повторно
        var third = await cache.GetOrDetectAsync(Owner, [3], () => detector.Detect([3]));
        detector.Calls.Should().Be(1, "результат в кэше — детектор не запускается повторно");
        third.Faces.Should().NotBeEmpty();
    }

    // facesKey детерминирован: те же байты — тот же ключ, другие — другой; формат «<16 hex>:v1»
    [Fact]
    public void FacesKey_детерминирован_по_байтам()
    {
        var a = FaceDetectionCache.FacesKey([1, 2, 3]);
        var b = FaceDetectionCache.FacesKey([1, 2, 3]);
        var c = FaceDetectionCache.FacesKey([4, 5, 6]);

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().Match("*:v1");
        a[..a.IndexOf(':')].Should().HaveLength(16, "префикс 16 hex sha256");
    }
}
