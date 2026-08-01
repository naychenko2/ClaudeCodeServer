using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Сторож длины поля instructions ответа initialize у personas-server и tasks-server:
/// claude CLI усекает instructions примерно на 2 КБ (проверено живой пробой — из 6 КБ
/// маркеров доехали первые ~2050 символов), а справочники серверов уезжают именно туда.
/// Запас до лимита CLI у personas-server — единицы символов; падаем заранее на ~1950,
/// чтобы будущая правка справочника не тихо обрезалась у модели («… [truncated]»),
/// а ловилась тут. У personas-server справочник зависит от WRITE/BINDINGS-флагов и от
/// того, задан ли PROJECT_ID (PROJECT_HINT) — гоняем все комбинации.
/// </summary>
[Trait("Category", "Integration")]
public class McpInstructionsLengthGuardTests
{
    private const int MaxInstructionsLength = 1950;

    private static string? FindServerPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // instructions ответа initialize у живого node-процесса сервера с заданными env. null — node недоступен
    private static string? GetInstructions(string serverPath, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Без явной UTF-8 кодировки .NET на Windows читает/пишет stdio процесса в системной
            // кодовой странице консоли — кириллица в instructions превращается в мнимо более
            // длинную мешанину (каждый байт многобайтовой UTF-8-последовательности читается
            // как отдельный символ), и сторож ложно падает на корректном справочнике.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var (key, value) in env) psi.Environment[key] = value;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на initialize");
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.GetProperty("result").GetProperty("instructions").GetString();
        }
    }

    public static IEnumerable<object[]> PersonasFlagCombinations()
    {
        foreach (var write in new[] { "0", "1" })
        foreach (var bindings in new[] { "0", "1" })
        foreach (var withProjectId in new[] { false, true })
            yield return [write, bindings, withProjectId];
    }

    [SkippableTheory]
    [MemberData(nameof(PersonasFlagCombinations))]
    public void PersonasServer_ЛюбаяКомбинацияФлагов_InstructionsВПределахЗапаса(
        string write, string bindings, bool withProjectId)
    {
        var serverPath = FindServerPath(Path.Combine("mcp", "personas-server", "index.js"));
        Skip.If(serverPath is null, "mcp/personas-server/index.js не найден");

        var env = new Dictionary<string, string>
        {
            ["PERSONAS_API_URL"] = "http://127.0.0.1:1",
            ["PERSONAS_API_TOKEN"] = "test",
            ["PERSONAS_WRITE"] = write,
            ["PERSONAS_BINDINGS"] = bindings,
        };
        if (withProjectId) env["PERSONAS_PROJECT_ID"] = Guid.NewGuid().ToString();

        var instructions = GetInstructions(serverPath!, env);
        if (instructions is null) return;   // скип отработал внутри

        instructions.Length.Should().BeLessOrEqualTo(MaxInstructionsLength,
            $"WRITE={write} BINDINGS={bindings} projectId={(withProjectId ? "guid" : "нет")}: " +
            "claude CLI усекает instructions за пределами ~2 КБ — запас на будущие правки исчерпан");
    }

    [SkippableFact]
    public void TasksServer_InstructionsВПределахЗапаса()
    {
        var serverPath = FindServerPath(Path.Combine("mcp", "tasks-server", "index.js"));
        Skip.If(serverPath is null, "mcp/tasks-server/index.js не найден");

        var instructions = GetInstructions(serverPath!, new Dictionary<string, string>
        {
            ["TASKS_API_URL"] = "http://127.0.0.1:1",
            ["TASKS_API_TOKEN"] = "test",
        });
        if (instructions is null) return;

        instructions.Length.Should().BeLessOrEqualTo(MaxInstructionsLength);
    }
}
