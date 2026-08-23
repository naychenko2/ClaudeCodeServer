namespace ClaudeHomeServer.Models;

// Как звучит персона в голосовом режиме. null у персоны — говорит голосом инстанса
// (Yandex:SpeechKit:Voice), то есть ровно как было до личных голосов.
//
// Все поля проверяются в VoiceResolver, а не здесь: стор переживает и переименование
// голоса в SpeechKit, и правку json руками, и в обоих случаях озвучка обязана продолжать
// работать — молча на дефолте, а не отказом.
public class PersonaVoice
{
    // Имя голоса SpeechKit (белый список — TtsVoiceCatalog). Пусто = голос не выбран
    public string? Voice { get; set; }

    // Амплуа голоса («good», «strict», «friendly», «evil», «whisper»); null — нейтрально.
    // Строкой, а не enum: enum в сторе персон потянул бы за собой версию схемы бэкапа,
    // потому что старый бинарь падает на десериализации незнакомого значения enum'а.
    public string? Role { get; set; }

    // Темп речи, 0.1–3.0; null — из конфига инстанса. Именно nullable: 0 у SpeechKit
    // не «по умолчанию», а ошибка 400, и весь ход уехал бы на голос браузера
    public double? Speed { get; set; }

    // Пустой объект (ни голоса, ни роли, ни темпа) равнозначен отсутствию: так форма
    // сбрасывает голос персоны к дефолту, не изобретая отдельного маркера
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Voice) && string.IsNullOrWhiteSpace(Role) && Speed is null;
}
