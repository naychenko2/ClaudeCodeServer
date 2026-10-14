namespace ClaudeHomeServer.Services.Media;

// Отображение состояния очереди GPU ComfyUI: все необязательные поля nullable, в журналы
// и траты не идёт (это живой снимок, а не учёт).

// Прогресс семплера: текущий шаг, всего шагов, текущая стадия (если известна).
public sealed record LocalGpuSteps(int Step, int Total, int? Stage);

// Кто сейчас держит GPU: вид прогона (Kind), тяжёлый ли (Heavy), оценка оставшегося времени.
public sealed record LocalGpuBusy(string Kind, bool Heavy, int? RemainingSeconds);

// Снимок очереди GPU: сколько прогонов впереди (Ahead), когда стартует этот (StartInSeconds),
// прогресс (Steps/Percent/PercentExact), остаток (RemainingSeconds), кто сейчас занят (Busy),
// с какого момента узел лежит (DownSince — unix-мс, как все времена фичи; null — узел жив).
// Running — задача идёт (в queue_running): Ahead=null, не «Следующая»; ждущая — Running=false,
// Ahead=число впереди. В конец позиционного record (необязательное, дефолт false) — чтобы не
// ломать существующие вызовы/тесты.
public sealed record LocalGpuQueueInfo(
    int? Ahead,
    int? StartInSeconds,
    LocalGpuSteps? Steps,
    int? Percent,
    bool PercentExact,
    int? RemainingSeconds,
    LocalGpuBusy? Busy,
    long? DownSince,
    bool Running = false);
