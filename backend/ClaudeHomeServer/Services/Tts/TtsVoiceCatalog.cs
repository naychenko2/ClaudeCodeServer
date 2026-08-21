namespace ClaudeHomeServer.Services.Tts;

// Белый список голосов синтеза (SpeechKit v3) — по нему проверяется имя из конфига.
//
// Состав проверен живыми запросами 20.08.2026: каждое имя отвечает 200 на v3. Восемь
// голосов достались от v1 (alena, jane, omazh, marina, filipp, zahar, ermil, madirus),
// остальные живут только в v3. `alena` в публичном списке голосов документации не
// значится, но API её принимает — держим, не закладываясь на вечность.
//
// Роли (амплуа) и человекочитаемые подписи голосов сюда НЕ добавлять, пока их некому
// применять: голос на весь инстанс один и роль ему задать неоткуда. Их место — рядом
// с личными голосами персон, где у них появится потребитель.
public static class TtsVoiceCatalog
{
    // Голос, на который падаем при незнакомом имени в конфиге: есть и в v1, и в v3
    public const string Default = "zahar";

    // madirus и madi_ru — одно и то же имя, v3 принимает оба написания
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "alena", "jane", "omazh", "marina", "filipp", "zahar", "ermil", "madirus", "madi_ru",
        "dasha", "julia", "lera", "masha", "alexander", "kirill", "anton",
    };

    public static bool IsKnown(string? voice) =>
        !string.IsNullOrWhiteSpace(voice) && Known.Contains(voice.Trim());

    // Незнакомый голос не повод глушить озвучку: опечатка в конфиге стоила бы всей речи
    // (фронт запомнил бы отказ и ушёл на голос браузера до конца сессии)
    public static string Resolve(string? voice) => IsKnown(voice) ? voice!.Trim() : Default;
}
