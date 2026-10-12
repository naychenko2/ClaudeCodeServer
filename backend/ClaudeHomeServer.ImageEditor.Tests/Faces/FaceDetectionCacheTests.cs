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
