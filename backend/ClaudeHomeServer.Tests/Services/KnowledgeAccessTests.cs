using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Изоляция per-owner раздела «Знания»: с общим Dify-ключом нельзя лезть в чужую базу.
// Именно эти решения дают контроллеру 403 — покрываем негативы явно.
public class KnowledgeAccessTests
{
    private static readonly IReadOnlySet<string> Others =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bob", "carol" };

    // --- IsRelevant (доступ на чтение → иначе 403) ---

    [Theory]
    [InlineData("alice:kb:мойсписок")]   // своя самостоятельная
    [InlineData("alice:notes")]          // свои заметки
    [InlineData("alice:MyProject")]      // свой проект
    [InlineData("Общая база")]           // глобальная (без префикса)
    public void IsRelevant_СвояИлиГлобальная_True(string name)
    {
        KnowledgeAccess.IsRelevant(name, "alice", Others).Should().BeTrue();
    }

    [Theory]
    [InlineData("bob:kb:секрет")]        // чужая самостоятельная
    [InlineData("bob:notes")]            // чужие заметки
    [InlineData("carol:Project")]        // чужой проект
    public void IsRelevant_ЧужаяПомеченная_False(string name)
    {
        KnowledgeAccess.IsRelevant(name, "alice", Others).Should().BeFalse();
    }

    [Theory]
    [InlineData("alice:persona:reviewer")]  // своя память персоны — внутренняя
    [InlineData("alice:team:proj")]         // своя память команды — внутренняя
    public void IsRelevant_СвояВнутренняяПамять_False(string name)
    {
        KnowledgeAccess.IsRelevant(name, "alice", Others).Should().BeFalse();
    }

    // --- IsDeletable (удаление из раздела → иначе 403) ---

    [Fact]
    public void IsDeletable_СвояСамостоятельная_True()
    {
        KnowledgeAccess.IsDeletable("alice:kb:list", "alice", Others, isAdmin: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("alice:notes")]      // привязка заметок
    [InlineData("alice:MyProject")]  // привязка проекта
    [InlineData("alice:team:x")]     // память команды
    public void IsDeletable_ПривязаннаяБаза_False(string name)
    {
        KnowledgeAccess.IsDeletable(name, "alice", Others, isAdmin: false).Should().BeFalse();
    }

    [Fact]
    public void IsDeletable_ЧужаяБаза_False_ДажеАдмин()
    {
        KnowledgeAccess.IsDeletable("bob:kb:list", "alice", Others, isAdmin: true).Should().BeFalse();
    }

    [Fact]
    public void IsDeletable_Глобальная_ТолькоАдмин()
    {
        KnowledgeAccess.IsDeletable("Общая", "alice", Others, isAdmin: false).Should().BeFalse();
        KnowledgeAccess.IsDeletable("Общая", "alice", Others, isAdmin: true).Should().BeTrue();
    }

    // Регистронезависимость префикса не даёт обойти изоляцию сменой регистра
    [Fact]
    public void IsRelevant_ЧужаяБазаВДругомРегистре_False()
    {
        KnowledgeAccess.IsRelevant("BOB:kb:x", "alice", Others).Should().BeFalse();
    }
}
