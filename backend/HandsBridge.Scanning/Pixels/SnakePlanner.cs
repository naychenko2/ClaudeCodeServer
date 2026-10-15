namespace ClaudeHomeServer.HandsBridge.Scanning.Pixels;

/// <summary>Вид шага змейки.</summary>
public enum ScanStepKind
{
    /// <summary>В начало: прокрутить вверх-влево до границы области содержимого (левый верхний угол).</summary>
    ToStart,
    /// <summary>Шаг змейки: один шаг вдоль оси в заданном направлении.</summary>
    Step,
    /// <summary>Готово: шагов больше нет (конец полотна или превышен потолок).</summary>
    Done,
}

/// <summary>
/// Один шаг змейки: <see cref="Kind"/> — вид шага, <see cref="Axis"/> — ось (для
/// <c>ToStart</c> — <c>Vertical</c>, направление «вверх-влево»), <see cref="Direction"/> —
/// направление (-1 — вверх/влево, +1 — вниз/вправо; для <c>ToStart</c> всегда -1),
/// <see cref="ExpectedShift"/> — ожидаемый сдвиг в пикселях (подсказка для
/// <see cref="ShiftEstimator"/>; для <c>ToStart</c> — 0, т.к. расстояние до границы неизвестно).
/// </summary>
public readonly record struct ScanStep(ScanStepKind Kind, Axis Axis, int Direction, int ExpectedShift);

/// <summary>
/// Планировщик змейки: конечный автомат, строящий последовательность шагов для прокрутки
/// полотна, большего, чем окно. Размер полотна заранее НЕизвестен — змейка останавливается по
/// признаку конца прокрутки (<see cref="FrameStability.IsEndOfScroll"/>: два соседних кадра с
/// одинаковым хешем).
///
/// Последовательность: сначала «в начало» (вверх-влево до границы области содержимого), затем
/// строки: первая строка слева направо, следующая справа налево (змейка), и т.д. Шаг по оси =
/// размер области содержимого минус перекрытие (по умолчанию 15 %). Вход автомата — «снимок
/// после шага» (кадр и измеренный сдвиг), выход — «следующий шаг»/«готово».
///
/// Потолки (при превышении — <see cref="Truncated"/> с <see cref="TruncationReason"/>):
/// сторона полотна ≤ 16 384 px, площадь ≤ 64 Мп, ≤ 100 плиток, ≤ 5 минут. Время приходит
/// снаружи как параметр <c>elapsedMs</c> — часы не читаем, чтобы тесты были детерминированными.
/// </summary>
public class SnakePlanner
{
    /// <summary>Максимальная сторона полотна, px (по умолчанию 16 384).</summary>
    public const int DefaultMaxSide = 16384;

    /// <summary>Максимальная площадь полотна, px² (по умолчанию 64 Мп = 64 × 10⁶).</summary>
    public const long DefaultMaxArea = 64_000_000;

    /// <summary>Максимальное число плиток (по умолчанию 100).</summary>
    public const int DefaultMaxTiles = 100;

    /// <summary>Максимальное время, ms (по умолчанию 5 мин = 300 000).</summary>
    public const long DefaultMaxElapsedMs = 300_000;

    /// <summary>Доля перекрытия по умолчанию (15 %).</summary>
    public const double DefaultOverlapFraction = 0.15;

    private enum Phase
    {
        ToStart,
        Horizontal,
        Vertical,
        Done,
    }

    private readonly int _regionWidth;
    private readonly int _regionHeight;
    private readonly int _stepShift;
    private readonly int _maxTiles;
    private readonly int _maxSide;
    private readonly long _maxArea;
    private readonly long _maxElapsedMs;

    private Phase _phase = Phase.ToStart;
    private int _row;
    private int _col;
    private int _hDir = 1;
    private int _tiles;
    private long _elapsedMs;

    private readonly List<ulong> _hashes = new();
    private readonly List<int> _measuredShifts = new();

    private bool _truncated;
    private string? _truncationReason;

    /// <summary>
    /// Создаёт планировщик. <paramref name="regionWidth"/>/<paramref name="regionHeight"/> —
    /// размер области содержимого (из <see cref="ContentRegion"/>), px;
    /// <paramref name="overlapFraction"/> — доля перекрытия соседних плиток (0..1, по умолчанию
    /// 15 %): шаг = размер области − перекрытие; остальные параметры — потолки (см. класс).
    /// </summary>
    public SnakePlanner(
        int regionWidth,
        int regionHeight,
        double overlapFraction = DefaultOverlapFraction,
        int maxTiles = DefaultMaxTiles,
        int maxSide = DefaultMaxSide,
        long maxArea = DefaultMaxArea,
        long maxElapsedMs = DefaultMaxElapsedMs)
    {
        if (regionWidth <= 0 || regionHeight <= 0)
            throw new ArgumentException("Размер области должен быть положительным.", nameof(regionWidth));

        _regionWidth = regionWidth;
        _regionHeight = regionHeight;
        _stepShift = ComputeStep(regionWidth, regionHeight, overlapFraction);
        _maxTiles = maxTiles;
        _maxSide = maxSide;
        _maxArea = maxArea;
        _maxElapsedMs = maxElapsedMs;
    }

    /// <summary>Текущий шаг, который инструмент должен выполнить (или <c>Done</c>).</summary>
    public ScanStep CurrentStep { get; private set; } =
        new(ScanStepKind.ToStart, Axis.Vertical, -1, 0);

    /// <summary>Превышен ли один из потолков (полотно обрезано).</summary>
    public bool Truncated => _truncated;

    /// <summary>Причина обрезки (null, если не обрезано).</summary>
    public string? TruncationReason => _truncationReason;

    /// <summary>Число выполненных шагов (плиток).</summary>
    public int TileCount => _tiles;

    /// <summary>Измеренные сдвиги по шагам (в порядке выполнения), для <see cref="CanvasAssembler"/>.</summary>
    public IReadOnlyList<int> MeasuredShifts => _measuredShifts;

    /// <summary>
    /// Кормит автомат «снимком после шага»: <paramref name="grayFrame"/> — серый кадр
    /// (row-major, 0..255) после выполнения текущего шага, <paramref name="measuredShift"/> —
    /// измеренный сдвиг этого шага (из <see cref="ShiftEstimator"/>), <paramref name="elapsedMs"/>
    /// — время, прошедшее от начала сканирования (извне, часы не читаем). Возвращает следующий
    /// шаг (или <c>Done</c>).
    /// </summary>
    public ScanStep Feed(ReadOnlySpan<byte> grayFrame, int measuredShift, long elapsedMs)
    {
        if (_phase == Phase.Done)
            throw new InvalidOperationException("Автомат уже завершён (Done).");

        _elapsedMs = elapsedMs;
        _measuredShifts.Add(measuredShift);
        _tiles++;

        // Хеш кадра для признака конца прокрутки (два последних кадра одинаковы).
        _hashes.Add(HashFrame(grayFrame));
        if (_hashes.Count > 2)
            _hashes.RemoveAt(0);

        var endOfScroll = FrameStability.IsEndOfScroll(_hashes);

        switch (_phase)
        {
            case Phase.ToStart:
                // В начале (левый верхний угол): начинаем первую строку слева направо.
                _phase = Phase.Horizontal;
                _row = 0;
                _col = 0;
                _hDir = 1;
                AdvanceHorizontal();
                break;

            case Phase.Horizontal:
                if (endOfScroll)
                {
                    // Край строки (добрались до правого/левого конца): вниз к следующей строке.
                    _phase = Phase.Vertical;
                    AdvanceVertical();
                }
                else
                {
                    // Ещё не край: продолжаем строку, сдвигаем позицию по столбцам.
                    _col += _hDir;
                    AdvanceHorizontal();
                }
                break;

            case Phase.Vertical:
                if (endOfScroll)
                {
                    // Нижний край полотна: всё.
                    _phase = Phase.Done;
                    SetDone();
                }
                else
                {
                    // Перешли на следующую строку: меняем направление (змейка).
                    _row++;
                    _col = 0;
                    _hDir = -_hDir;
                    _phase = Phase.Horizontal;
                    AdvanceHorizontal();
                }
                break;
        }

        CheckCeilings();

        return CurrentStep;
    }

    /// <summary>Шаг в текущем горизонтальном направлении.</summary>
    private void AdvanceHorizontal()
    {
        CurrentStep = new ScanStep(ScanStepKind.Step, Axis.Horizontal, _hDir, _stepShift);
    }

    /// <summary>Шаг вниз (переход на следующую строку).</summary>
    private void AdvanceVertical()
    {
        CurrentStep = new ScanStep(ScanStepKind.Step, Axis.Vertical, 1, _stepShift);
    }

    private void SetDone()
    {
        CurrentStep = new ScanStep(ScanStepKind.Done, Axis.Vertical, 0, 0);
    }

    /// <summary>
    /// Проверяет потолки. Порядок: число плиток → сторона → площадь → время. Первый превышенный
    /// фиксирует причину.
    /// </summary>
    private void CheckCeilings()
    {
        if (_truncated)
            return;

        if (_tiles > _maxTiles)
        {
            _truncated = true;
            _truncationReason = $"Превышено число плиток: {_tiles} > {_maxTiles}.";
        }
        else if (_regionWidth > _maxSide || _regionHeight > _maxSide)
        {
            _truncated = true;
            _truncationReason = $"Сторона области превышает {_maxSide} px.";
        }
        else if ((long)_regionWidth * _regionHeight > _maxArea)
        {
            _truncated = true;
            _truncationReason = $"Площадь области превышает {_maxArea} px².";
        }
        else if (_elapsedMs > _maxElapsedMs)
        {
            _truncated = true;
            _truncationReason = $"Превышено время: {_elapsedMs} ms > {_maxElapsedMs} ms.";
        }
    }

    /// <summary>
    /// Шаг по оси = размер области вдоль этой оси минус перекрытие. Берём меньшее из шага по
    /// горизонтали и по вертикали, чтобы шаг был одинаковым для обеих осей (змейка шагает на
    /// одну величину). Округление вниз: перекрытие не меньше заданной доли.
    /// </summary>
    private static int ComputeStep(int regionWidth, int regionHeight, double overlapFraction)
    {
        var w = (int)(regionWidth * (1.0 - overlapFraction));
        var h = (int)(regionHeight * (1.0 - overlapFraction));
        return Math.Max(1, Math.Min(w, h));
    }

    /// <summary>FNV-1a 64-bit хеш кадра (детерминированный, без внешних зависимостей).</summary>
    private static ulong HashFrame(ReadOnlySpan<byte> frame)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var b in frame)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
