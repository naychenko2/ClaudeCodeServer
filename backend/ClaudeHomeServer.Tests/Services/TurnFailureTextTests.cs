using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Единственная точка человеческих формулировок сбоя хода: сырые .NET-тексты и английские
// ответы CLI не должны доезжать до ленты чата (они уходят в Details и в лог сервера).
public class TurnFailureTextTests
{
    // Прод-инцидент: запись в stdin закрывающегося CLI (System.IO.IOException,
    // «Идет закрытие канала.») — новое сообщение пришло, пока предыдущий ход сворачивался
    [Fact]
    public void ЗакрытиеКанала_ПоРусскомуТексту_ЧастнаяФормулировка()
        => TurnFailureText.ForException(new IOException("Идет закрытие канала."))
            .Should().Be(TurnFailureText.PipeClosing);

    [Theory]
    [InlineData("The pipe is being closed.")]
    [InlineData("Broken pipe")]
    [InlineData("Pipe has been ended.")]
    public void ЗакрытиеКанала_ПоАнглийскомуТексту_ЧастнаяФормулировка(string message)
        => TurnFailureText.ForException(new IOException(message))
            .Should().Be(TurnFailureText.PipeClosing);

    // Локаль системы менять нельзя, а текст исключения от неё зависит — код ошибки Windows
    // (ERROR_NO_DATA 232) обязан опознаваться и при незнакомой формулировке
    [Fact]
    public void ЗакрытиеКанала_ПоКодуОшибки_ЧастнаяФормулировка()
    {
        var io = new IOException("konnte nicht geschrieben werden") { HResult = unchecked((int)0x800700E8) };
        TurnFailureText.ForException(io).Should().Be(TurnFailureText.PipeClosing);
    }

    // Исключение приезжает обёрнутым (await по каналу, Task.WhenAll) — разворачиваем
    [Fact]
    public void ЗакрытиеКанала_ВнутриОбёртки_ЧастнаяФормулировка()
    {
        var wrapped = new AggregateException(new InvalidOperationException("wrapper",
            new IOException("Идет закрытие канала.")));
        TurnFailureText.ForException(wrapped).Should().Be(TurnFailureText.PipeClosing);
    }

    // Прод-инцидент 16.08.2026: ObjectDisposedException приехал в ленту сырым текстом
    [Fact]
    public void ОбъектУничтожен_ОбщаяФормулировка()
        => TurnFailureText.ForException(new ObjectDisposedException("System.Threading.CancellationTokenSource"))
            .Should().Be(TurnFailureText.Generic);

    [Fact]
    public void НеопознанноеИсключение_ОбщаяФормулировка()
        => TurnFailureText.ForException(new InvalidOperationException("boom"))
            .Should().Be(TurnFailureText.Generic);

    [Fact]
    public void ФайловыйIo_БезПризнаковКанала_ОбщаяФормулировка()
        => TurnFailureText.ForException(new IOException("The process cannot access the file 'a.txt'"))
            .Should().Be(TurnFailureText.Generic);

    [Fact]
    public void ИсключенияНет_ОбщаяФормулировка()
        => TurnFailureText.ForException(null).Should().Be(TurnFailureText.Generic);

    // Тот самый текст из ленты прода — за ним следовала подмена модели
    [Fact]
    public void Перегрузка529_ТекстПерегрузки()
        => TurnFailureText.ForCliError(
                "API Error: 529 Overloaded. This is a server-side issue, usually temporary — try again in a moment. If it persists, check https://status.claude.com")
            .Should().Be(TurnFailureText.Overloaded);

    [Theory]
    [InlineData("overloaded_error")]
    [InlineData("API Error: 500 Internal Server Error")]
    [InlineData("502 Bad Gateway")]
    [InlineData("Service Unavailable")]
    [InlineData("Gateway timeout")]
    public void ОшибкиПровайдера_ТекстПерегрузки(string raw)
        => TurnFailureText.ForCliError(raw).Should().Be(TurnFailureText.Overloaded);

    // Нераспознанное остаётся сырым: вызывающий покажет настоящий текст, а не выдуманный
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("API Error: 400 invalid_request_error")]
    [InlineData("Prompt is too long: 1529 tokens")]
    public void Нераспознанное_Null(string? raw)
        => TurnFailureText.ForCliError(raw).Should().BeNull();
}
