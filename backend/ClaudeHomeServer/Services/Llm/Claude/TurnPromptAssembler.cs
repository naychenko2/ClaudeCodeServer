using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Склейка системного промпта хода из секций — ЕДИНСТВЕННЫЙ путь сборки.
/// Раньше текст копился в одной переменной тринадцатью повторами
/// «пусто ? кусок : накопленное + \n\n + кусок»; теперь каждый кусок кладётся секцией,
/// а отправляемый текст собирается отсюда. Двух параллельных путей («что показали
/// пользователю» и «что отправили модели») не остаётся — разойтись им негде.
/// </summary>
public static class TurnPromptAssembler
{
    // Разделитель между слоем продукта и слоем персоны: персона — отдельный голос,
    // а не очередная подсказка (порядок и разделители менять нельзя — это ровно то,
    // что уходит в --append-system-prompt)
    public const string PersonaSeparator = "\n\n---\n\n";

    /// <summary>
    /// Текст для --append-system-prompt. Учитываются только секции Kind = "system":
    /// текст хода (Kind = "turn") идёт в stdin отдельным сообщением, а не в промпт.
    /// </summary>
    public static string Combine(IReadOnlyList<PromptSectionDto> sections, string? personaLayer)
    {
        var body = string.Join("\n\n", sections
            .Where(s => s.Kind == "system" && !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => s.Text));

        if (string.IsNullOrWhiteSpace(personaLayer)) return body;
        return string.IsNullOrWhiteSpace(body) ? personaLayer : body + PersonaSeparator + personaLayer;
    }
}
