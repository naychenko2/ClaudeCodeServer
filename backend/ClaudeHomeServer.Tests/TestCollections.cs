namespace ClaudeHomeServer.Tests;

// Имена xunit-коллекций. Коллекция сериализует классы МЕЖДУ СОБОЙ, оставляя остальной
// набор параллельным — точечная замена глобальному parallelizeTestCollections=false,
// который уводил в один поток все сотни классов, включая WebApplicationFactory-буты,
// git и MCP-интеграции (ревью 2026-09-06, M-3).
public static class TestCollections
{
    // Классы, которые правят ПРОЦЕСС-ГЛОБАЛЬНОЕ состояние и потому не могут идти
    // параллельно ни друг с другом, ни между собой: перехват Console.SetError
    // (чужое восстановление в finally отдаёт тесту не свой поток) и переменные
    // окружения процесса (Environment.SetEnvironmentVariable).
    //
    // Оба класса проблем объединены в ОДНУ коллекцию сознательно: xunit разрешает
    // классу состоять ровно в одной коллекции, а SubscriptionOAuthUsageServiceTests
    // трогает и env, и stderr — держать их раздельно значило бы потерять изоляцию
    // на этом классе.
    public const string ProcessGlobalState = "process-global-state";
}

// Объявление коллекции (без фикстуры — общего состояния тестам не нужно, нужна только
// сериализация). Раньше жило в DockerProcessRunnerEnvTests.cs под именем SystemEnv;
// переехало сюда вместе с расширением области на stderr-классы.
[CollectionDefinition(TestCollections.ProcessGlobalState)]
public class ProcessGlobalStateCollection;
