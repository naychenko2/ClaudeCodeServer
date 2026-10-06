using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Память сферы — внутренняя база «{user}:sphere:{id}»: в каталоге «Знаний» не видна, как память команды
public class KnowledgeBaseCatalogSphereTests
{
    private static readonly HashSet<string> Others = new(StringComparer.OrdinalIgnoreCase) { "bob" };

    // Classify не ходит ни в Dify, ни в хранилище пользователей — зависимости не нужны
    private static KnowledgeBaseCatalogService NewCatalog() =>
        new(null!, new Mock<IUserStore>().Object);

    [Fact]
    public void Classify_СвояПамятьСферы_Скрыта()
    {
        var d = new DifyDatasetListItem("ds-1", "alice:sphere:s-42");

        NewCatalog().Classify(d, "alice", Others).Should().BeNull();
    }

    [Fact]
    public void Classify_ОбычныйПроект_НеЗатронут()
    {
        var d = new DifyDatasetListItem("ds-2", "alice:MyProject");

        NewCatalog().Classify(d, "alice", Others).Should().NotBeNull();
    }

    [Fact]
    public void IsRelevant_СвояПамятьСферы_False_ЧужаяТоже()
    {
        KnowledgeAccess.IsRelevant("alice:sphere:s-42", "alice", Others).Should().BeFalse();
        KnowledgeAccess.IsRelevant("bob:sphere:s-42", "alice", Others).Should().BeFalse();
    }
}
