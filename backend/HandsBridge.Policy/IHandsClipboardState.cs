namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Состояние текстового буфера обмена, которое гейт сверяет при чтении (<c>clipboard_text</c>,
/// D1c). Мост сам наполняет его на каждом действии ввода в ходе: <see cref="SequenceNumber"/> —
/// текущий номер последовательности Windows (<c>GetClipboardSequenceNumber</c>, <c>uint</c>),
/// <see cref="SequenceNumberAtLastOwnInput"/> — номер, зафиксированный после последнего собственного
/// ввода моста (<c>write</c>), <see cref="HadOwnInput"/> — был ли такой ввод, <see cref="FormatNames"/> —
/// имена форматов, которые сейчас в буфере.
/// </summary>
public interface IHandsClipboardState
{
    /// <summary>
    /// Текущий номер последовательности буфера. <c>uint</c> (как <c>GetClipboardSequenceNumber</c>):
    /// переполнение обнуляет счётчик, поэтому гейт сравнивает номера «отличается от», а не «больше».
    /// </summary>
    uint SequenceNumber { get; }

    /// <summary>
    /// Номер последовательности, зафиксированный мостом после последнего собственного ввода
    /// (<c>write</c>); 0 — собственного ввода в ходе ещё не было.
    /// </summary>
    uint SequenceNumberAtLastOwnInput { get; }

    /// <summary>
    /// Был ли в ходе собственный ввод моста. <see cref="SequenceNumberAtLastOwnInput"/> = 0 сам по
    /// себе не доказывает «свежесть»: если мост ещё ничего не вписывал, читать нечего.
    /// </summary>
    bool HadOwnInput { get; }

    /// <summary>Имена форматов, которые сейчас в буфере.</summary>
    IReadOnlyCollection<string> FormatNames { get; }
}
