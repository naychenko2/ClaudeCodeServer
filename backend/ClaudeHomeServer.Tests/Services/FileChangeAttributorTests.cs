using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// FileChangeAttributor — реестр заявок file_changed чату-источнику (см. план в задаче
// «Атрибуция file_changed чату-источнику»): при параллельных ходах двух чатов одного
// проекта TurnFileWatcher чужого чата должен подавить карточку правки, сделанной сессией А.
public class FileChangeAttributorTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Незаявленный_путь_НеСчитаетсяЧужим()
    {
        var attributor = new FileChangeAttributor();

        attributor.IsClaimedByOther("session-a", "/repo/file.cs", Base).Should().BeFalse();
    }

    [Fact]
    public void СвояЗаявка_НеСчитаетсяЧужой()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/file.cs", Base);

        attributor.IsClaimedByOther("session-a", "/repo/file.cs", Base).Should().BeFalse();
    }

    [Fact]
    public void ЗаявкаДругойСессии_ВПределахОкна_СчитаетсяЧужой()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/file.cs", Base);

        attributor.IsClaimedByOther("session-b", "/repo/file.cs", Base.AddSeconds(5))
            .Should().BeTrue();
    }

    [Fact]
    public void ЗаявкаДругойСессии_ПослеОкна_БольшеНеЧужая()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/file.cs", Base);

        var after = Base + FileChangeAttributor.AttributionWindow + TimeSpan.FromSeconds(1);
        attributor.IsClaimedByOther("session-b", "/repo/file.cs", after).Should().BeFalse();
    }

    [Fact]
    public void ЗаявкаДругойСессии_РовноНаГраницеОкна_ЕщёЧужая()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/file.cs", Base);

        var atWindowEdge = Base + FileChangeAttributor.AttributionWindow;
        attributor.IsClaimedByOther("session-b", "/repo/file.cs", atWindowEdge).Should().BeTrue();
    }

    [Fact]
    public void НоваяЗаявкаПерезаписываетСтарую_ДругойПутьНеТрогает()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/file.cs", Base);
        attributor.Claim("session-b", "/repo/file.cs", Base.AddSeconds(1));

        // Теперь последняя заявка — session-b: для неё путь свой, для session-a — чужой
        attributor.IsClaimedByOther("session-b", "/repo/file.cs", Base.AddSeconds(2)).Should().BeFalse();
        attributor.IsClaimedByOther("session-a", "/repo/file.cs", Base.AddSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void РазныеПути_НеВлияютДругНаДруга()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", "/repo/a.cs", Base);

        attributor.IsClaimedByOther("session-b", "/repo/b.cs", Base.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void ПутьСРазнымРегистром_СчитаетсяТемЖеПутём()
    {
        var attributor = new FileChangeAttributor();
        attributor.Claim("session-a", @"C:\repo\File.cs", Base);

        attributor.IsClaimedByOther("session-b", @"C:\REPO\file.CS", Base.AddSeconds(1))
            .Should().BeTrue();
    }
}
