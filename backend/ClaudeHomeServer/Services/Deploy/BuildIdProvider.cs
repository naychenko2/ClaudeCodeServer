namespace ClaudeHomeServer.Services.Deploy;

/// <summary>
/// Идентификатор текущей выкатки — то, что уезжает в заголовок <c>X-Build</c> ответа
/// <c>GET /api/health</c> (ADR-010). Признак «поднялся именно новый экземпляр» нужен
/// агенту: файл на диске сам по себе ничего не доказывает — он лежит там независимо от
/// того, какой exe сейчас в памяти, а заголовок отдаёт живой процесс.
///
/// Файл <c>build-id.txt</c> кладёт рядом с exe агент выкатки на шаге переключения.
/// Значение читается ОДИН РАЗ при старте процесса: оно описывает именно этот экземпляр,
/// и перечитывание файла на каждом пинге отдавало бы чужой идентификатор.
/// Файла нет или значение не прошло проверку — заголовка просто не будет.
/// </summary>
public sealed class BuildIdProvider
{
    public const string FileName = "build-id.txt";
    public const string HeaderName = "X-Build";

    public string? BuildId { get; }

    public BuildIdProvider() : this(AppContext.BaseDirectory) { }

    public BuildIdProvider(string baseDir) => BuildId = Read(baseDir);

    internal static string? Read(string baseDir)
    {
        try
        {
            var path = Path.Combine(baseDir, FileName);
            if (!File.Exists(path)) return null;
            // Первая строка: агенту удобно дописывать в файл пояснения, нам нужен только id
            var value = File.ReadLines(path).FirstOrDefault()?.Trim();
            // Содержимое файла едет в HTTP-заголовок: без строгой проверки CR/LF в нём
            // означал бы инъекцию заголовков
            return DeployValidation.IsValidBuildId(value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
