using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Состав инструментов wsp-сервера (mcp/workspace-server/index.js) при разных env хода:
/// гоняем настоящий node-процесс и спрашиваем у него tools/list — так проверяются оба гейта
/// (секции и WORKSPACE_WRITE) вместе с их взаимодействием, которое из C# не видно.
///
/// Регресс бага прода: chats_send/chats_create лежали в WRITE_TOOLS и потому исчезали на ходах
/// без интента записи (гейт по тексту хода) — персона-постановщик видела «функции то есть,
/// то нет» с переподключением сервера. Набор chats-инструментов обязан зависеть ТОЛЬКО от
/// смонтированной секции chats.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceMcpServerToolsTests
{
    // Путь к серверу: поднимаемся от bin тестов до корня репозитория
    private static string? FindServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "workspace-server", "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // tools/list у живого сервера с заданными env. null — node недоступен (тест скипается)
    private static IReadOnlyList<string>? ListTools(string sections, string write)
    {
        var serverPath = FindServerPath();
        Skip.If(serverPath is null, "mcp/workspace-server/index.js не найден");

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
        psi.Environment["WORKSPACE_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["WORKSPACE_API_TOKEN"] = "test";
        psi.Environment["WORKSPACE_SECTIONS"] = sections;
        psi.Environment["WORKSPACE_WRITE"] = write;

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

    [SkippableFact]
    public void СекцияChats_БезИнтентаЗаписи_ЧатовыеИнструментыНаМесте()
    {
        var tools = ListTools("projects,files,knowledge,search,chats", write: "0");
        if (tools is null) return;   // скип отработал внутри

        // Главное: состав chats не зависит от write-интента хода
        tools.Should().Contain(["chats_list", "chats_history", "chats_create", "chats_send", "chats_update"]);
        // Гейт при этом продолжает работать для тяжёлых write-схем
        tools.Should().NotContain("files_write").And.NotContain("projects_create");
    }

    [SkippableFact]
    public void СекцияChats_СИнтентомЗаписи_ТотЖеНаборЧатов()
    {
        var readTurn = ListTools("projects,files,knowledge,search,chats", write: "0");
        var writeTurn = ListTools("projects,files,knowledge,search,chats", write: "1");
        if (readTurn is null || writeTurn is null) return;

        // Никакого «мерцания» между ходами: chats-инструменты одинаковы при WRITE=0 и WRITE=1
        readTurn.Where(t => t.StartsWith("chats_"))
            .Should().BeEquivalentTo(writeTurn.Where(t => t.StartsWith("chats_")));
    }

    [SkippableFact]
    public void БезСекцииChats_ЧатовыхИнструментовНет()
    {
        // Секция — единственный гейт чатов: не смонтирована → нет ни read, ни write
        var tools = ListTools("projects,files,knowledge,search", write: "1");
        if (tools is null) return;

        tools.Should().NotContain(t => t.StartsWith("chats_"));
    }
}
