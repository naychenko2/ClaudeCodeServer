using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Состав инструментов серверов заметок и задач: гоняем настоящий node-процесс и спрашиваем
/// у него tools/list.
///
/// Заметки разрезаны на ядро (перечислить, найти текстом и по смыслу, прочитать, создать,
/// изменить, переместить) и модуль NOTES_ANNOTATIONS — комментарии к markdown-документам
/// плюс редкие операции (дневник, граф, обратные ссылки, удаление, промоут чекбокса,
/// подсказка заголовка, резолв [[ссылки]]). Модуль по умолчанию выключен: за две недели
/// наблюдений его инструменты не звали ни разу, а схемы висели в контексте каждого хода.
/// </summary>
[Trait("Category", "Integration")]
public class NotesAndTasksMcpServerToolsTests
{
    private static string? FindServerPath(string server)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", server, "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // tools/list у живого сервера с заданными env. null — node недоступен (тест скипается)
    private static IReadOnlyList<string>? ListTools(string server, string prefix,
        (string Key, string Value)[] env)
    {
        var serverPath = FindServerPath(server);
        Skip.If(serverPath is null, $"mcp/{server}/index.js не найден");

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Бэкенд тесту не нужен: tools/list считается из env, в сеть сервер не ходит
        psi.Environment[$"{prefix}_API_URL"] = "http://127.0.0.1:1";
        psi.Environment[$"{prefix}_API_TOKEN"] = "test";
        foreach (var (key, value) in env) psi.Environment[key] = value;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/list");
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
        }
    }

    private static readonly string[] CoreNotes =
    [
        "notes_list", "notes_search", "notes_semantic_search", "notes_read",
        "notes_create", "notes_update", "notes_move",
    ];

    [SkippableFact]
    public void БезМодуляКомментариев_ОстаётсяТолькоЯдро()
    {
        var tools = ListTools("notes-server", "NOTES", [("NOTES_ANNOTATIONS", "0")]);
        if (tools is null) return;

        tools.Should().BeEquivalentTo(CoreNotes);
    }

    [SkippableFact]
    public void СМодулемКомментариев_ЯдроНаМестеПлюсМодуль()
    {
        var tools = ListTools("notes-server", "NOTES", [("NOTES_ANNOTATIONS", "1")]);
        if (tools is null) return;

        // Ядро не смеет пострадать от разреза
        tools.Should().Contain(CoreNotes);
        tools.Should().Contain(["notes_annotate", "notes_annotations", "notes_reply",
            "notes_thread", "notes_set_status", "notes_daily", "notes_graph",
            "notes_backlinks", "notes_delete", "notes_promote_task", "notes_resolve",
            "notes_suggest_title"]);
    }

    [SkippableFact]
    public void UIХелперыЗадач_ИзСоставаЧатаУбраны()
    {
        // tasks_suggest_meta / tasks_normalize_title — хелперы формы задачи, их зовёт фронт
        // по REST; из чата их не звали ни разу
        var tools = ListTools("tasks-server", "TASKS", []);
        if (tools is null) return;

        tools.Should().NotContain("tasks_suggest_meta").And.NotContain("tasks_normalize_title");
        // Соседние инструменты на месте — разрез не задел ядро задач
        tools.Should().Contain(["tasks_list", "tasks_create", "tasks_update", "tasks_complete",
            "tasks_find_duplicate"]);
    }
}
