using System.Reflection;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сторож границ доступа GitService.RunAsync/RunOkAsync: оба метода НЕ должны быть
// `public` (и не должны быть `protected`) — после волн 1-3 вся нужная функциональность
// доступна через типизированные методы GitService и IGitRefSnapshotStore. Любой внешний
// код, желающий сырую команду git, должен идти через типизированный метод (образец
// DiffFileVsHeadAsync/LogNumstatRangeAsync, добавленные в волне 3). Этот файл живёт
// в `ClaudeHomeServer.Git.Tests` (Этап 3, вынос вертикали в отдельный .csproj) и
// видит internal-члены через `InternalsVisibleTo("ClaudeHomeServer.Git.Tests")` в
// `ClaudeHomeServer.Git.csproj`; второй атрибут там же, на `ClaudeHomeServer.Tests`,
// оставлен ради тестов, которые остались в Main.Tests.
//
// Проверка опирается на сам факт модификатора: если кто-то по ошибке вернёт
// `public`/`protected`, сторож упадёт. Сам компилятор при текущем `internal` тоже
// является гейтом (внешние вызывающие в продакшн-коде не компилируются), но компилятор
// молчит, если кто-то добавит новый вызывающий в Services/Git/ — а это уже злоупотребление
// (нарушает дух «GitService.RunAsync — внутренняя точка plumbing»), и сторож закрывает
// эту дыру.
public class GitAccessBoundaryTests
{
    [Fact]
    public void RunAsync_Не_Pубличный()
    {
        var m = typeof(GitService).GetMethod(nameof(GitService.RunAsync),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m.Should().NotBeNull("GitService.RunAsync должен существовать");
        var vis = m!.IsPublic || m.IsFamily /* protected */;
        vis.Should().BeFalse(
            "GitService.RunAsync — внутренний plumbing; снаружи Services/Git/ должна быть типизированная обёртка");
    }

    [Fact]
    public void RunOkAsync_Не_Публичный()
    {
        var m = typeof(GitService).GetMethod("RunOkAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m.Should().NotBeNull("GitService.RunOkAsync должен существовать");
        var vis = m!.IsPublic || m.IsFamily /* protected */;
        vis.Should().BeFalse(
            "GitService.RunOkAsync — внутренний plumbing; снаружи Services/Git/ должна быть типизированная обёртка");
    }
}