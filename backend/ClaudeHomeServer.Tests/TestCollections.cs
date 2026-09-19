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

    // Классы, которые пишут или читают статические `Session.TaskSourceSessionResolver` /
    // `Session.TaskDelegationDepthResolver` / `Session.TaskDoneResolver`. Эти `Func`-свойства
    // живут в `Core/Models/Session.cs:481/505/515`, а ставит их конструктор `TaskManager`
    // (`Services/Tasks/TaskManager.cs:32-36`). Любой `new TaskManager(...)` в тестах
    // перезаписывает все три под ногами у параллельно идущих классов — отсюда плавающее
    // падение `TaskManagerTests.Constructor_УстанавливаетТриРезолвераНаSession` и
    // `TeamWaveServiceTests.Пульс_АктивностьСчитаетсяПоВсемТочкамВолны` в полном прогоне
    // (Ф5 не меняла логику, но сменила порядок/частоту создания этих объектов и разбудила
    // гонку). Лучше включить лишний класс, чем пропустить нужный: цена ошибки в первую
    // сторону — секунды прогона, во вторую — возврат флака.
    //
    // Это НЕ костыль-ретрай, а честная декларация: эти тесты делят глобальное состояние
    // процесса и параллелиться не могут. Убирать статику из `Session` — отдельная задача
    // (модель в Core не должна звать сервисы, но её вычисляемые свойства торчат в DTO и
    // фильтрах чатов; трогать это заодно нельзя).
    public const string SessionStaticResolvers = "session-static-resolvers";

    // Классы, которые на время теста урезают лимит дескрипторов ПРОЦЕССА (setrlimit, см.
    // InotifyProbe.WithFdHeadroom) и меряют inotify-fd процесса: параллельный сосед либо
    // упал бы на EMFILE, либо исказил бы замер своими наблюдателями.
    public const string FdLimit = "fd-limit";
}

// Объявление коллекции (без фикстуры — общего состояния тестам не нужно, нужна только
// сериализация). Раньше жило в DockerProcessRunnerEnvTests.cs под именем SystemEnv;
// переехало сюда вместе с расширением области на stderr-классы.
[CollectionDefinition(TestCollections.ProcessGlobalState)]
public class ProcessGlobalStateCollection;

[CollectionDefinition(TestCollections.SessionStaticResolvers, DisableParallelization = true)]
public class SessionStaticResolversCollection;

[CollectionDefinition(TestCollections.FdLimit, DisableParallelization = true)]
public class FdLimitCollection;
