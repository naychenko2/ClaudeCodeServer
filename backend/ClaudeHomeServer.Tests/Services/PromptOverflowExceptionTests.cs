using System.ComponentModel;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Исключение бросает ClaudeSession, когда ApplyBudget не уложился в порог 30 000
// даже после срезания всех нестабильных секций. Маркируется как Win32Exception
// (NativeErrorCode = 206 ERROR_FILENAME_EXCED_RANGE) — общий catch в RunTurnAsync
// ловит его тем же кодом, что и реальный Win32Exception от Process.Start, и кладёт
// в Details ErrorMessage префикс "[Win32:206]". Классификатор по нему отдаёт
// FallbackErrorClass.PromptOverflow, фолбэк не запускается.
//
// Тест-фикстуры ловят:
//  1) NativeErrorCode == 206 — иначе классификатор не сработает по этому каналу;
//  2) наследник Win32Exception — общий catch в ClaudeSession пишет маркер
//     "[Win32:{NativeErrorCode}]" в Details, и для обычных Exception он бы не сработал;
//  3) конструктор пробрасывает счётчики — PromptOverflowAsync использует их в логе.
public class PromptOverflowExceptionTests
{
    [Fact]
    public void Конструктор_ПроставляетNativeErrorCode206()
    {
        // 206 = ERROR_FILENAME_EXCED_RANGE. Классификатор в TurnErrorClassifier.cs
        // сравнивает строго с этим числом; другое значение (или 0) уйдёт в Unreachable,
        // и адаптер запустит фолбэк на 5 пар впустую.
        var ex = new PromptOverflowException(totalCmdlineChars: 35_000, budget: 30_000);

        ex.NativeErrorCode.Should().Be(206,
            "PromptOverflow — это наш локальный аналог ERROR_FILENAME_EXCED_RANGE, " +
            "число должно совпадать с тем, что ловит TurnErrorClassifier");
    }

    [Fact]
    public void Конструктор_СохраняетСчётчики()
    {
        var ex = new PromptOverflowException(totalCmdlineChars: 35_000, budget: 30_000);
        ex.TotalCmdlineChars.Should().Be(35_000);
        ex.Budget.Should().Be(30_000);
    }

    [Fact]
    public void НаследникWin32Exception()
    {
        // Общий catch в ClaudeSession ловит всё через Exception и пишет
        // "[Win32:{NativeErrorCode}]{ex.Message}" в Details только если ex — Win32Exception.
        // Если бы PromptOverflowException наследовал просто Exception, общий catch
        // не смог бы поставить маркер и адаптер не увидел бы PromptOverflow.
        var ex = new PromptOverflowException(35_000, 30_000);
        ex.Should().BeAssignableTo<Win32Exception>(
            "ClaudeSession общий catch пишет [Win32:206] префикс только для Win32Exception");
    }

    [Fact]
    public void СообщениеСодержитЦифры()
    {
        // Текст исключения едет в Details ErrorMessage и в лог сервера — должен быть
        // информативным для диагностики (фактические цифры, не голое "ошибка").
        var ex = new PromptOverflowException(35_000, 30_000);
        ex.Message.Should().Contain("35");
        ex.Message.Should().Contain("30");
    }
}
