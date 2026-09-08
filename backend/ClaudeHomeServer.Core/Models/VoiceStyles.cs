namespace ClaudeHomeServer.Models;

// Стиль озвучки чата (Session.VoiceStyle) — единственная точка значений: магических
// строк "talk"/"digest" в коде быть не должно. Те же значения на фронте — union-тип
// VoiceStyle в lib/voiceStyle.ts.
public static class VoiceStyles
{
    // Разговор: ответ целиком короткий и без разметки, читается вслух по мере набора.
    public const string Talk = "talk";
    // Вслух: ответ обычный и полный, вслух идёт только выжимка из маркера <voice> в его конце.
    public const string Digest = "digest";

    public static bool IsKnown(string? value) => value is Talk or Digest;

    // Нормализация применяется на ЗАПИСИ (сеттер SessionManager), поэтому читатели
    // тривиально безопасны. Пустое, легаси и битое значение из стора — молча talk;
    // явный мусор из API отбивается 400 в контроллере ДО этого вызова.
    public static string Normalize(string? value) => IsKnown(value) ? value! : Talk;
}
