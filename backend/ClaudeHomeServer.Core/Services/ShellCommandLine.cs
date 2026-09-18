namespace ClaudeHomeServer.Services;

// Сборка командной строки системного шелла — примитив спинки, общий у сторожей чатов
// (`WatchdogCommandRunner`) и пульта удалённых команд (`LocalShellCommandRunner`). Оба
// запускают «строку в шелле», и формула у них обязана быть ОДНА: дубль тут стоил прода.
//
// Суть фикса 01.09 (сторожа dd1fac4e/8ea8c9cc/3a091224): аргументы cmd.exe идут СЫРОЙ
// строкой «/s /c "команда"», а не через ArgumentList. .NET экранирует внутренние " как \",
// cmd этих правил не знает — команда с вложенными кавычками (powershell -NoProfile -Command
// "if …") разваливалась: cmd сносил первую внешнюю кавычку, powershell получал литеральные
// кавычки, исполнял тело как строковый литерал и ЭХАЛ его в stdout с exit 0 — ложное
// срабатывание. Ключ /s заставляет cmd снять только внешние кавычки, внутренние доходят
// до команды как есть.
//
// Unix-ветка кавычек не касается вовсе: аргументы уходят массивом в execve без
// экранирования, поэтому там достаточно пары «-lc» + команда (логин-шелл ради PATH).
public static class ShellCommandLine
{
    /// <summary>Имя исполняемого файла шелла по платформе цели.</summary>
    public static string ShellFileName(bool windows) => windows ? "cmd.exe" : "bash";

    /// <summary>Сырая строка аргументов cmd.exe: «/s /c "команда"». Только Arguments, не ArgumentList.</summary>
    public static string WindowsCmdArguments(string command) => $"/s /c \"{command}\"";

    /// <summary>Аргументы bash списком: логин-шелл, команда отдельным элементом.</summary>
    public static string[] UnixShellArgs(string command) => ["-lc", command];
}
