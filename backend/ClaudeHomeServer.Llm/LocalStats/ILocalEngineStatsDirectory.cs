namespace ClaudeHomeServer.Services.Llm;

// Справочник дорожек статистики по движкам: имя дорожки → снимок и кольцо истории.
// Дорожка "main" — основная модель (vLLM); именно её отдаёт ILocalModelStatsReader.
public interface ILocalEngineStatsDirectory
{
    // Имена дорожек в порядке регистрации ("main" — первой).
    IReadOnlyList<string> Names { get; }

    // null — дорожки с таким именем нет.
    LocalModelStatsCurrent? CurrentOf(string name);

    // Пустой список — дорожки нет или в окне нет точек.
    IReadOnlyList<LocalModelStatsPoint> HistoryOf(string name, TimeSpan window, long? sinceUnixMs = null);
}
