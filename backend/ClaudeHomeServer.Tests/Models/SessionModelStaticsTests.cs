using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

// Сторож разреза «модель сессии — чистая»: в спин-модели нет ИЗМЕНЯЕМОЙ статики.
//
// Зачем сторож. До этой работы `Models.Session` держала статические Func-резолверы
// (`TaskSourceSessionResolver`/`TaskDoneResolver`/`TaskDelegationDepthResolver`), которые
// ставил конструктор `TaskManager` из вертикали Tasks: спин-модель фактически зависела от
// вертикали, сторож границ (рефлексия по ТИПАМ) присваивания не видел, а тесты приходилось
// держать в безпараллельной коллекции, потому что состояние было общим на процесс.
// Теперь вычисления живут в `SessionTaskLinks` поверх шва `ITaskLookup`, а поля wire
// дописывает `SessionJsonConverter` на границе сериализации.
//
// Почему рефлексия по ВСЕМУ namespace `ClaudeHomeServer.Models`, а не по типу `Session`:
// критерий «в Session.cs нет статики» иначе выполнялся бы переименованием типа или
// переносом резолвера в соседний файл моделей. Разрешено то, что изменяемым состоянием не
// является: `const`, `static readonly` (в т.ч. таблицы-справочники) и статические методы /
// вычисляемые свойства без сеттера.
public class SessionModelStaticsTests
{
    // Типы моделей, написанные человеком: сгенерированное компилятором отбрасываем — кэши
    // делегатов (`<>O.<0>__…` для групп методов) и замыкания это статика, но не наша.
    private static IEnumerable<Type> ModelTypes() =>
        typeof(Session).Assembly.GetTypes()
            .Where(t => t.Namespace == "ClaudeHomeServer.Models")
            .Where(t => !t.Name.Contains('<')
                && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

    [Fact]
    public void Модели_НеДержатИзменяемыхСтатическихПолей()
    {
        var mutable = ModelTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsLiteral && !f.IsInitOnly)
                .Select(f => $"{t.Name}.{f.Name}"))
            .ToList();

        mutable.Should().BeEmpty(
            "изменяемая статика в спин-модели — это скрытая зависимость от вертикали и общее " +
            "состояние процесса (бывшие Session.*Resolver); допустимы только const и static readonly");
    }

    [Fact]
    public void Модели_НеДержатСтатическихСвойствССеттером()
    {
        var settable = ModelTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.SetMethod is not null)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        settable.Should().BeEmpty(
            "статическое свойство с сеттером — та же точка внешней подстановки, что и Func-резолвер");
    }

    [Fact]
    public void Session_НеДержитРезолверовЗадачи()
    {
        // Точечный сторож на возврат именно этой механики: любой член с «Resolver» в имени
        // у Session — сигнал, что связи «чат ↔ задача» снова считаются в модели.
        typeof(Session).GetMembers(BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.Contains("Resolver", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("связи считает SessionTaskLinks поверх ITaskLookup, а не модель");
    }
}
