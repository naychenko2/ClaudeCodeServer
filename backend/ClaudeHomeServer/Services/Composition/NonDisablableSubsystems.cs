namespace ClaudeHomeServer.Services.Composition;

// Неотключаемые подсистемы (ADR-014, «Неотключаемые подсистемы»): для них тумблер
// `Subsystems:{Key}:Enabled` не действует — ядро зависит от них обязательно, поэтому отказ
// обязан быть явным на старте, а не случайным исключением DI глубже в запуске. На сегодня
// такая одна — `llm` (п.5: новую заводят только записью в этот пункт с причиной).
//
// Тело проверки вынесено из top-level statements Program.cs в метод специально: контракт
// обязан охранять юнит-тест, а код между `WebApplication.CreateBuilder` и `builder.Build()`
// тестом недостижим — top-level statements выполняются раньше, чем TestWebApplicationFactory
// успевает подмешать ExtraConfig (та же грабля, из-за которой четыре теста `LlmOff` были
// вакуумны: ревью R-S3-LLM-CORE, п.5). В Program.cs остаётся единственный вызов — на прежнем
// месте: после всех источников конфига, до `AddSubsystems` и до побочных эффектов старта.
public static class NonDisablableSubsystems
{
    // `llm` — несущая зависимость ядра сессий, а не шов: на `LlmProviderRegistry` стоят
    // маршрут хода к провайдеру и CLI-профилю, вывод провайдера из модели, смена провайдера
    // и учёт расхода хода (решение архитектора e6c00b1e, ADR-014 п.5).
    public static void EnsureLlmEnabled(IConfiguration config)
    {
        if (SubsystemGate.IsEnabled(config, "llm")) return;

        throw new InvalidOperationException(
            "Подсистема llm не отключается: ядро сессий зависит от неё (ADR-014, „Неотключаемые подсистемы“)");
    }
}
