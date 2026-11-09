namespace ClaudeHomeServer.HandsBridge.Scanning.Pixels;

/// <summary>
/// Стабилизация кадра: «кадр перестал меняться» — признак конца прокрутки. Чистые функции без
/// Windows-зависимостей: серые кадры (0..255) и хеши последних кадров.
/// </summary>
public static class FrameStability
{
    /// <summary>
    /// Два кадра стабильны, если средняя абсолютная разница серого по пикселям меньше
    /// <paramref name="tolerance"/> (та же мера, что в спайке: кадр «не изменился» в пределах
    /// допуска). Кадры обязаны быть одного размера.
    /// </summary>
    public static bool AreStable(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, double tolerance)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Кадры разного размера: {a.Length} и {b.Length}.", nameof(a));

        if (a.Length == 0)
            return true;

        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs((double)a[i] - b[i]);

        return sum / a.Length <= tolerance;
    }

    /// <summary>
    /// Конец прокрутки: два последних кадра дали ОДИНАКОВЫЙ хеш — кадр не изменился после
    /// прокрутки два раза подряд (контент больше не двигается). Меньше двух хешей — рано
    /// говорить о конце.
    /// </summary>
    public static bool IsEndOfScroll(IReadOnlyList<ulong> lastHashes)
    {
        if (lastHashes is null)
            throw new ArgumentNullException(nameof(lastHashes));

        if (lastHashes.Count < 2)
            return false;

        return lastHashes[^1] == lastHashes[^2];
    }
}
