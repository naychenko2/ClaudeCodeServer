using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Стоп-список необратимых команд для режима прав «Авто»: режим разрешает shell-вызовы
// без карточки пользователя (обещание «действует сам»), и только узнаваемо необратимое —
// принудительное удаление, разрушительные операции git, pipe-to-shell, разметка дисков,
// выключение машины — по-прежнему спрашивает. Действует в ClaudeSession.DecidePermissionAsync
// строго ПОСЛЕ deny-правил проекта: явный запрет сильнее авто-разрешения.
//
// Как CoordinatorWriteGuard, эвристика КОНСЕРВАТИВНА: ловим узнаваемые опасные формы
// и их очевидные варианты (порядок флагов, sudo, длинные ключи), не претендуя на
// непробиваемость против нарочитого обхода. Ложное срабатывание («спросить» на безобидной
// команде) безопаснее пропуска — потому же флаг -D у git branch чувствителен к регистру:
// -d отказывается удалять неслитые ветки, это мягкое удаление.
public static class IrreversibleCommandGuard
{
    // Оба shell-инструмента CLI: на Windows claude зовёт то Bash, то PowerShell для одной
    // и той же просьбы (см. CoordinatorWriteGuard) — закрывать надо оба.
    private static readonly string[] ShellTools = ["Bash", "PowerShell"];

    public static bool IsShellTool(string toolName) =>
        Array.Exists(ShellTools, t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex IrreversiblePattern = new(
        @"\brm\s+(?=[^|;&\n]*-[a-z]*r)(?=[^|;&\n]*-[a-z]*f)"          // rm -rf/-fr/-r -f (флаги в любом виде)
        + @"|\brmdir\s+[^|;&\n]*/s\b"                                  // rmdir /s
        + @"|\bremove-item\b(?=[^|;&\n]*-recurse)(?=[^|;&\n]*-force)"  // Remove-Item -Recurse -Force
        + @"|\bgit\s+push\b(?=[^|;&\n]*(\s-f\b|--force(?!\S)))"        // git push -f/--force (но не --force-with-lease)
        + @"|\bgit\s+push\b(?=[^|;&\n]*(\s-d\b|--delete\b))"           // git push --delete (удаление ветки на ремоуте)
        + @"|\bgit\s+reset\b(?=[^|;&\n]*--hard\b)"                     // git reset --hard
        + @"|\bgit\s+clean\b(?=[^|;&\n]*-[a-z]*f)(?=[^|;&\n]*-[a-z]*d)" // git clean -fd (f и d в любом порядке)
        + @"|\bgit\s+branch\b(?=[^|;&\n]*(\s(?-i:-D)\b|--delete\b[^|;&\n]*--force\b))" // git branch -D (не -d)
        + @"|\b(curl|wget)\b[^|;&\n]*\|[^\n]*\b(bash|zsh|dash|ksh|sh)\b" // curl/wget … | sh (в т.ч. через sudo)
        + @"|\bmkfs(\.\w+)?\b"                                         // mkfs, mkfs.ext4
        + @"|\bdiskpart\b"                                             // diskpart
        + @"|\bdd\b[^|;&\n]*\bof=/dev/"                                // dd … of=/dev/… (запись на устройство)
        + @"|(?<!-)(?<!\b(dotnet|npm|yarn|pnpm|npx)\s(?:run\s)?)\bformat\b(?!-)" // format c: — но НЕ dotnet format, npm run format, --format, format-patch
        + @"|\bshutdown\b"
        + @"|\breboot\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Похожа ли команда на необратимую (стоп-список выше). null/пусто — не похожа.
    public static bool LooksIrreversible(string? command) =>
        !string.IsNullOrWhiteSpace(command) && IrreversiblePattern.IsMatch(command);
}
