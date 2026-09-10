using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services;

// Годится ли значение как имя файла/папки транскрипта. Проверять обязательно: ключ
// claudeSessionId попадает в sessions.json не только от CLI, но и снаружи — параметром
// resumeSessionId в POST /sessions|/chats|/personas/{id}/chats, а сам файл ещё и правится
// руками при восстановлении чатов. Ниже по этому ключу удаляются файлы и папки, поэтому
// белый список, а не Path.GetFileName (тот пропустил бы «..»).
//
// Этап 5, волна 3: примитив поднят в спину по образцу `TranscriptRoots`/`SafePath` — это
// чистая валидация идентификатора, к домену LLM отношения не имеющая, а нужна она и
// вертикали `Dossiers` (захват коммита сверяет трейлеры CCS-Session/CCS-Task), и Main.
//
// ⚠ Техдолг на один шаг: одноимённый `Services.Llm.TranscriptMigrator.IsSafeSessionId`
// со своей копией регулярки ОСТАВЛЕН как есть — папку `Services/Llm` ведёт соседняя линия
// (её вынос в отдельный `.csproj` идёт параллельно), и правка её файлов этой задачей
// запрещена прямо. Свернуть копию в форвардер на этот примитив — двухстрочное дело для
// той линии; пока источников правды два, регулярка в них обязана совпадать посимвольно.
public static class SessionIdGuard
{
    private static readonly Regex SafeSessionId = new(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    public static bool IsSafe([NotNullWhen(true)] string? claudeSessionId) =>
        claudeSessionId is not null && SafeSessionId.IsMatch(claudeSessionId);
}
