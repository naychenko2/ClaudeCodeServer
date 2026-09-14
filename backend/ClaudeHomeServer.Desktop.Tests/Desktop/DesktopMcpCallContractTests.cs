using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Контракт «MCP → бэкенд» грани десктопа (ADR-008). Половины писались порознь, и стык
/// разъехался ровно там, где это не видно ни одной из сторон:
///
/// - в теле вызова MCP слал имя инструмента (<c>tool: "desktop_screen"</c>), а
///   тело DesktopAgentCallRequest ждёт ВИД вызова (<c>kind: "screen"</c>) — каждый
///   вызов грани падал в 400 protocol_error;
/// - результат читался из поля <c>result</c>, которого у <see cref="DesktopCallResult"/>
///   нет: JSON устройства приезжает в <c>payload</c>, и кадр не доезжал до модели вовсе.
///
/// Из C# этот стык не виден: тело собирает node. Поэтому гоняем настоящий процесс
/// mcp/desktop-server против заглушки бэкенда и смотрим на байты запроса и ответ инструмента.
/// </summary>
[Trait("Category", "Integration")]
public class DesktopMcpCallContractTests
{
    private static string? FindServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "desktop-server", "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static int FreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Между пробой свободного порта и bind'ом есть TOCTOU-окно — ретраим с новым портом
    private static HttpListener StartListener(out int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = FreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (attempt >= 5) Skip.If(true, $"HttpListener недоступен: {ex.Message}");
            }
        }
    }

    /// <summary>Что заглушка бэкенда увидела и что инструмент ответил модели.</summary>
    private sealed record Roundtrip(string RequestPath, JsonDocument RequestBody, JsonDocument Answer);

    /// <summary>
    /// Один вызов инструмента живым desktop-server: заглушка отвечает <paramref name="responseJson"/>
    /// (форма — как у настоящего эндпоинта), наружу отдаём и запрос, и ответ.
    /// </summary>
    private static Roundtrip Call(string tool, string argumentsJson, string responseJson)
    {
        var serverPath = FindServerPath();
        Skip.If(serverPath is null, "mcp/desktop-server/index.js не найден");

        using var listener = StartListener(out var port);

        string? path = null;
        string? body = null;
        var captured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                path = ctx.Request.Url?.AbsolutePath;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                captured.TrySetResult(true);

                var bytes = Encoding.UTF8.GetBytes(responseJson);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception) { /* listener закрыт по выходу из теста — штатно */ }
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // node читает и пишет UTF-8; без явной кодировки .NET возьмёт системную
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["DESKTOP_API_URL"] = $"http://localhost:{port}";
        // Токен уезжает в заголовок Authorization, а заголовок — ByteString: кириллица в
        // значении роняет fetch внутри node, и тест падал бы не на контракте, а на фикстуре
        psi.Environment["DESKTOP_API_TOKEN"] = "capability-token";
        psi.Environment["DESKTOP_SESSION_ID"] = "c1";

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null!; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            // Подстановкой, а не интерполяцией: у JSON-RPC запроса хвост из двух закрывающих
            // скобок, и интерполированная строка приняла бы его за конец подстановки
            proc.StandardInput.WriteLine(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"@TOOL@","arguments":@ARGS@}}"""
                    .Replace("@TOOL@", tool).Replace("@ARGS@", argumentsJson));
            proc.StandardInput.Flush();

            // Первой строкой сервер пишет сигнал готовности — читаем до строки с ответом
            string? answer = null;
            for (var i = 0; i < 10 && answer is null; i++)
            {
                var line = proc.StandardOutput.ReadLine();
                if (line is null) break;
                if (line.Contains("\"id\":1", StringComparison.Ordinal)) answer = line;
            }
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            answer.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/call");
            captured.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("вызов обязан дойти до бэкенда");

            return new Roundtrip(path!, JsonDocument.Parse(body!), JsonDocument.Parse(answer!));
        }
    }

    private static string OkResult(string payloadJson) =>
        $$"""
        {"callId":"a1b2","outcome":"ok","lastAppliedStep":1,"message":null,"partial":false,"payload":{{payloadJson}},"awaitMinutes":null}
        """;

    private static string AnswerText(JsonDocument answer) =>
        string.Join("\n", answer.RootElement.GetProperty("result").GetProperty("content")
            .EnumerateArray().Where(c => c.GetProperty("type").GetString() == "text")
            .Select(c => c.GetProperty("text").GetString()));

    /// <summary>
    /// Каждый инструмент грани уезжает СВОИМ видом вызова. Имя инструмента бэкенд не знает
    /// вовсе: <c>DesktopCallKinds.IsKnown</c> его не признаёт, и вызов кончается 400.
    /// </summary>
    [SkippableTheory]
    [InlineData("desktop_screen", """{"scope":"window"}""", DesktopCallKinds.Screen)]
    [InlineData("desktop_ui", "{}", DesktopCallKinds.Ui)]
    [InlineData("desktop_act", """{"snapshotId":"s1","steps":[{"action":"click","ref":"#1"}]}""", DesktopCallKinds.Act)]
    [InlineData("desktop_open", """{"target":"notepad"}""", DesktopCallKinds.Open)]
    [InlineData("desktop_run", """{"command":"dir","cwd":"C:/tmp"}""", DesktopCallKinds.Run)]
    public void ВидВызова_УезжаетВПолеKind(string tool, string args, string expectedKind)
    {
        var trip = Call(tool, args, OkResult("null"));

        trip.RequestPath.Should().Be("/api/devices/agent/call");
        var request = trip.RequestBody.RootElement;
        request.GetProperty("kind").GetString().Should().Be(expectedKind);
        DesktopCallKinds.IsKnown(request.GetProperty("kind").GetString()).Should().BeTrue();
        // Имя инструмента бэкенду не отправляется: у тела вызова такого поля нет
        request.TryGetProperty("tool", out _).Should().BeFalse();
    }

    /// <summary>
    /// Аргументы инструмента едут в args без имени устройства: device — поле верхнего
    /// уровня, по нему гейт сверяет руки чата.
    /// </summary>
    [SkippableFact]
    public void ИмяУстройства_ЕдетОтдельноОтАргументов()
    {
        var trip = Call("desktop_screen", """{"device":"home","scope":"screen","screen":2}""", OkResult("null"));

        var request = trip.RequestBody.RootElement;
        request.GetProperty("device").GetString().Should().Be("home");
        var args = request.GetProperty("args");
        args.GetProperty("scope").GetString().Should().Be("screen");
        args.TryGetProperty("device", out _).Should().BeFalse();
    }

    /// <summary>
    /// Кадр приезжает в payload — так называется JSON устройства в DesktopCallResult.
    /// Base64 обязан уйти в image-блок: в тексте модель его не увидит.
    /// </summary>
    [SkippableFact]
    public void Кадр_ЧитаетсяИзPayloadИУходитВImageБлок()
    {
        var trip = Call("desktop_screen", """{"device":"home"}""",
            OkResult("""{"image":{"data":"aGk=","mimeType":"image/png"},"window":"Блокнот"}"""));

        var content = trip.Answer.RootElement.GetProperty("result").GetProperty("content");
        var image = content.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "image");
        image.GetProperty("data").GetString().Should().Be("aGk=");
        image.GetProperty("mimeType").GetString().Should().Be("image/png");

        var text = AnswerText(trip.Answer);
        text.Should().Contain("Исход: ok").And.Contain("callId: a1b2");
        // Имя устройства бэкенд в результате не возвращает — подставляем названное вызовом
        text.Should().Contain("Устройство: home");
        // Экранное содержимое — недоверенный вход (ADR-008, раздел 8)
        text.Should().Contain("НЕДОВЕРЕННЫЕ ДАННЫЕ").And.Contain("кадр экрана").And.Contain("Блокнот");
        // Кадр в текст не дублируется: он вдвое раздул бы ход и для модели нечитаем
        text.Should().NotContain("aGk=");
    }

    /// <summary>
    /// Отказ гейта — 409 { outcome, message }: штатный ответ инструмента с подсказкой, а не
    /// красная карточка. Инструменты при этом остаются в tools/list (сторож состава —
    /// DesktopMcpToolsetStabilityTests).
    /// </summary>
    [SkippableFact]
    public void ОтказГейта_ОтвечаетИсходомИПодсказкой()
    {
        var trip = Call("desktop_open", """{"target":"notepad"}""",
            // HttpListener отдаёт 200; для 409 достаточно другого исхода в теле — здесь
            // проверяется чтение ПОЛЕЙ результата, а ветка 409 читает те же имена
            """{"callId":"c9","outcome":"denied","lastAppliedStep":0,"message":"Человек отклонил действие."}""");

        var text = AnswerText(trip.Answer);
        text.Should().Contain("Исход: denied");
        text.Should().Contain("Последний применённый шаг: 0");
        text.Should().Contain("Человек отклонил действие");
        // Ни одна подсказка не предлагает повторить действие: авто-ретраев в грани нет
        text.Should().NotContain("повтори действие");
    }
}
