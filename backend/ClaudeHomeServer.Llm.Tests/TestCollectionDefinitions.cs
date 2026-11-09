namespace ClaudeHomeServer.Tests;

// Определения коллекций xunit обязаны жить в сборке самих тестов: из TestKit они не
// подхватываются, и DisableParallelization там не действует (Inotify шёл параллельно с
// остальным). Обе коллекции процесс-глобальные (inotify-бюджет и setrlimit, stderr и env
// процесса) — такие классы идут поодиночке, после параллельной части прогона.
[CollectionDefinition(TestCollections.Inotify, DisableParallelization = true)]
public class LlmInotifyCollection;

// РАСХОЖДЕНИЕ с монолитом, зафиксированное намеренно (разрез 3908fd11): здесь ProcessGlobalState
// с DisableParallelization (Llm-классы реально дерутся за stderr/env процесса рядом с живыми
// процессами CLI), в монолите та же коллекция — без флага (её поведение перенесено 1:1 из
// времени, когда флаг в TestKit не действовал). Решение выравнивать стороны — отдельное,
// по факту флака; расхождение зафиксировано в обоих TestCollectionDefinitions.cs.
[CollectionDefinition(TestCollections.ProcessGlobalState, DisableParallelization = true)]
public class LlmProcessGlobalStateCollection;

// Тесты с реальным коротким бюджетом продукта (LocalEngineStateClient: 800 мс на запрос к
// заглушке в том же процессе) под нагрузкой параллельной части дают ложный «недоступен».
[CollectionDefinition(LlmTestCollections.TimingSensitive, DisableParallelization = true)]
public class LlmTimingSensitiveCollection;

public static class LlmTestCollections
{
    public const string TimingSensitive = "llm-timing-sensitive";
}
