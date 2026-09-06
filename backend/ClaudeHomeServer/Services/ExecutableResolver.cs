namespace ClaudeHomeServer.Services;

// Поиск команды по PATH с подстановкой расширений PATHEXT — как это делает cmd:
// в каждом каталоге перебираются все расширения, и лишь потом берётся следующий.
//
// Вынесен из `Execution.LocalProcessRunner` (задача `57b5e9bc`, шаг 5): примитиву
// не нужны ни процессы, ни песочница, ни DI — это чистая функция от имени и PATH.
// До выноса `CodeGraph → Execution.LocalProcessRunner.ResolveExecutable("node")`
// был швом между вертикалями; теперь TypeScriptGraphProvider берёт примитив из
// спинки `Services.ExecutableResolver` и `CodeGraph` зависит только от корня Services.
//
// Тесты правила живут на `LocalProcessRunner.FindInPath(...)` и
// `LocalProcessRunner.ResolveExecutable(...)` в `LocalProcessRunnerPathTests`.
// Сам `LocalProcessRunner` — тонкая обёртка над `ExecutableResolver`
// (`LocalProcessRunner.cs:65+`), и обе функции тестов проверяют контракт
// сквозь неё; правки в `ExecutableResolver` сразу видны по красным тестам.
// Отдельный `ExecutableResolverTests` не заведён — тесты проверяют контракт
// через обёртку, как и для большинства других примитивов спинки.
public static class ExecutableResolver
{
    /// <summary>Развернуть имя исполняемого файла в полный путь по PATH+PATHEXT.
    /// На не-Windows и при наличии расширения/пути — возвращает как есть.</summary>
    public static string ResolveExecutable(string fileName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fileName)) return fileName;
        // Путь (хоть относительный) и имя с расширением ОС разбирает сама
        if (fileName.Contains('/') || fileName.Contains('\\') || Path.HasExtension(fileName))
            return fileName;

        return FindInPath(fileName,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT")) ?? fileName;
    }

    /// <summary>Поиск команды по каталогам PATH с подстановкой расширений PATHEXT.
    /// Значения приходят параметрами, чтобы правило проверялось тестом на любой ОС.</summary>
    public static string? FindInPath(string fileName, string? path, string? pathext)
    {
        var exts = (string.IsNullOrWhiteSpace(pathext) ? ".COM;.EXE;.BAT;.CMD" : pathext)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dirs = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawDir in dirs)
        {
            // Записи PATH нередко приходят в кавычках — Path.Combine на них спотыкается
            var dir = rawDir.Trim('"');
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir, fileName + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { break; }   // мусорная запись в PATH
            }
        }
        return null;
    }
}
