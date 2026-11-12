namespace ClaudeHomeServer.Services;

// Отказ запуска исполнителя по маршруту фичи (B3): маршрут не подтверждён либо у узла слияния
// не завершён предшественник. Штатный ответ правила, а не поломка: автозапуск пропускает его
// тихо, как отказ по паузе (FeatureHoldException). База — InvalidOperationException, поэтому
// существующие catch вызывающих (MCP tasks_run_executor, POST /execute) не меняются.
public class FeatureRouteLaunchException : InvalidOperationException
{
    public FeatureRouteLaunchException(string message) : base(message) { }
}
