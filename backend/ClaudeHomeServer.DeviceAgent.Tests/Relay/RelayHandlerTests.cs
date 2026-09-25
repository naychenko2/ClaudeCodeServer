using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.DeviceAgent.Tests.Composition;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.DeviceAgent.Tests.Relay;

/// <summary>
/// Ретранслятор на агенте (ADR-016 §5, задача 5.1): агент исполняет чтение со своими
/// проверками и серверу не доверяет (G7) — корень проекта из запроса, «..», абсолютный путь,
/// ссылка наружу и потолки размера решает политика агента. Записи в таблице операций нет (G6).
/// </summary>
public sealed class RelayHandlerTests : IDisposable
{
    private readonly AgentSandbox _box = new();
    private readonly RelayHandler _relay;

    public RelayHandlerTests()
    {
        File.WriteAllText(Path.Combine(_box.Project, "big.bin"), new string('x', 4096));
        Directory.CreateDirectory(Path.Combine(_box.Project, "sub"));
        File.WriteAllText(Path.Combine(_box.Project, "sub", "b.txt"), "b\n");
        var git = new GitService(AgentLauncherFactory.Instance);
        var files = new AgentProjectFiles(new FileService(git),
            _box.Policy(new AgentLimits { MaxReadBytes = 1024, MaxStreamBytes = 2048 }));
        _relay = new RelayHandler(files, git);
    }

    public void Dispose() => _box.Dispose();

    private RelayRequest Req(string op, string? path = null, string? variant = null, string? root = null) =>
        new(op, "p1", root ?? _box.Project, variant, path);

    private async Task<(int Status, JsonElement? Json, byte[] Bytes)> RunAsync(RelayRequest request)
    {
        await using var reply = await _relay.ExecuteAsync(request, CancellationToken.None);
        var bytes = reply.Body.ToArray();
        if (reply.Content is { } content)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        JsonElement? json = reply.Head.ContentType?.Contains("json") == true ? JsonDocument.Parse(bytes).RootElement.Clone() : null;
        return (reply.Head.Status, json, bytes);
    }

    // ---------- G6: только чтение ----------

    [Fact]
    public void ТаблицаОпераций_РовноПротокол()
    {
        _relay.Operations.Should().BeEquivalentTo(RelayOperations.All);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("git-commit")]
    [InlineData("")]
    public async Task НезнакомаяОперация_Отказ400(string op)
    {
        File.WriteAllText(Path.Combine(_box.Project, "a.txt"), "было");

        (await RunAsync(Req(op, "a.txt"))).Status.Should().Be(400);

        File.ReadAllText(Path.Combine(_box.Project, "a.txt")).Should().Be("было");
    }

    // ---------- G7: агент не выходит за корни ----------

    [Theory]
    [InlineData(RelayOperations.Read, null)]
    [InlineData(RelayOperations.Read, RelayVariants.Stream)]
    [InlineData(RelayOperations.Stat, null)]
    [InlineData(RelayOperations.List, null)]
    public async Task ТочкиТочки_Отказ403(string op, string? variant)
    {
        (await RunAsync(Req(op, "../../outside/secret.txt", variant))).Status.Should().Be(403);
    }

    [Theory]
    [InlineData(RelayOperations.Read, null)]
    [InlineData(RelayOperations.Read, RelayVariants.Stream)]
    [InlineData(RelayOperations.Stat, null)]
    public async Task АбсолютныйПуть_Отказ403(string op, string? variant)
    {
        var (status, _, bytes) = await RunAsync(Req(op, Path.Combine(_box.Outside, "secret.txt"), variant));

        status.Should().Be(403);
        Encoding.UTF8.GetString(bytes).Should().NotContain("секрет");
    }

    [SkippableTheory]
    [InlineData(RelayOperations.Read, null)]
    [InlineData(RelayOperations.Read, RelayVariants.Stream)]
    [InlineData(RelayOperations.Stat, null)]
    public async Task СсылкаНаружу_Отказ403(string op, string? variant)
    {
        _box.Link(Path.Combine(_box.Project, "leak.txt"), Path.Combine(_box.Outside, "secret.txt"), directory: false);

        var (status, _, bytes) = await RunAsync(Req(op, "leak.txt", variant));

        status.Should().Be(403);
        Encoding.UTF8.GetString(bytes).Should().NotContain("секрет");
    }

    [SkippableFact]
    public async Task Листинг_И_Поиск_НеПоказываютСодержимоеЗаСсылкойНаружу()
    {
        _box.Link(Path.Combine(_box.Project, "ext"), Path.Combine(_box.Outside, "dir"), directory: true);

        var list = await RunAsync(Req(RelayOperations.List, "", RelayVariants.Tree));
        var search = await RunAsync(new RelayRequest(RelayOperations.Search, "p1", _box.Project, Query: "inner"));

        list.Status.Should().Be(200);
        list.Json!.Value.EnumerateArray().Select(e => e.GetProperty("path").GetString()).Should().NotContain(p => p!.Contains("inner"));
        search.Json!.Value.EnumerateArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData(RelayOperations.List)]
    [InlineData(RelayOperations.Read)]
    [InlineData(RelayOperations.Search)]
    [InlineData(RelayOperations.GitStatus)]
    [InlineData(RelayOperations.GitLog)]
    public async Task КореньОтСервера_ВнеРазрешённых_Отказ403(string op)
    {
        // Скомпрометированный сервер присылает корнем чужой каталог — агент ему не верит
        var (status, _, bytes) = await RunAsync(Req(op, op == RelayOperations.Read ? "secret.txt" : null, root: _box.Outside));

        status.Should().Be(403);
        Encoding.UTF8.GetString(bytes).Should().NotContain("секрет");
    }

    [Fact]
    public async Task GitДифф_ПутьНаружу_Отказ400()
    {
        (await RunAsync(Req(RelayOperations.GitDiff, "../../outside/secret.txt"))).Status.Should().Be(400);
    }

    [Fact]
    public async Task ФайлБольшеПотолка_Чтение413_ПотокТоже413()
    {
        (await RunAsync(Req(RelayOperations.Read, "big.bin"))).Status.Should().Be(413);
        (await RunAsync(Req(RelayOperations.Read, "big.bin", RelayVariants.Stream))).Status.Should().Be(413);
    }

    // ---------- чтение ----------

    [Fact]
    public async Task Поток_ОтдаётБайтыИДлину()
    {
        File.WriteAllText(Path.Combine(_box.Project, "clip.mp4"), new string('v', 1500));

        await using var reply = await _relay.ExecuteAsync(Req(RelayOperations.Read, "clip.mp4", RelayVariants.Stream), CancellationToken.None);

        reply.Head.Status.Should().Be(200);
        reply.Head.ContentType.Should().Be("video/mp4");
        reply.Head.Length.Should().Be(1500);
        reply.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task Stat_ФайлПапкаКорень_ИНетТакого()
    {
        var file = await RunAsync(Req(RelayOperations.Stat, "sub/b.txt"));
        var dir = await RunAsync(Req(RelayOperations.Stat, "sub"));
        var root = await RunAsync(Req(RelayOperations.Stat, ""));
        var missing = await RunAsync(Req(RelayOperations.Stat, "sub/nope.txt"));

        file.Status.Should().Be(200);
        file.Json!.Value.GetProperty("path").GetString().Should().Be("sub/b.txt");
        file.Json.Value.GetProperty("size").GetInt64().Should().Be(2);
        dir.Json!.Value.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
        root.Json!.Value.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
        missing.Status.Should().Be(404);
    }

    [Fact]
    public async Task СодержимоеПапки_404()
    {
        (await RunAsync(Req(RelayOperations.Read, "sub"))).Status.Should().Be(404);
    }

    [Fact]
    public async Task GitShow_ФайлВКоммите_ИДифф()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Тест");
        Git("config", "user.email", "t@t");
        Git("add", "sub/b.txt");
        Git("commit", "-qm", "первый");
        var sha = Git("rev-parse", "HEAD").Trim();

        var file = await RunAsync(new RelayRequest(RelayOperations.GitShow, "p1", _box.Project, RelayVariants.CommitFile, "sub/b.txt", Sha: sha));
        var detail = await RunAsync(new RelayRequest(RelayOperations.GitShow, "p1", _box.Project, Sha: sha));
        var badSha = await RunAsync(new RelayRequest(RelayOperations.GitShow, "p1", _box.Project, Sha: "--output=/tmp/x"));

        file.Json!.Value.GetProperty("content").GetString().Should().Be("b\n");
        detail.Json!.Value.GetProperty("subject").GetString().Should().Be("первый");
        badSha.Status.Should().Be(404);
    }

    // ---------- по настоящей связи: кадры запроса и ответа ----------

    [Fact]
    public async Task ПоСвязи_ЗаголовокТелоКонец_БольшойФайлКадрами()
    {
        var files = new AgentProjectFiles(new FileService(new GitService(AgentLauncherFactory.Instance)), _box.Policy());
        var relay = new RelayHandler(files, new GitService(AgentLauncherFactory.Instance));
        var payload = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(Path.Combine(_box.Project, "large.bin"), payload);

        await using var server = new ExecTestServer();
        var link = new ExecLink("relay-1", server, TimeSpan.FromSeconds(10));
        await link.StartAsync(CancellationToken.None);
        var run = relay.RunAsync(link, CancellationToken.None);
        await server.SendAsync(DeviceExecFrameChannel.Control, JsonSerializer.SerializeToUtf8Bytes(
            Req(RelayOperations.Read, "large.bin", RelayVariants.Stream), RelayProtocol.Json));

        RelayResponseHead? head = null;
        using var body = new MemoryStream();
        var chunks = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var frame in server.Received.ReadAllAsync(timeout.Token))
        {
            if (frame.Channel == DeviceExecFrameChannel.Info)
                head = JsonSerializer.Deserialize<RelayResponseHead>(frame.Payload.Span, RelayProtocol.Json);
            else if (frame.Channel == DeviceExecFrameChannel.Stdout) { body.Write(frame.Payload.Span); chunks++; }
            else if (frame.Channel == DeviceExecFrameChannel.Exit) break;
        }

        head!.Status.Should().Be(200);
        head.Length.Should().Be(payload.Length);
        body.ToArray().Should().Equal(payload);
        chunks.Should().BeGreaterThan(1, "большой файл идёт кадрами, а не одним куском в памяти");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _box.Project, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "арранж: git " + string.Join(' ', args));
        return output;
    }
}
