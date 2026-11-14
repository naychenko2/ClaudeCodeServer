using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Шов «лента чата для чтения без побочек»: экран этапа (D1a) берёт им историю чата исполнителя.
// Отдельный от ISessionDirectory.GetHistoryAsync: тот на холодном чтении идёт через LoadAsync,
// а при недописанном history.json JsonFileStore убирает файл в .corrupt-бэкап — для ручки только
// на чтение это недопустимо. Здесь файл не трогается, недописанная хвостовая запись пропускается.
public interface IChatTranscriptReader
{
    // Записи ленты сессии по её id (не claudeSessionId). Пустой список — сессии или истории нет.
    // Не бросает на обрезанном/битом файле: отдаёт то, что удалось разобрать
    Task<IReadOnlyList<StoredMessage>> ReadAsync(string sessionId);
}
