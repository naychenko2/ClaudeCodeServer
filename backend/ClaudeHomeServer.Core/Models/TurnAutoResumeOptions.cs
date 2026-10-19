namespace ClaudeHomeServer.Models;

// Настройки авто-возобновления ходов после рестарта сервера (флаг turn-auto-resume) —
// секция «TurnAutoResume» в appsettings.json. Дефолты: MaxPerBoot=10, WarmupSeconds=15,
// GapSeconds=5. Переопределяются в appsettings.Local.json.
public class TurnAutoResumeOptions
{
    public const string Section = "TurnAutoResume";

    // Потолок кандидатов на возобновление за один старт сервера.
    public int MaxPerBoot { get; set; } = 10;

    // Пауза прогрева (сек) после ApplicationStarted перед первой отправкой.
    public int WarmupSeconds { get; set; } = 15;

    // Пауза (сек) между сессиями при последовательной отправке.
    public int GapSeconds { get; set; } = 5;
}
