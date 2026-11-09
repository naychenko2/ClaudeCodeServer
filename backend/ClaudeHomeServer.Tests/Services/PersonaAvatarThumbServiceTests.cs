using System.Buffers.Binary;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Гонка миниатюры аватара (Перф-3 B10, п.4): два параллельных GetOrGenerateAsync одной
// ступени. Задерживающий растр гарантирует, что оба запроса проходят проверку File.Exists
// ДО создания файла и оба генерируют — детерминированная гонка. Итог: один валидный файл
// (победил последний Move), оба ответа — валидная 64-миниатюра, не оригинал.
public class PersonaAvatarThumbServiceTests
{
    // Растр с задержкой в Apply: два параллельных вызова гарантированно пересекаются
    // в фазе генерации (оба проходят File.Exists до создания файла)
    private sealed class DelayingRaster : IImageRaster
    {
        private readonly IImageRaster _inner;
        private readonly int _delayMs;
        public DelayingRaster(IImageRaster inner, int delayMs) { _inner = inner; _delayMs = delayMs; }
        public RasterProbe? Probe(byte[] data) => _inner.Probe(data);
        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null)
        {
            Thread.Sleep(_delayMs);
            return _inner.Apply(data, ops, encode);
        }
        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => _inner.Encode(data, encode);
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => _inner.ResizeMask(mask, width, height);
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => _inner.EraseMasked(image, mask);
    }

    // Настоящий PNG 100×100 (градиент) — Skia его уменьшит до 64
    private static byte[] RealPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAIAAAD/gAIDAAACmklEQVR4nO2cwU7DMBBEx07//5s5VKUlid1uY3vfopU4RINE5w0kegfiIqmqbCpVZVO9X/9eDAl3350Rji3coripFun+VQ8XUcJFn3jT5o4aYymp3v+yaPzEpaRy00bjhy6ll2cWhJ+7lB7PLAg/eimpvt6G/m3ISymgOnjWiKUOzjUCqYN/jSjqgKgRQh0gNQKoA6RGEV4dIDXK45nFVQdIjec1Vh0gNf6ETHWA1NiHQHWA1DgJaeoAqXEeotQBUqMZctQBUqMXQtSBNUorJKgDbpRW6K4OxFFaoa86+PObQkd1QPCbQi91oPCbQhd1APGbwvXqwOI3hYvVAcdvCleqA5HfFC5TB3/U6+HxNsylmhS72zCX6lHMVgcI6hiKqeoAQR1GMU8dIKgjKSapAwR1MMUMdYCgjqcYrg4Q1CkUY9UBgjqLYqA6QFAnUoxSBwjqXIoh6gBBnU5xXR0gqCsoLqoDBHXR7/uKOkBQFy11vA1zqV74nTpAUJcupa/UAYK6einZ1QGC6rDU8ZmVS/XCz9UBguq2lD5WBwiq51LH2zCX6oVv1QGC6r+U3qkDBBWxlLrqAEGlLHV8ZtFQQUupoQ4QVNZSYvy3coylOrdh6HDWDwe+6ARdSrwXnbhLCfaiE3opkV50oi8lzItOAZbSv1CHdZ8YXR2WfmJodVj9iXHVwaFGUHXwqRFRHdxqhFMHzxqx1MG5RiB18K8RRR0QNUKoA6RGAHWA1CjCqwOkRnk8s7jqAKnxvMaqA6TGn5CpDpAa+xCoDpAaJyFNHSA1zkOUOkBqNEOOOkBq9EKIOrBGaYUEdcCN0grd1YE4SivMk9kMYZ7MZgjzZDZDmCezGcI8mc0Q5slshjBPZjOEeTKbIcyT2QwUP2GvNdcQjiWyAAAAAElFTkSuQmCC");

    // PNG: ширина/высота в IHDR — байты 16..19 и 20..23 (big-endian)
    private static (int W, int H) PngSize(byte[] png) =>
        ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));

    [Fact]
    public async Task GetOrGenerate_ГонкаДвухВызовов_ОдинВалидныйФайлОбеВалидны()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string originalName = "avatar-race.png";
            var originalPath = Path.Combine(dir, originalName);
            File.WriteAllBytes(originalPath, RealPng());

            var persona = new Persona();
            persona.Avatar.Kind = PersonaAvatarKind.Image;
            persona.Avatar.ImageFile = originalName;

            var service = new PersonaAvatarThumbService(new DelayingRaster(new SkiaImageRaster(), 200),
                NullLogger<PersonaAvatarThumbService>.Instance);

            // Два параллельных вызова одной ступени: задержка в Apply гарантирует, что оба
            // проходят File.Exists до создания файла и оба генерируют
            var results = await Task.WhenAll(
                service.GetOrGenerateAsync(persona, originalPath, 64),
                service.GetOrGenerateAsync(persona, originalPath, 64));

            // Оба вернули путь к ОДНОЙ и той же миниатюре (не null, не оригиналу)
            results[0].Should().NotBeNull();
            results[1].Should().Be(results[0]);

            // Файл — валидная 64×64 PNG, не оригинал 100×100
            var thumbPath = results[0]!;
            var thumbBytes = File.ReadAllBytes(thumbPath);
            PngSize(thumbBytes).Should().Be((64, 64));
            thumbBytes.Should().NotBeEquivalentTo(RealPng());

            // В папке только оригинал и одна миниатюра: без осиротевших временных файлов
            Directory.GetFiles(dir).Select(Path.GetFileName)
                .Should().BeEquivalentTo(originalName, Path.GetFileName(thumbPath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }
    }
}
