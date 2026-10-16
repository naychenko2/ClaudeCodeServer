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
    private readonly int _stepShiftH;
    private readonly int _stepShiftV;
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
    private long _canvasWidth;
    private long _canvasHeight;

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

        // m4: валидация доли перекрытия: NaN схлопывается в шаг 1 px, отрицательная даёт шаг
        // больше области (дыры), >= 1 — нулевой шаг.
        if (double.IsNaN(overlapFraction) || overlapFraction < 0 || overlapFraction >= 1)
            throw new ArgumentException(
                $"Доля перекрытия {overlapFraction} должна быть в [0, 1).", nameof(overlapFraction));

        _regionWidth = regionWidth;
        _regionHeight = regionHeight;
        _stepShiftH = ComputeStepH(regionWidth, overlapFraction);
        _stepShiftV = ComputeStepV(regionHeight, overlapFraction);
        _maxTiles = maxTiles;
        _maxSide = maxSide;
        _maxArea = maxArea;
        _maxElapsedMs = maxElapsedMs;
        // M2: полотно стартует с размера окна и растёт по измеренным сдвигам.
        _canvasWidth = regionWidth;
        _canvasHeight = regionHeight;
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
    /// ЗНАКОВЫЙ измеренный сдвиг этого шага (из <see cref="ShiftEstimator"/>: положительный —
    /// контент <c>before</c> сдвинут вниз/вправо в <c>after</c>, отрицательный — вверх/влево;
    /// знак зависит от направления шага, в т.ч. строки змейки справа налево),
    /// <paramref name="elapsedMs"/> — время, прошедшее от начала сканирования (извне, часы не
    /// читаем). Возвращает следующий шаг (или <c>Done</c>).
    /// </summary>
    public ScanStep Feed(ReadOnlySpan<byte> grayFrame, int measuredShift, long elapsedMs)
    {
        if (_phase == Phase.Done)
            throw new InvalidOperationException("Автомат уже завершён (Done).");

        _elapsedMs = elapsedMs;
        _measuredShifts.Add(measuredShift);
        _tiles++;

        // M2: полотно растёт по ИЗМЕРЕННЫМ сдвигам, а не по окну. Ось шага, который только что
        // выполнен, берём из CurrentStep (он ещё не заменён на следующий в switch ниже). ToStart —
        // позиционирование в угол, полотно не растит.
        if (CurrentStep.Kind != ScanStepKind.ToStart)
        {
            // Minor: на строках справа налево measuredShift отрицательный — простое «+=» уменьшало
            //    ширину и занижало потолки площади. Держим МАКСИМУМ достигнутой ширины/высоты.
            if (CurrentStep.Axis == Axis.Horizontal)
                _canvasWidth = Math.Max(_canvasWidth, _canvasWidth + measuredShift);
            else
                _canvasHeight = Math.Max(_canvasHeight, _canvasHeight + measuredShift);
        }

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

    /// <summary>Шаг в текущем горизонтальном направлении (M5: шаг по горизонтали).</summary>
    private void AdvanceHorizontal()
    {
        CurrentStep = new ScanStep(ScanStepKind.Step, Axis.Horizontal, _hDir, _stepShiftH);
    }

    /// <summary>Шаг вниз (переход на следующую строку) (M5: шаг по вертикали).</summary>
    private void AdvanceVertical()
    {
        CurrentStep = new ScanStep(ScanStepKind.Step, Axis.Vertical, 1, _stepShiftV);
    }

    private void SetDone()
    {
        CurrentStep = new ScanStep(ScanStepKind.Done, Axis.Vertical, 0, 0);
    }

    /// <summary>
    /// Проверяет потолки по РАСТУЩЕМУМУ полотну (M2: не по окну, а по накопленным измеренным
    /// сдвигам). Порядок: число плиток → сторона → площадь → время. Первый превышенный фиксирует
    /// причину. M3: при срабатывании любого потолка автомат переходит в Done (Truncated остаётся).
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
        else if (_canvasWidth > _maxSide || _canvasHeight > _maxSide)
        {
            _truncated = true;
            _truncationReason = $"Сторона полотна {_canvasWidth}×{_canvasHeight} px превышает потолок {_maxSide} px.";
        }
        else if (_canvasWidth * _canvasHeight > _maxArea)
        {
            _truncated = true;
            _truncationReason = $"Площадь полотна {_canvasWidth * _canvasHeight} px² превышает потолок {_maxArea} px².";
        }
        else if (_elapsedMs > _maxElapsedMs)
        {
            _truncated = true;
            _truncationReason = $"Превышено время: {_elapsedMs} ms > {_maxElapsedMs} ms.";
        }

        // M3: при срабатывании любого потолка автомат останавливается (Done), а не идёт дальше.
        if (_truncated && _phase != Phase.Done)
        {
            _phase = Phase.Done;
            SetDone();
        }
    }

    /// <summary>
    /// M5: шаг по оси = размер области вдоль этой оси минус перекрытие. Горизонтальный и
    /// вертикальный шаги различны (змейка шагает на разную величину по осям). Округление вниз:
    /// перекрытие не меньше заданной доли.
    /// </summary>
    private static int ComputeStepH(int regionWidth, double overlapFraction)
        => Math.Max(1, (int)(regionWidth * (1.0 - overlapFraction)));

    private static int ComputeStepV(int regionHeight, double overlapFraction)
        => Math.Max(1, (int)(regionHeight * (1.0 - overlapFraction)));

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
