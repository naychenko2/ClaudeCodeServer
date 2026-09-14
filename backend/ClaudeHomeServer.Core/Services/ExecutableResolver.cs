namespace ClaudeHomeServer.Services;

// Поиск команды по PATH с подстановкой расширений PATHEXT — как это делает cmd:
// в каждом каталоге перебираются все расширения, и лишь потом берётся следующий.
//
// Вынесен из `Execution.LocalProcessRunner` (задача `57b5e9bc`, шаг 5): примитиву
// не нужны ни процессы, ни песочница, ни DI — это чистая функция от имени и PATH.
//
// В Этап 3 (волна 1, feature/etap3-codegraph-extract) поднят из Main в Core:
// примитив нужен и `CodeGraph.TypeScriptGraphProvider` (вызов Node-экстрактора),
// и `Execution.LocalProcessRunner`; когда CodeGraph станет отдельным `.csproj`,
// он не сможет сослаться на `Services.ExecutableResolver` (Main), а шов на Core
// уже есть. Дубликата в Core/Main нет: реализация единственная, локальные
// обёртки (`LocalProcessRunner`) зовут её из Core.
//
// Тесты правила живут на `LocalProcessRunner.FindInPath(...)` и
// `LocalProcessRunner.ResolveExecutable(...)` в `LocalProcessRunnerPathTests`:
// правило проверяется тестом на любой ОС через перегрузку с параметрами PATH/PATHEXT.
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