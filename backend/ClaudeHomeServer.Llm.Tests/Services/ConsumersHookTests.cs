using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Хук PreToolUse на Edit|Write: потребители правленого файла приходят в ход additionalContext.
// Гоняем настоящий скрипт из файла настроек против заглушки сервера на loopback: хук не должен
// ни блокировать правку, ни тормозить ход, поэтому каждое «молчание» проверяем отдельно.
public class ConsumersHookTests
{
    private sealed record Run(string Stdout, int Exit, TimeSpan Elapsed);

    private static string SettingsPath() =>
        ClaudeRuntimeSettings.HooksOffArgs(LocalProcessRunner.Instance).ToList()[1];

    private static string ScriptPath() =>
        Path.Combine(Path.GetDirectoryName(SettingsPath())!, ClaudeRuntimeSettings.ConsumersHookScriptName);

    // Заглушка сервера: отвечает на каждый запрос handler'ом (статус, тело, задержка)
    private sealed class Stub : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public List<string> Requests { get; } = [];

        public Stub(Func<string, (int Status, string Body, int DelayMs)> handler)
        {
            var port = new Random().Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    var (status, body, delay) = handler(ctx.Request.Url!.PathAndQuery);
                    lock (Requests) Requests.Add(ctx.Request.Url!.PathAndQuery);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (delay > 0) await Task.Delay(delay);
                            var bytes = Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(bytes);
                            ctx.Response.Close();
                        }
                        catch { /* клиент ушёл по таймауту */ }
                    });
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    // Проект уникален на тест: пауза после сбоя ключуется проектом и иначе переходила бы между тестами
    private readonly string _project = "proj-" + Guid.NewGuid().ToString("N");

    private Run RunHook(string? api, string session, string file, string cwd = "/work/tree",
        string? token = "tok", string? project = null)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(ScriptPath());
        psi.Environment.Remove("CCS_HOOK_API");
        psi.Environment.Remove("CCS_HOOK_TOKEN");
        psi.Environment.Remove("CCS_HOOK_PROJECT");
        if (api is not null) psi.Environment["CCS_HOOK_API"] = api;
        if (token is not null) psi.Environment["CCS_HOOK_TOKEN"] = token;
        psi.Environment["CCS_HOOK_PROJECT"] = project ?? _project;
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(new
        {
            session_id = session, cwd, tool_input = new { file_path = Path.Combine(cwd, file) },
        }));
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return new Run(stdout, proc.ExitCode, sw.Elapsed);
    }

    private static string Neighbors(int count, int total = -1, int tests = 0)
    {
        var items = Enumerable.Range(0, count).Select(i => new
        {
            id = $"f{i}", fqn = $"src/Consumer{i}.tsx", location = i < tests ? $"src/__tests__/C{i}.test.tsx" : $"src/Consumer{i}.tsx",
            kind = "File", direction = "in", relation = "References", confidence = "",
        });
        return JsonSerializer.Serialize(new { node = new { }, neighbors = items, total = total < 0 ? count : total });
    }

    private static string NewSession() => "s" + Guid.NewGuid().ToString("N");

    private static string[] Context(Run run)
    {
        var ctx = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("hookSpecificOutput");
        ctx.GetProperty("hookEventName").GetString().Should().Be("PreToolUse");
        return ctx.GetProperty("additionalContext").GetString()!.Split('\n');
    }

    [Fact]
    public void Настройки_ХукНаEditWriteСтоитРядомСХукомBash()
    {
        if (OperatingSystem.IsWindows()) return;
        var hooks = JsonDocument.Parse(File.ReadAllText(SettingsPath())).RootElement
            .GetProperty("hooks").GetProperty("PreToolUse");
        hooks.EnumerateArray().Select(h => h.GetProperty("matcher").GetString())
            .Should().Equal("Bash", "Edit|Write");
    }

    [Fact]
    public void ПерваяПравка_ОтдаётПотребителейИТесты_ПовторнаяМолчит()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(3, tests: 1), 0));
        var session = NewSession();

        var first = RunHook(stub.Url, session, "src/pages/WorkspacePage.tsx");
        var lines = Context(first);
        lines[0].Should().Contain("src/pages/WorkspacePage.tsx");
        lines.Should().Contain(l => l.Contains("[тест]") && l.Contains("__tests__"));
        lines.Should().Contain(l => l.Contains("src/Consumer2.tsx"));
        stub.Requests.Single().Should().Contain("file=src%2Fpages%2FWorkspacePage.tsx").And.Contain("direction=in");

        var second = RunHook(stub.Url, session, "src/pages/WorkspacePage.tsx");
        second.Stdout.Should().BeEmpty("повторная правка того же файла в сессии — молчание");
        stub.Requests.Should().HaveCount(1, "один запрос на файл");

        // другой файл той же сессии — снова спрашивает
        Context(RunHook(stub.Url, session, "src/other.ts"));
    }

    [Fact]
    public void Файл50ПлюсЗависимых_ОбрезаетсяДо20СтрокСЕщёN()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(60, total: 75, tests: 3), 0));

        var lines = Context(RunHook(stub.Url, NewSession(), "src/lib/api.ts"));

        lines.Should().HaveCount(20);
        lines[^1].Should().Be("ещё 57", "показано 18 из 75, остаток — в «ещё N»");
        lines.Count(l => l.Contains("[тест]")).Should().Be(3, "покрывающие тесты идут первыми, не теряются при обрезке");
    }

    [Fact]
    public void Таймаут3Секунды_ХукМолчитИНеТормозитХод()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(3), 7000));

        var run = RunHook(stub.Url, NewSession(), "src/slow.ts");

        run.Stdout.Should().BeEmpty("ответ дольше 3 с — молчание");
        run.Exit.Should().Be(0);
        run.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5.5));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public void ОшибкаСервера_ХукМолчитСКодом0(int status)
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (status, "{\"message\":\"x\"}", 0));

        var run = RunHook(stub.Url, NewSession(), "src/a.ts");

        run.Stdout.Should().BeEmpty();
        run.Exit.Should().Be(0);
    }

    [Fact]
    public void НетПеременныхИлиСервераНедоступен_ХукМолчит()
    {
        if (OperatingSystem.IsWindows()) return;
        RunHook(null, NewSession(), "src/a.ts").Stdout.Should().BeEmpty("нет CCS_HOOK_API");
        RunHook("http://127.0.0.1:9", NewSession(), "src/a.ts", token: null).Stdout.Should().BeEmpty("нет токена");
        var refused = RunHook("http://127.0.0.1:9", NewSession(), "src/a.ts");
        refused.Stdout.Should().BeEmpty("соединение отклонено");
        refused.Exit.Should().Be(0);
    }

    [Fact]
    public void ГрафWorktreeСтроится_ОтвечаетОсновнымДеревомСПометкой()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(path => path.Contains("rootPath=")
            ? (404, "{\"building\":true}", 0)
            : (200, Neighbors(2), 0));

        var lines = Context(RunHook(stub.Url, NewSession(), "src/a.ts"));

        lines[0].Should().Contain("ещё строится");
        lines.Should().Contain(l => l.Contains("src/Consumer1.tsx"));
        lines.Length.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void НеудачныйПервыйЗапрос_НеСъедаетФайл()
    {
        if (OperatingSystem.IsWindows()) return;
        var status = 500;
        using var stub = new Stub(_ => (status, status == 200 ? Neighbors(2) : "{\"message\":\"x\"}", 0));
        var session = NewSession();

        RunHook(stub.Url, session, "src/a.ts").Stdout.Should().BeEmpty("сервер отказал — молчание");

        // пауза после сбоя действует 30 с; сбрасываем её, не дожидаясь, — важно только то, что файл не помечен
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(ScriptPath())!, "hook-state"), "*.down"))
            File.Delete(f);
        status = 200;
        Context(RunHook(stub.Url, session, "src/a.ts")).Should().Contain(l => l.Contains("Consumer1"),
            "первый запрос не состоялся — файл не помечен, повторная правка получает контекст");
    }

    [Fact]
    public void ПослеСбоя_ХукМолчитБезОжиданияПотолка()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(2), 7000));
        var session = NewSession();

        RunHook(stub.Url, session, "src/a.ts").Stdout.Should().BeEmpty("таймаут");
        var second = RunHook(stub.Url, session, "src/b.ts");

        second.Stdout.Should().BeEmpty();
        second.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5), "пауза после сбоя — без ожидания 3 с на каждую правку");
        stub.Requests.Should().HaveCount(1, "второй запрос не уходил");
    }

    [Fact]
    public void СостояниеХука_ВКаталоге0700ФайлыСПрефиксомПроекта()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(1), 0));
        var session = NewSession();

        RunHook(stub.Url, session, "src/a.ts");

        var dir = Path.Combine(Path.GetDirectoryName(ScriptPath())!, "hook-state");
        File.GetUnixFileMode(dir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Directory.EnumerateFiles(dir).Single(f => f.EndsWith("_" + session));
        File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Path.GetFileName(file).Should().NotBe(session, "имя несёт префикс проекта");
    }

    [Fact]
    public void ФайлВнеГрафа_НеПомечается_ПослеДостройкиГрафаПравкаПолучаетПотребителей()
    {
        if (OperatingSystem.IsWindows()) return;
        var body = JsonSerializer.Serialize(new { node = new { }, neighbors = Array.Empty<object>(), total = 0 });
        using var stub = new Stub(_ => (200, body, 0));
        var session = NewSession();

        RunHook(stub.Url, session, "src/a.ts").Stdout.Should().BeEmpty("файл вне графа — контекста нет");

        body = Neighbors(2);
        Context(RunHook(stub.Url, session, "src/a.ts")).Should().Contain(l => l.Contains("Consumer1"),
            "контекст не выдавался — файл не помечен, граф достроился, повторная правка его получает");
        stub.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void ДваПроектаСОдинымSessionId_ДваРазныхФайлаСостояния()
    {
        if (OperatingSystem.IsWindows()) return;
        using var stub = new Stub(_ => (200, Neighbors(1), 0));
        var session = NewSession();

        RunHook(stub.Url, session, "src/a.ts", project: "proj-a-" + Guid.NewGuid().ToString("N"));
        RunHook(stub.Url, session, "src/a.ts", project: "proj-b-" + Guid.NewGuid().ToString("N"));

        var dir = Path.Combine(Path.GetDirectoryName(ScriptPath())!, "hook-state");
        Directory.EnumerateFiles(dir, "*_" + session).Should().HaveCount(2,
            "одинаковый session_id у разных проектов не должен делить файл состояния");
        stub.Requests.Should().HaveCount(2, "второй проект не считает файл уже виденным");
    }
}
