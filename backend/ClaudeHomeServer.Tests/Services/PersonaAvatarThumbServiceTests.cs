using System.Buffers.Binary;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Гонка миниатюры аватара: два параллельных GetOrGenerateAsync одной
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

    // Заголовок PNG — 8 байтов сигнатуры: по нему усечённый файл выглядит валидным
    private static ReadOnlySpan<byte> PngSignature =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // Хвост PNG — чанк IEND: длина 0, «IEND», CRC. Его отсутствие = файл усечён
    private static ReadOnlySpan<byte> PngEnd =>
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

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

            var service = new PersonaAvatarThumbService(
                NullLogger<PersonaAvatarThumbService>.Instance, new DelayingRaster(new SkiaImageRaster(), 200));

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

    // Ранний выход «растра нет» стоял ДО проверки файла на диске, и
    // кэшированная миниатюра на выключенной вертикали images не отдавалась (вызывающий
    // отдавал оригинал). Файл миниатюры валиден без растра: он уже был создан, когда
    // вертикаль была включена — повторной генерации не требует.
    [Fact]
    public async Task GetOrGenerate_РастраНетМиниатюраНаДиске_ОтдаётКэшированныйФайл()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-cached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string originalName = "avatar-cached.png";
            var originalPath = Path.Combine(dir, originalName);
            File.WriteAllBytes(originalPath, RealPng());

            // Миниатюра 64 записана на диске «из прошлого» — пока вертикаль была включена
            var thumbPath = Path.Combine(dir, "avatar-cached-64.png");
            var shrunk = new SkiaImageRaster().Apply(File.ReadAllBytes(originalPath), [new ResizeOp(Width: 64)], null);
            shrunk.Ok.Should().BeTrue();
            File.WriteAllBytes(thumbPath, shrunk.Image!.Bytes);

            var persona = new Persona();
            persona.Avatar.Kind = PersonaAvatarKind.Image;
            persona.Avatar.ImageFile = originalName;

            // Растра нет — так сервис стоит на выключенной вертикали images (параметр null)
            var service = new PersonaAvatarThumbService(
                NullLogger<PersonaAvatarThumbService>.Instance, raster: null);

            var result = await service.GetOrGenerateAsync(persona, originalPath, 64);

            // Это путь к миниатюре на диске, а не null и не оригинал
            result.Should().Be(thumbPath);
            var bytes = File.ReadAllBytes(result!);
            PngSize(bytes).Should().Be((64, 64));
            bytes.Should().NotBeEquivalentTo(RealPng());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }
    }

    // Обратная сторона той же починки: файла нет и растра нет — «миниатюры нет», вызывающий
    // отдаёт оригинал (тот же контракт, что «растр не смог»). Ранний выход остаётся,
    // но только когда файла действительно нет.
    [Fact]
    public async Task GetOrGenerate_РастраНетФайлаМиниатюрыНет_ВозвращаетNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-noraster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string originalName = "avatar-noraster.png";
            var originalPath = Path.Combine(dir, originalName);
            File.WriteAllBytes(originalPath, RealPng());

            var persona = new Persona();
            persona.Avatar.Kind = PersonaAvatarKind.Image;
            persona.Avatar.ImageFile = originalName;

            var service = new PersonaAvatarThumbService(
                NullLogger<PersonaAvatarThumbService>.Instance, raster: null);

            (await service.GetOrGenerateAsync(persona, originalPath, 64)).Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }
    }

    // Инвариант отдачи кэша — «файл миниатюры считается валидным
    // по факту существования», содержимое при отдаче не проверяется (PersonaAvatarThumbService).
    // Целостность файла гарантирует схема записи (tmp + File.Move — единственный писатель
    // с первого коммита 38c3a8650), а не проверка при отдаче; тот же контракт, что у оригинала:
    // он отдаётся по одному File.Exists (PersonasController.Avatar). Проба на усечённом
    // файле (первые байты валидной 64×64: заголовок PNG цел, хвоста IEND нет) документирует
    // поведение как есть: кэш отдаётся как есть на обеих вертикалях — не перегенерируется,
    // не «чинится» фолбэком на оригинал. Появится неатомичный писатель миниатюр —
    // этот тест покраснеет первым и потребует проверки содержимого.
    [Fact]
    public async Task GetOrGenerate_УсечённыйФайлНаДиске_ОтдаётсяКакЕсть_Инвариант()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avatar-trunc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string originalName = "avatar-trunc.png";
            var originalPath = Path.Combine(dir, originalName);
            File.WriteAllBytes(originalPath, RealPng());

            // Валидная 64-миниатюра и её усечённая копия: заголовок PNG цел, хвост IEND отрезан
            var shrunk = new SkiaImageRaster().Apply(File.ReadAllBytes(originalPath), [new ResizeOp(Width: 64)], null);
            shrunk.Ok.Should().BeTrue();
            var fullThumb = shrunk.Image!.Bytes;
            fullThumb.AsSpan().EndsWith(PngEnd).Should().BeTrue();
            var truncated = fullThumb[..(fullThumb.Length / 2)];
            truncated.AsSpan().StartsWith(PngSignature).Should().BeTrue();
            truncated.AsSpan().EndsWith(PngEnd).Should().BeFalse();

            var thumbPath = Path.Combine(dir, "avatar-trunc-64.png");
            File.WriteAllBytes(thumbPath, truncated);

            var persona = new Persona();
            persona.Avatar.Kind = PersonaAvatarKind.Image;
            persona.Avatar.ImageFile = originalName;

            // Вертикаль включена: битый кэш отдаётся как есть — файла касаются, не генеряют
            var withRaster = new PersonaAvatarThumbService(
                NullLogger<PersonaAvatarThumbService>.Instance, new SkiaImageRaster());
            var result = await withRaster.GetOrGenerateAsync(persona, originalPath, 64);
            result.Should().Be(thumbPath);
            File.ReadAllBytes(result!).Should().Equal(truncated);

            // Вертикаль выключена: то же поведение — тот же ранний выход до проверки растра
            var noRaster = new PersonaAvatarThumbService(
                NullLogger<PersonaAvatarThumbService>.Instance, raster: null);
            (await noRaster.GetOrGenerateAsync(persona, originalPath, 64)).Should().Be(thumbPath);
            File.ReadAllBytes(thumbPath).Should().Equal(truncated);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }
    }
}
