namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Состояние текстового буфера обмена, которое гейт сверяет при чтении (<c>clipboard_text</c>,
/// D1c). Мост сам наполняет его: <see cref="SequenceNumber"/> — текущий номер последовательности
/// Windows (<c>GetClipboardSequenceNumber</c>), <see cref="SequenceNumberAtLastOwnInput"/> — номер,
/// зафиксированный после последнего собственного ввода моста (<c>write</c>), <see cref="FormatNames"/> —
/// имена форматов, которые сейчас в буфере.
/// </summary>
public interface IHandsClipboardState
{
    /// <summary>Текущий номер последовательности буфера; растёт на каждом изменении.</summary>
    int SequenceNumber { get; }

    /// <summary>
    /// Номер последовательности, зафиксированный мостом после последнего собственного ввода.
    /// Буфер «свежий» (чужое изменение) только если <see cref="SequenceNumber"/> больше этого.
    /// </summary>
    int SequenceNumberAtLastOwnInput { get; }

    /// <summary>Имена форматов, которые сейчас в буфере.</summary>
    IReadOnlyCollection<string> FormatNames { get; }
}
