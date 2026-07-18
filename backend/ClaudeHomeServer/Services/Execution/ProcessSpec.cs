using System.Text;

namespace ClaudeHomeServer.Services.Execution;

// Спецификация запуска процесса в среде исполнения пользователя.
// WorkingDirectory — всегда ХОСТОВЫЙ путь: драйвер среды сам переводит его
// в свой вид (IPathMapper) при запуске.
public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool RedirectStdin { get; init; } = true;
    // Кодировка всех перенаправленных потоков (claude/skills — UTF-8 без BOM);
    // null — дефолт платформы (dev-серверы, терминал)
    public Encoding? StdioEncoding { get; init; }
    public bool EnableRaisingEvents { get; init; }
    // Метка убиваемости: в песочнице docker-клиент — лишь пайп к процессу,
    // и убить настоящий процесс можно только внутри контейнера — по этой метке
    public string? TurnId { get; init; }
}
