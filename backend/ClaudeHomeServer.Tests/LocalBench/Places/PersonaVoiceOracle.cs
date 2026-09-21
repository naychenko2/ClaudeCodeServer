using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Tts;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>persona-voice</c> (Батарея II, ось A).
///
/// Контракт задан промптом в <c>PersonasController.AiVoice</c>: ОДНА строка — ключ голоса
/// либо ключ голоса и амплуа через пробел; «none», если не подошёл ни один; никаких
/// пояснений.
///
/// Ключ ищется ПРОДУКТОВЫМ разбором <see cref="PersonasController.ParseVoiceAnswer"/>:
/// он терпим к болтовне вокруг (ищет по границам слова), и свой парсер дал бы валидность,
/// которой у места нет. Но терпимость не безгранична: ответ, в котором названы ДВА разных
/// голоса, продукт молча берёт первым по тексту — для человека это выбор наугад, и оракул
/// считает такой ответ нарушением, хотя продукт его переживает.
///
/// «none» — валидный исход контракта (у места он отдельная ветка ответа: 200 с пустым
/// голосом), но для замера бесполезный: считаем его валидным и отмечаем в результате.
/// </summary>
public static class PersonaVoiceOracle
{
    /// <summary>
    /// Потолок длины ответа: «одной строкой» — это ключ и, может быть, амплуа. Ответ
    /// длиннее — рассуждение, из которого продукт выковыряет первое попавшееся имя.
    /// </summary>
    public const int MaxAnswerChars = 64;

    /// <summary>Чем нарушен контракт. null — ответ валиден.</summary>
    public static string? Violation(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return "пустой ответ";

        var answer = rawAnswer.Trim();
        var picked = PersonasController.ParseVoiceAnswer(answer);

        if (picked is null)
        {
            // «none» без голоса — законный ответ «не подошёл никто».
            return answer.Contains("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : "голос не распознан — в ответе нет ключа из белого списка";
        }

        if (!TtsVoiceCatalog.IsKnown(picked.Value.Voice))
            return $"голос вне белого списка: «{picked.Value.Voice}»";

        // Два разных голоса в одном ответе: продукт возьмёт первый по тексту, то есть
        // выберет за модель — а место существует ровно ради выбора.
        var mentioned = Voices(answer);
        if (mentioned.Count > 1)
            return $"названо несколько голосов: {string.Join(", ", mentioned)}";

        if (answer.Length > MaxAnswerChars)
            return $"ответ длиннее {MaxAnswerChars} символов — не одна строка, а рассуждение";

        // Амплуа необязательно, но названное обязано поддерживаться этим голосом.
        // Продуктовый разбор чужую роль просто НЕ ВИДИТ (он ищет среди поддержанных), и
        // «zahar whisper» доедет до места как голос без амплуа — просьба модели молча
        // потеряется. Поэтому роль ищется по всему каталогу ролей, а не по разбору.
        var foreign = ForeignRole(answer, picked.Value.Voice);
        if (foreign is not null)
            return $"амплуа «{foreign}» не поддержано голосом «{picked.Value.Voice}»";

        return null;
    }

    /// <summary>Голос, который место вынет из ответа (для колонки результата).</summary>
    public static (string Voice, string? Role)? Picked(string? rawAnswer) =>
        string.IsNullOrWhiteSpace(rawAnswer) ? null : PersonasController.ParseVoiceAnswer(rawAnswer);

    // Все амплуа, какие вообще есть у голосов каталога: по ним видно, что модель назвала
    // именно роль, а не случайное слово.
    private static readonly HashSet<string> AllRoles =
        new(TtsVoiceCatalog.All.SelectMany(v => v.Roles), StringComparer.OrdinalIgnoreCase);

    // Названное в ответе амплуа, которого выбранный голос не тянет. null — такого нет.
    private static string? ForeignRole(string answer, string voice) =>
        System.Text.RegularExpressions.Regex.Matches(answer, "[A-Za-z_]+")
            .Select(m => m.Value)
            .FirstOrDefault(w => AllRoles.Contains(w) && !TtsVoiceCatalog.SupportsRole(voice, w));

    // Все канонические голоса, названные в ответе, — по тем же границам слова, по которым
    // их ищет продукт.
    private static IReadOnlyList<string> Voices(string answer)
    {
        var found = new List<string>();
        foreach (var word in System.Text.RegularExpressions.Regex.Matches(answer, "[A-Za-z_]+")
                     .Select(m => m.Value))
        {
            var voice = TtsVoiceCatalog.Canonical(word);
            if (voice is not null && !found.Contains(voice, StringComparer.Ordinal))
                found.Add(voice);
        }
        return found;
    }
}
