using System.Text;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.WebDav;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-6b-3 (разрез 4bc3d04f §1 путь 19): WebDAV на чужом приватном вложении
/// (.cc-attachments/private/{author}/…, S2d-6b-1): GET/HEAD/PROPFIND по пути чужого private — 404
/// (как несуществующий файл, существования не раскрывает); в листинге папок чужое private не
/// показывается; запись PUT/DELETE/MKCOL/COPY/MOVE в чужой private-путь или с назначением в нём —
/// 403 (чужой путь не создаётся и не перезаписывается). Актор — сам аутентифицированный
/// пользователь Basic-аутентификации. Свои private, общие вложения и прочие пути — без изменений.
///
/// QA-W1 (дефект 6690314e): цель MOVE/COPY — контейнер private/ (сам .cc-attachments/private,
/// .cc-attachments, корень проекта) — тот же 403, что источник-контейнер, до любого следа на
/// диске: каталог private/ не сносится и не сливается переносом/копированием. Каталоги авторов
/// контейнеры не затрагивает: перенос/копия в свой private разрешены, как прежде.
///
/// Проверяется без хоста по образцу WebDavPutTests: HandleAsync с настоящим UserStore/ProjectManager/
/// FileService на временном дереве (Basic-заголовок, путь запроса — как их приносит клиент).
/// </summary>
public class WebDavPrivateAttachmentTests : IDisposable
{
    private const string ProjectName = "davproj";
    private const string Username = "dav-owner";
    private const string Password = "pwd";

    private readonly string _tempDir;
    private readonly string _projectRoot;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly FileService _files = new();
    private readonly string _davId;   // владелец проекта, он же WebDAV-пользователь
    private readonly string _otherId; // автор чужого приватного вложения

    private readonly string _foreignPath; // чужое private
    private readonly string _ownPath;     // своё private (автор — _davId)
    private readonly string _commonPath;  // общее вложение прежней раскладки
    private const string PlainPath = "docs/plain.txt";

    public WebDavPrivateAttachmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "webdav_priv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _davId = _users.Add(Username, Password, "user").Id;
        _otherId = _users.Add("other-author", Password, "user").Id;

        _projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _projects.Create(ProjectName, _projectRoot, _davId, Username);

        _foreignPath = PrivateAttachments.Build(_otherId, Guid.NewGuid(), "foreign.txt")!;
        _ownPath = PrivateAttachments.Build(_davId, Guid.NewGuid(), "own.txt")!;
        _commonPath = AttachmentsGitExclude.AttachmentPath("common.txt")!;

        Write(_foreignPath, "чужое");
        Write(_ownPath, "своё");
        Write(_commonPath, "общее");
        Write(PlainPath, "обычное");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
    }

    private void Write(string relPath, string content)
    {
        var abs = Path.Combine(_projectRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private string AbsOf(string relPath) => Path.Combine(_projectRoot, relPath);

    // Запрос как его приносит клиент: путь /projects/{name}/{rel}, Basic-авторизация владельца
    private DefaultHttpContext Req(string method, string relPath, string? destination = null,
        string? depth = null, string? body = null, string? overwrite = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_config);
        services.AddSingleton(_users);
        services.AddSingleton(_projects);
        services.AddSingleton(_files);

        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Method = method;
        ctx.Request.Path = "/projects/" + ProjectName + (relPath.Length > 0 ? "/" + relPath : "");
        ctx.Request.Headers["Authorization"] =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        if (destination is not null) ctx.Request.Headers["Destination"] = destination;
        if (depth is not null) ctx.Request.Headers["Depth"] = depth;
        if (overwrite is not null) ctx.Request.Headers["Overwrite"] = overwrite;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<DefaultHttpContext> RunAsync(DefaultHttpContext ctx)
    {
        await WebDavHandler.HandleAsync(ctx);
        return ctx;
    }

    private static string BodyOf(DefaultHttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

    private static string Dest(string relPath) =>
        $"http://localhost/projects/{ProjectName}/{relPath}";

    // ── чтение ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ЧтениеЧужогоPrivate_404_СвоёОбщееИФайлыПроектаКакПрежде()
    {
        var r = await RunAsync(Req("GET", _foreignPath));
        r.Response.StatusCode.Should().Be(404, "чужое private — как несуществующий файл: существования не раскрывает");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое", "файл на диске не тронут");

        r = await RunAsync(Req("GET", _ownPath));
        r.Response.StatusCode.Should().Be(200);
        BodyOf(r).Should().Be("своё");

        r = await RunAsync(Req("GET", _commonPath));
        r.Response.StatusCode.Should().Be(200);
        BodyOf(r).Should().Be("общее");

        r = await RunAsync(Req("GET", PlainPath));
        r.Response.StatusCode.Should().Be(200);
        BodyOf(r).Should().Be("обычное");
    }

    [Fact]
    public async Task HeadIProfindЧужогоPrivate_404()
    {
        (await RunAsync(Req("HEAD", _foreignPath))).Response.StatusCode.Should().Be(404);

        var r = await RunAsync(Req("PROPFIND", _foreignPath, depth: "1"));
        r.Response.StatusCode.Should().Be(404);
        BodyOf(r).Should().NotContain("foreign.txt");
    }

    [Fact]
    public async Task ЛистингПапок_ЧужоеPrivateНеПоказывается_СвоёИОбщееВидно()
    {
        // PROPFIND по .cc-attachments/private: каталог чужого автора скрыт, свой и общий guid — видны
        var r = await RunAsync(Req("PROPFIND", ".cc-attachments/private", depth: "1"));
        r.Response.StatusCode.Should().Be(207);
        var xml = BodyOf(r);
        xml.Should().Contain(_davId, "свой private-каталог автора — в листинге как раньше");
        xml.Should().NotContain(_otherId, "каталог чужого автора — не показывается");

        // HTML-листинг GET — тот же фильтр
        r = await RunAsync(Req("GET", ".cc-attachments/private"));
        r.Response.StatusCode.Should().Be(200);
        var html = BodyOf(r);
        html.Should().Contain(_davId);
        html.Should().NotContain(_otherId);

        // сам .cc-attachments и его непосредственные дети — не «чужое private», остаются
        r = await RunAsync(Req("PROPFIND", ".cc-attachments", depth: "1"));
        r.Response.StatusCode.Should().Be(207);
        BodyOf(r).Should().Contain("private").And.Contain(_commonPath.Split('/')[1]);
    }

    // ── запись ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutIDeleteВЧужомPrivate_403_ЧужойПутьНеСоздаётсяИНеТронут()
    {
        var r = await RunAsync(Req("PUT", _foreignPath, body: "поверх чужого"));
        r.Response.StatusCode.Should().Be(403, "чужой путь не перезаписывается");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        var newInForeign = _foreignPath.Replace("foreign.txt", "new.txt");
        r = await RunAsync(Req("PUT", newInForeign, body: "новое в чужом"));
        r.Response.StatusCode.Should().Be(403, "чужой путь не создаётся");
        File.Exists(AbsOf(newInForeign)).Should().BeFalse();

        r = await RunAsync(Req("DELETE", _foreignPath));
        r.Response.StatusCode.Should().Be(403);
        File.Exists(AbsOf(_foreignPath)).Should().BeTrue();

        r = await RunAsync(Req("MKCOL", $".cc-attachments/private/{_otherId}/newdir"));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf($".cc-attachments/private/{_otherId}/newdir")).Should().BeFalse();
    }

    [Fact]
    public async Task MoveICopy_ЧужойPrivate_403_ИсточникиИНазначенияНеТронуты()
    {
        // MOVE с источником в чужом private — 403, источник не сдвинут
        var r = await RunAsync(Req("MOVE", _foreignPath, destination: Dest("moved.txt")));
        r.Response.StatusCode.Should().Be(403);
        File.Exists(AbsOf(_foreignPath)).Should().BeTrue();
        File.Exists(AbsOf("moved.txt")).Should().BeFalse();

        // MOVE с назначением внутри чужого private — 403, чужой путь не создан, свой источник на месте
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(_foreignPath)));
        r.Response.StatusCode.Should().Be(403);
        File.Exists(AbsOf(PlainPath)).Should().BeTrue();
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // COPY в чужой private — 403, копия не создана
        r = await RunAsync(Req("COPY", PlainPath, destination: Dest(_foreignPath)));
        r.Response.StatusCode.Should().Be(403);
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // COPY с источником из чужого private — 403, даже в свой адресат
        r = await RunAsync(Req("COPY", _foreignPath, destination: Dest("copy.txt")));
        r.Response.StatusCode.Should().Be(403);
        File.Exists(AbsOf("copy.txt")).Should().BeFalse();
    }

    // ── доработка 1 по вердикту ревью 426b21ad: источник-контейнер под private/ ──

    [Fact]
    public async Task CopyIMove_КонтейнераПодPrivate_403_PrivateОстаётсяПодСвоимКорнем()
    {
        // COPY .cc-attachments → 403: CopyDirectory (:831) копирует поддерево без исключений,
        // и чужое private появилось бы копией вне .cc-attachments/private/{id}
        var r = await RunAsync(Req("COPY", ".cc-attachments", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403, "источник — контейнер, под которым лежит closed-поддерево private/");
        Directory.Exists(AbsOf("stolen")).Should().BeFalse("stolen/private/… не появилось на диске");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое", "чужой файл остался под своим корнем");

        // MOVE .cc-attachments → 403: перенос каталога унёс бы чужое private из-под путевого правила
        r = await RunAsync(Req("MOVE", ".cc-attachments", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf(".cc-attachments")).Should().BeTrue();
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // .cc-attachments/private — тоже контейнер (под ним все авторы)
        r = await RunAsync(Req("COPY", ".cc-attachments/private", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();
        r = await RunAsync(Req("MOVE", ".cc-attachments/private", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();

        // корень проекта — предок, под которым лежит private/
        r = await RunAsync(Req("COPY", "", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();

        // нормализация до решения: «..» в тот же контейнер — тот же 403
        r = await RunAsync(Req("COPY", "docs/../.cc-attachments", destination: Dest("stolen")));
        r.Response.StatusCode.Should().Be(403);
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();

        // границы правила: каталог автора — НЕ контейнер. Свой private-каталог автор переносит,
        // как раньше; общее вложение (прежняя раскладка {guid}/) — тоже
        r = await RunAsync(Req("COPY", $".cc-attachments/private/{_davId}", destination: Dest("mycopy")));
        r.Response.StatusCode.Should().BeOneOf(200, 201, 204);
        File.ReadAllText(AbsOf($"mycopy/{_ownPath.Split('/')[3]}/own.txt")).Should().Be("своё");

        r = await RunAsync(Req("COPY", _commonPath.Split('/')[0] + "/" + _commonPath.Split('/')[1],
            destination: Dest("commoncopy")));
        r.Response.StatusCode.Should().BeOneOf(200, 201, 204);
        File.ReadAllText(AbsOf($"commoncopy/{_commonPath.Split('/')[2]}")).Should().Be("общее");

        // чужой private после отказанных операций — цел и на своём месте
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");
    }

    // ── доработка 2 по дельта-ревью 24c92aef: экранированный Destination ─────────

    [Fact]
    public async Task MoveICopy_ЭкранированныйDestinationВЧужомPrivate_403_НазначениеНеСоздано()
    {
        var foreignId = _foreignPath.Split('/')[2];
        var guid = _foreignPath.Split('/')[3];
        // %2F — тот же путь, записанный экранированно: ParseDestination снимает URI-кодирование
        // до решения (Uri.AbsolutePath → UnescapeDataString), и гейт видит канонический путь:
        // отказ тот же, что у некодированного Destination
        var encoded = Dest($".cc-attachments%2Fprivate%2F{foreignId}%2F{guid}%2Fforeign.txt");

        var r = await RunAsync(Req("MOVE", PlainPath, destination: encoded));
        r.Response.StatusCode.Should().Be(403, "назначение после декодирования — чужое private");
        File.Exists(AbsOf(PlainPath)).Should().BeTrue("источник не сдвинут");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое", "чужой путь не создан поверх своего");

        r = await RunAsync(Req("COPY", PlainPath, destination: encoded));
        r.Response.StatusCode.Should().Be(403);
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");
    }

    // ── QA-W1 (дефект 6690314e): цель-контейнер private для MOVE/COPY ──────────

    [Fact]
    public async Task Move_ЦельКонтейнерPrivate_403_ЧужиеВложенияЦелыПобайтно()
    {
        // В прежнем коде IsPrivateContainerPath проверялся только для источника: цель
        // .cc-attachments/private проходила IsForeignPrivatePath (у контейнера нет сегмента-
        // автора), затем MOVE сносил её рекурсивно (Directory.Delete(dstAbs, true)) со всеми
        // авторами, и только потом падал или «проходил» сам rename. Теперь — 403 до следа на диске.
        var snapshot = SnapshotPrivate();

        var r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(".cc-attachments/private")));
        r.Response.StatusCode.Should().Be(403, "цель — контейнер, под которым лежит closed-поддерево private/");
        AssertPrivateUnchanged(snapshot, "отказ до любого сноса: private/ цел побайтно");
        File.ReadAllText(AbsOf(PlainPath)).Should().Be("обычное", "источник не сдвинут");

        // вторая та же операция: две подряд операции в контейнер не оставляют частично
        // снесённого каталога — отказ стоит до Directory.Delete
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(".cc-attachments/private")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot, "после второй отказанной операции private/ цел побайтно");

        // Overwrite:F — тот же 403: изоляция контейнера приоритетнее 412 (как чужой private-путь)
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(".cc-attachments/private"), overwrite: "F"));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        // .cc-attachments — тоже контейнер (под ним private/)
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(".cc-attachments")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        // нормализация и экранирование Destination — тот же путь, тот же 403 (по образцу доработки 2)
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest("docs/../.cc-attachments/private")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest(".cc-attachments%2Fprivate")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        // граница правила: каталог автора — НЕ контейнер: перенос в свой private разрешён,
        // как прежде; чужой автор при этом не тронут
        r = await RunAsync(Req("MOVE", PlainPath, destination: Dest($".cc-attachments/private/{_davId}/moved")));
        r.Response.StatusCode.Should().BeOneOf(201, 204);
        File.ReadAllText(AbsOf($".cc-attachments/private/{_davId}/moved")).Should().Be("обычное");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое", "чужой автор не тронут разрешённой операцией");
    }

    [Fact]
    public async Task Copy_ЦельКонтейнерPrivate_403_СлиянияНет()
    {
        // В прежнем коде цель COPY не проверялась вовсе: CopyDirectory сливала поддерево
        // источника в private/ (или File.Copy падал на каталоге-цели). Теперь — 403 до записи.
        Write("docs/sub/nested.txt", "вложенное");
        var snapshot = SnapshotPrivate();

        var r = await RunAsync(Req("COPY", "docs", destination: Dest(".cc-attachments/private")));
        r.Response.StatusCode.Should().Be(403, "цель — контейнер private/");
        AssertPrivateUnchanged(snapshot, "отказ до CopyDirectory: слияния нет");
        File.Exists(AbsOf(".cc-attachments/private/plain.txt")).Should().BeFalse();
        File.Exists(AbsOf(".cc-attachments/private/sub")).Should().BeFalse();

        // копия файла в контейнер — тот же отказ, копия не создана
        r = await RunAsync(Req("COPY", PlainPath, destination: Dest(".cc-attachments/private")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        // .cc-attachments и корень проекта — тоже контейнеры
        r = await RunAsync(Req("COPY", PlainPath, destination: Dest(".cc-attachments")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        r = await RunAsync(Req("COPY", PlainPath, destination: Dest("")));
        r.Response.StatusCode.Should().Be(403, "корень — предок, под которым лежит весь private/");
        AssertPrivateUnchanged(snapshot);
        File.ReadAllText(AbsOf(PlainPath)).Should().Be("обычное", "источник не скопирован в корень");

        // экранированный Destination — тот же путь, тот же 403
        r = await RunAsync(Req("COPY", PlainPath, destination: Dest(".cc-attachments%2Fprivate")));
        r.Response.StatusCode.Should().Be(403);
        AssertPrivateUnchanged(snapshot);

        // граница: каталог автора — не контейнер, копия в свой private разрешена; чужой не тронут
        r = await RunAsync(Req("COPY", PlainPath, destination: Dest($".cc-attachments/private/{_davId}/copied.txt")));
        r.Response.StatusCode.Should().BeOneOf(201, 204);
        File.ReadAllText(AbsOf($".cc-attachments/private/{_davId}/copied.txt")).Should().Be("обычное");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");
    }

    [Fact]
    public async Task СвойPrivateИОбщиеФайлы_ПишутсяКакПрежде()
    {
        var r = await RunAsync(Req("PUT", _ownPath, body: "правка своего"));
        r.Response.StatusCode.Should().BeOneOf(200, 201, 204);
        File.ReadAllText(AbsOf(_ownPath)).Should().Be("правка своего");

        r = await RunAsync(Req("PUT", "docs/new.txt", body: "новый"));
        r.Response.StatusCode.Should().BeOneOf(200, 201, 204);
        File.ReadAllText(AbsOf("docs/new.txt")).Should().Be("новый");
    }

    // ── обходы пути: нормализация до решения, чужое не работает ────────────────

    [Fact]
    public async Task ОбходыПути_ЧужоеPrivateНеОтдаётся_СвоёНеЗакрыт()
    {
        var foreignId = _foreignPath.Split('/')[2];
        var ownId = _ownPath.Split('/')[2];
        var guid = _foreignPath.Split('/')[3];
        var ownGuid = _ownPath.Split('/')[3];

        // «..» из своей папки в чужую: без нормализации путь выглядел бы своим — 404 обязателен
        await AssertForeign404($"docs/../.cc-attachments/private/{foreignId}/{guid}/foreign.txt");
        await AssertForeign404($".cc-attachments/private/{ownId}/../{foreignId}/{guid}/foreign.txt");
        // %2E%2E как «..» (URL-экранирование снимается на входе, решение — после)
        await AssertForeign404($".cc-attachments/private/{ownId}/%2E%2E/{foreignId}/{guid}/foreign.txt");
        // %2F — путь, записанный экранированно целиком
        await AssertForeign404($".cc-attachments%2Fprivate%2F{foreignId}%2F{guid}%2Fforeign.txt");
        // обратные слеши
        await AssertForeign404($".cc-attachments\\private\\{foreignId}\\{guid}\\foreign.txt");
        // регистр сегментов
        await AssertForeign404($".CC-Attachments/PRIVATE/{foreignId.ToUpperInvariant()}/{guid}/foreign.txt");
        // двойные разделители
        await AssertForeign404($".cc-attachments//private//{foreignId}//{guid}//foreign.txt");

        // обход не закрывает своё: «..» в своё private — читается
        var r = await RunAsync(Req("GET", $"docs/../.cc-attachments/private/{ownId}/{ownGuid}/own.txt"));
        r.Response.StatusCode.Should().Be(200);
        BodyOf(r).Should().Be("своё");
    }

    private async Task AssertForeign404(string relPath)
    {
        var r = await RunAsync(Req("GET", relPath));
        r.Response.StatusCode.Should().Be(404, $"путь {relPath} после нормализации — чужое private");
    }

    // Снимок всего поддерева .cc-attachments/private: относительный путь → байты содержимого.
    private Dictionary<string, byte[]> SnapshotPrivate()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var privateRoot = AbsOf(".cc-attachments/private");
        foreach (var f in Directory.GetFiles(privateRoot, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(_projectRoot, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        return map;
    }

    private void AssertPrivateUnchanged(Dictionary<string, byte[]> snapshot, string because = "содержимое private/ цел побайтно")
    {
        var now = SnapshotPrivate();
        now.Keys.Should().BeEquivalentTo(snapshot.Keys, $"{because}: набор файлов не изменился");
        foreach (var (rel, bytes) in snapshot)
            now[rel].Should().Equal(bytes, $"{because}: {rel}");
    }
}
