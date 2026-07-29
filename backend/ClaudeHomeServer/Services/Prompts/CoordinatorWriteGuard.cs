using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Prompts;

// Гард «координатор не пишет код сам» (Э7-фикс, находка Веры Major №1): --disallowedTools
// закрывает Edit/Write/NotebookEdit, но координатор пишет содержимое файла ЧЕРЕЗ shell —
// `cat > file << EOF`, и CLI на Windows не привязан к одному инструменту: то Bash, то
// PowerShell (проверено вживую). Закрыть Bash/PowerShell целиком нельзя — тем же
// инструментом координатор гоняет сборку/тесты на стадии проверки. Поэтому гейт — по
// КОМАНДЕ: узнаваемые способы записать содержимое файла — deny, всё остальное (dotnet
// build, git status, ls) проходит как обычно.
//
// Эвристика КОНСЕРВАТИВНА, как WriteIntentGate: ловит подтверждённый вживую способ обхода
// и его очевидные варианты, не претендуя на полную непробиваемость против нарочитого
// обхода через инлайн-интерпретаторы (python -c, node -e и т.п.) — координатор в этом
// режиме не состязательный противник, а модель, которую иногда тянет срезать угол.
//
// ВАЖНО: этот гейт срабатывает только когда CLI реально спрашивает разрешение на
// инструмент (sdk_control_request) — а спрашивает он не в любом --permission-mode.
// В acceptEdits/bypassPermissions CLI одобряет Bash клиентски, минуя сервер (проверено
// вживую той же командой из находки Веры) — поэтому SetTeamImplementAsync принудительно
// переводит координатора в режим auto/default, где проверка команды реально работает.
public static class CoordinatorWriteGuard
{
    // Инструменты shell, которыми можно обойти запрет Edit/Write (проверено вживую:
    // claude CLI на Windows вызывает то Bash, то PowerShell для одной и той же просьбы).
    private static readonly string[] ShellTools = ["Bash", "PowerShell"];

    public static bool IsShellTool(string toolName) =>
        Array.Exists(ShellTools, t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex WritePattern = new(
        @"<<-?\s*['""]?[A-Za-z_]\w*['""]?" +                             // heredoc: cat > f << 'EOF'
        @"|\b(echo|printf|cat)\b[^\n]*(?<!\d)>{1,2}(?!&)" +               // echo/printf/cat + редирект в файл
        @"|\btee\b" +                                                     // ... | tee file
        @"|\bsed\b[^\n]*-i\b" +                                           // sed -i (правка на месте)
        @"|\b(Set-Content|Add-Content|Out-File|New-Item|Copy-Item)\b",    // PowerShell: запись содержимого
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Признаки записи содержимого в файл через shell-команду. null/пусто — не похоже на запись.
    public static bool LooksLikeFileWrite(string? command) =>
        !string.IsNullOrWhiteSpace(command) && WritePattern.IsMatch(command);
}
