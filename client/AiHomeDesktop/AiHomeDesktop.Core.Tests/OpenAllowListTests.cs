using AiHomeDesktop.Core.Policies;
using FluentAssertions;
using Xunit;

namespace AiHomeDesktop.Core.Tests;

/// <summary>
/// Allow-list целей desktop_open. Это ГИГИЕНА И СЛЕДЫ, а не граница безопасности
/// (ADR-008): мимо списка едут .lnk и протокольные обработчики. Тесты фиксируют ровно то,
/// что список обещает, — и ничего сверх.
/// </summary>
public class OpenAllowListTests
{
    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell.exe")]
    [InlineData("wt")]
    [InlineData("bash")]
    [InlineData(@"C:\work\deploy.ps1")]
    [InlineData("script.bat")]
    public void Оболочки_Вычеркнуты(string target)
    {
        // Даже явно отмеченная человеком запись оболочку не открывает
        var list = new OpenAllowList([target]);
        list.Evaluate(target).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Ссылки_ТолькоHttpИHttps()
    {
        var list = new OpenAllowList();

        list.Evaluate("https://example.com/report").Allowed.Should().BeTrue("ссылки разрешены классом");
        list.Evaluate("file:///C:/secret.txt").Allowed.Should().BeFalse();
        list.Evaluate("ms-settings:privacy").Allowed.Should().BeFalse(
            "протокольный обработчик — способ запустить что угодно, а не «ссылка»");
    }

    [Fact]
    public void ПриложениеИФайл_ТолькоИзСписка()
    {
        var list = new OpenAllowList(["notepad.exe", @"C:\work"]);

        list.Evaluate("notepad").Allowed.Should().BeTrue();
        list.Evaluate(@"C:\work\отчёт.docx").Allowed.Should().BeTrue();
        list.Evaluate(@"C:\work2\чужое.docx").Allowed.Should().BeFalse(
            "сравнение по сегментам пути, а не по префиксу строки");
        list.Evaluate(@"C:\Users\me\Desktop\что-то.exe").Allowed.Should().BeFalse();
    }

    [Fact]
    public void ПустаяЦель_Отказ()
    {
        new OpenAllowList(["notepad.exe"]).Evaluate("  ").Allowed.Should().BeFalse();
    }
}
