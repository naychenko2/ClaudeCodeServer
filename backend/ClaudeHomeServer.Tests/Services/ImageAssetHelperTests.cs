using ClaudeHomeServer.Services;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Дефект de740a5b: AvatarThumbStep при size > 256 возвращал 0 (default(int)) вместо null,
// и GET /api/personas/{id}/avatar?size=512 уходил в генерацию миниатюры «-0» с no-cache,
// а не в оригинал с immutable-кэшем. Прямая проверка сетки ступеней, включая все края.
public class ImageAssetHelperTests
{
    // Сетка ступеней [64, 128, 256]: наименьшая ступень, покрывающая size.
    // Края (ровно 64/128/256) и «за сеткой» (257, 512) — дефолт 0 маскировался как
    // «нужна миниатюра», хотя нужен оригинал.
    [Theory]
    [InlineData(null, null)]     // size не задан — оригинал
    [InlineData(0, null)]        // size = 0 — оригинал
    [InlineData(-5, null)]       // size < 0 — оригинал
    [InlineData(1, 64)]          // меньше младшей ступени — младшая
    [InlineData(64, 64)]         // ровно младшая ступень
    [InlineData(65, 128)]        // выше младшей — следующая
    [InlineData(128, 128)]       // ровно средняя ступень
    [InlineData(129, 256)]       // выше средней — старшая
    [InlineData(256, 256)]       // ровно старшая ступень
    [InlineData(257, null)]      // дефект: выше старшей — оригинал, не «-0»
    [InlineData(512, null)]      // дефект: размер из репро — оригинал
    public void AvatarThumbStep_СеткаИКрая(int? size, int? expected)
    {
        ImageAssetHelper.AvatarThumbStep(size).Should().Be(expected);
    }
}
