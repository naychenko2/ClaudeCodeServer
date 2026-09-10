using System.Text;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Сборка транскрипта сессии для LLM: реплики пользователя/Claude + однострочные пометки
// об инструментах и файлах; thinking и метаданные пропускаются.
//
// Этап 5, волна 3: чистая функция переехала из `SessionSummaryService` (корень `Services/`
// в Main) в спину — её зовут ВОСЕМЬ мест из трёх сборок: сам конспект сессии,
// `ChatTaskExtractionService`, `ChatDigestService`, `SessionManager` (два бюджета), а также
// вертикали `Memory` (авто-память персон и команды) и `Dossiers` (транскрипт под паспорт).
// После выноса Memory/Dossiers в отдельные `.csproj` держать её в Main значило бы связь
// «вертикаль → Main», которую сторож границ запрещает, а компилятор не собирает вовсе.
//
// `SessionSummaryService.BuildTranscript` остался тонким форвардером: у него семь
// вызывающих внутри Main (в том числе в `Services/Llm`, которую ведёт соседняя линия),
// и переименование их ничего бы не дало. Тот же приём, что `FileService.SafeJoin` →
// `SafePath.Join` (см. «Соглашения» в CLAUDE.md): из вертикали зови Core-примитив
// напрямую, обращение через фасад Main — это ссылка на чужую сборку.
public static class SessionTranscript
{
    // Переполнение бюджета — голова (цель сессии) + хвост (развязка), середина сокращается.
    public static string Build(IReadOnlyList<StoredMessage> messages, int budget)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m)
            {
                case StoredUserMessage u when !string.IsNullOrWhiteSpace(u.Text):
                    sb.AppendLine("Пользователь:");
                    sb.AppendLine(u.Text.Trim());
                    sb.AppendLine();
                    break;
                // Текст сабагента (ParentToolUseId != null) — не реплика Claude в диалоге
                case StoredTextMessage { ParentToolUseId: null } t when !string.IsNullOrWhiteSpace(t.Text):
                    sb.AppendLine("AI:");
                    sb.AppendLine(t.Text.Trim());
                    sb.AppendLine();
                    break;
                case StoredToolUseMessage tu when !string.IsNullOrEmpty(tu.Name):
                    sb.AppendLine($"[инструмент {tu.Name}]");
                    break;
                case StoredFileChangedMessage f:
                    sb.AppendLine($"[изменён файл {f.Path} +{f.Added}/-{f.Removed}]");
                    break;
            }
        }
        var text = sb.ToString().Trim();
        if (text.Length <= budget) return text;
        var head = budget / 5;
        var tail = budget - head;
        return text[..head] + "\n\n[…транскрипт сокращён…]\n\n" + text[^tail..];
    }
}
