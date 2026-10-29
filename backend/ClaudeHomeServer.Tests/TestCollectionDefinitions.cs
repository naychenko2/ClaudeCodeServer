namespace ClaudeHomeServer.Tests;

// Определения xunit-коллекций обязаны жить в сборке самих тестов: xunit 2.9.2 находит
// [CollectionDefinition] только в прогоняемой сборке, из TestKit они не подхватывались
// никогда — [Collection(TestCollections.Inotify)] без локального определения становился
// неявной коллекцией «по имени»: классы шли последовательно внутри неё, но параллельно
// со всем прогоном, и упёрлись в общий бюджет inotify пользователя («max_user_instances
// has been reached»). Локальность определений проверяет CollectionDefinitionGuard
// (TestKit) — его вызывает тест этой сборки. Образец: TestCollectionDefinitions.cs
// в Llm.Tests.
[CollectionDefinition(TestCollections.Inotify, DisableParallelization = true)]
public class InotifyCollection;

// РАСХОЖДЕНИЕ с Llm.Tests, зафиксированное намеренно (разрез 3908fd11): там ProcessGlobalState
// определён с DisableParallelization, здесь — без флага. Семантика монолита перенесена 1:1
// из TestKit: классы сериализованы между собой и параллельны остальному прогону — это и был
// исходный замысел коллекции (см. шапку TestCollections.cs); основания менять её в этом разрезе
// нет, 10+ классов ушли бы в последовательный хвост. Решение выравнивать стороны — отдельное,
// по факту флака.
[CollectionDefinition(TestCollections.ProcessGlobalState)]
public class ProcessGlobalStateCollection;
