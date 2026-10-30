using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests;

// Сторож этой сборки: Inotify/ProcessGlobalState имеют [CollectionDefinition] здесь
// (xunit не подхватывает их из TestKit), Inotify — с DisableParallelization.
// Мутации, которые обязаны его покраснить: снять флаг или удалить определение
// в TestCollectionDefinitions.cs.
public class CollectionDefinitionGuardTests
{
    [Fact]
    public void ОпределенияКоллекцийЛокальныДляЭтойСборки() =>
        CollectionDefinitionGuard.AssertLocalDefinitions(typeof(CollectionDefinitionGuardTests).Assembly);
}
