using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.ImageEditor;

// Намерение запроса, которое меняет выбор модели. Сейчас одно — «стереть отмеченное»: чистый
// инпейнт по маске (FLUX Fill) рисует в маске то, что написано в запросе, и удалять не умеет.
// Фронт держит копию правила (isRemovalPrompt в imageEditor/format.ts) — котировка выбирает
// модель до запуска; менять оба места вместе.
public static partial class EditIntent
{
    // Удаление, а не замена: «убери лампу» — да, «убери лампу и поставь вазу» — нет
    public static bool IsRemoval(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        return RemovalPattern().IsMatch(prompt) && !OtherActionPattern().IsMatch(prompt);
    }

    [GeneratedRegex(@"(?<!\p{L})(удал|убер|убра|сотр|стер|стира|избав|remove|erase|delete|get rid)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemovalPattern();

    [GeneratedRegex(@"(?<!\p{L})(добав|замен|встав|нарисуй|дорисуй|постав|полож|сдела|превра|перекрас|add|replace|insert|put|draw|turn|make)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherActionPattern();
}
