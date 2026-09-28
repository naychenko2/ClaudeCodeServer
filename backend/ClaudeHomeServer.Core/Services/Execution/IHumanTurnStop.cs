namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Ход остановил человек на устройстве («Стоп» в трее рук, ADR-016 §7). Раннер устройства
/// (Execution) зовёт шов по кадру Exit с <c>StoppedBy</c> ДО того, как ретранслятор выйдет, а
/// реализация (Main) ведёт его тем же путём, что веб-«Стоп»: прерывание хода человеком, без
/// фолбэка на следующую модель, с отметкой в истории.
/// </summary>
public interface IHumanTurnStop
{
    void StoppedByHuman(string sessionId);
}
