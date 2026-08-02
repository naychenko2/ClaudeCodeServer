namespace ClaudeHomeServer.Services;

// Отказ включить режим «Командная реализация» (гард на входе, B2 приёмки): чат без
// координатора или без состава исполнителей штабом не станет. Код отказа машинный —
// фронт по нему решает, что показывать (пикер координатора / выбор состава), и не
// отправляет вводную обычным сообщением; текст — человеку.
public sealed class TeamImplementSetupException(string code, string message) : Exception(message)
{
    // Нет персоны-собеседника и не выбран координатор явно
    public const string NoCoordinator = "team_implement_no_coordinator";
    // Состав исполнителей пуст: вне проекта команды нет либо в проекте нет персон
    public const string NoExecutors = "team_implement_no_executors";

    public string Code { get; } = code;
}
