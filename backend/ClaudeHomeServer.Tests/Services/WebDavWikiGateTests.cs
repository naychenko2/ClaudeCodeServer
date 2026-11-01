using System.Text;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Гейт «в вики пишет только Хранитель» на PUT, COPY и MOVE (ревью S1b, MAJOR 1): все три
// пишут или сносят на диск мимо FileService (FileStream + File.Move / File.Copy; MOVE сносит
// цель до files.Rename) и раньше дотянулись до data/wiki/ напрямую. data-корень — тот же
// резолв от DataPath, что у FileService; корень проекта здесь НАД data-корнем, чтобы
// относительный путь имел форму боя «data/wiki/…» («проект на весь домашний каталог»).
// Отказ — 403 и пустой диск: ни файла, ни каталога, ни временного файла. Для MOVE предикат
// шире, как GuardNotWikiSubtree в FileService: путь под вики и путь-предок вики (data/,
// корень) — рекурсивный снос предка снёс бы вику целиком, обойдя IsWikiPath.
public class WebDavWikiGateTests : IDisposable
{
    private const string Project = "p1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webdav-wiki-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;   // {root}/data — тот же data-корень, что у ResolveDataDir(DataPath)

    public WebDavWikiGateTests()
    {
        Directory.CreateDirectory(_root);
        _dataDir = Path.Combine(_root, "data");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private DefaultHttpContext Ctx(Stream? body = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dataDir, "projects.json") })
            .Build();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider(),
        };
        ctx.Request.Method = "PUT";
        if (body is not null)
        {
            ctx.Request.Body = body;
            ctx.Request.ContentLength = body.Length;
        }
        return ctx;
    }

    // Destination — абсолютный URI с /projects/{name}/ (ParseDestination), как шлёт клиент
    private static void SetDestination(DefaultHttpContext ctx, string relPath)
    {
        ctx.Request.Method = "COPY";
        ctx.Request.Headers["Destination"] = $"http://localhost/projects/{Project}/{relPath}";
    }

    private string[] LeftoverTempFiles() =>
        Directory.GetFiles(_root, "*.davput-*", SearchOption.AllDirectories);

    // Страница вики, записанная мимо WebDAV и FileService: «унаследованное» состояние
    private string WriteWikiPage(string name, string content)
    {
        var full = Path.Combine(_dataDir, "wiki", "u1", "pages", name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    // ─── PUT ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_ПодDataWiki_403_НиФайлаНиПапки()
    {
        var payload = Encoding.UTF8.GetBytes("# Страница");

        var ctx = Ctx(new MemoryStream(payload));
        await WebDavHandler.HandlePutAsync(ctx, _root, "data/wiki/u1/pages/Страница.md");

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(Path.Combine(_dataDir, "wiki", "u1", "pages", "Страница.md")).Should().BeFalse();
        Directory.Exists(Path.Combine(_dataDir, "wiki")).Should().BeFalse(); // отказ стоит до CreateDirectory
        LeftoverTempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Put_ОбычныеПути_РаботаютКакПрежде()
    {
        var payload = Encoding.UTF8.GetBytes("тело");

        var ctx = Ctx(new MemoryStream(payload));
        await WebDavHandler.HandlePutAsync(ctx, _root, "docs/a.txt");
        ctx.Response.StatusCode.Should().Be(201);
        (await File.ReadAllTextAsync(Path.Combine(_root, "docs", "a.txt"))).Should().Be("тело");

        // под data/, но мимо вики: data/notes/ — заметки, не вики (тот же край, что у IsWikiPath)
        var notes = Ctx(new MemoryStream(payload));
        await WebDavHandler.HandlePutAsync(notes, _root, "data/notes/Заметка.md");
        notes.Response.StatusCode.Should().Be(201);
        File.Exists(Path.Combine(_dataDir, "notes", "Заметка.md")).Should().BeTrue();
    }

    // ─── COPY: цель под вики ─────────────────────────────────────────────────

    [Fact]
    public async Task Copy_ЦельПодDataWiki_403_ЦельНеСоздана_ИсточникНеТронут()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        await File.WriteAllTextAsync(src, "обычный файл");

        var ctx = Ctx();
        SetDestination(ctx, "data/wiki/u1/pages/Страница.md");
        await WebDavHandler.HandleCopyAsync(ctx, new FileService(), _root, "docs/a.txt", Project);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(Path.Combine(_dataDir, "wiki", "u1", "pages", "Страница.md")).Should().BeFalse();
        Directory.Exists(Path.Combine(_dataDir, "wiki")).Should().BeFalse();
        File.Exists(src).Should().BeTrue();
    }

    // ─── COPY: источник под вики (вынос так же недопустим, как внос) ─────────

    [Fact]
    public async Task Copy_ИсточникПодDataWiki_403_СтраницаНаМесте()
    {
        var page = WriteWikiPage("Страница.md", "# Страница");

        var ctx = Ctx();
        SetDestination(ctx, "Копия.md");
        await WebDavHandler.HandleCopyAsync(ctx, new FileService(), _root, "data/wiki/u1/pages/Страница.md", Project);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(page).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Копия.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_ОбычныеПути_РаботаютКакПрежде()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        await File.WriteAllTextAsync(src, "обычное");

        var ctx = Ctx();
        SetDestination(ctx, "docs/b.txt");
        await WebDavHandler.HandleCopyAsync(ctx, new FileService(), _root, "docs/a.txt", Project);
        ctx.Response.StatusCode.Should().Be(201);
        (await File.ReadAllTextAsync(Path.Combine(_root, "docs", "b.txt"))).Should().Be("обычное");

        // под data/, но мимо вики — прежний успех
        var notes = Ctx();
        SetDestination(notes, "data/notes/Копия.md");
        await WebDavHandler.HandleCopyAsync(notes, new FileService(), _root, "docs/a.txt", Project);
        notes.Response.StatusCode.Should().Be(201);
        File.Exists(Path.Combine(_dataDir, "notes", "Копия.md")).Should().BeTrue();
    }

    // ─── MOVE ──────────────────────────────────────────────────────────────────

    // Destination для MOVE: тот же абсолютный URI, метод запроса — MOVE (ParseDestination
    // читает только заголовок Destination, метод для него не значим)
    private static void SetMoveDestination(DefaultHttpContext ctx, string relPath)
    {
        ctx.Request.Method = "MOVE";
        ctx.Request.Headers["Destination"] = $"http://localhost/projects/{Project}/{relPath}";
    }

    // Дефект хвоста ревью S1b: HandleMove сносил существующую цель до files.Rename, поэтому
    // отказ по вики приходил уже после потери данных. Теперь гейт стоит до сноса: 403 и пустой
    // диск — ни цель не снесена, ни источник не тронут.

    [Fact]
    public void Move_ЦельПодDataWiki_403_СтраницаИИсточникНаМесте()
    {
        var src = Path.Combine(_root, "notes", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var page = WriteWikiPage("Страница.md", "# Страница");

        var ctx = Ctx();
        SetMoveDestination(ctx, "data/wiki/u1/pages/Страница.md");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "notes/x.md", Project);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(page).Should().BeTrue(); // цель не снесена: отказ до любого следа на диске
        File.Exists(src).Should().BeTrue();  // источник не тронут
    }

    [Fact]
    public void Move_ИзВикиНаСуществующийФайл_403_ОбычныйФайлНаМесте()
    {
        var page = WriteWikiPage("Страница.md", "# Страница");
        var dst = Path.Combine(_root, "docs", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, "обычный файл");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/x.md");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "data/wiki/u1/pages/Страница.md", Project);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(dst).Should().BeTrue();  // обычный файл не снесён
        File.Exists(page).Should().BeTrue(); // страница вики на месте
    }

    // Тот же дефект с предком вики в ЦЕЛИ (хвост ревью S1b, W3-0): до широкого гейта MOVE на
    // data проходил IsUnderDataWiki (он видит только путь под вики), снос цели был
    // рекурсивным — data/wiki сносился целиком, а 403 приходил уже из files.Rename, после
    // потери данных. Теперь 403 и целый диск.
    [Fact]
    public void Move_ЦельПредокВики_Overwrite_403_ИКаталогНаМесте()
    {
        var src = Path.Combine(_root, "notes", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var page = WriteWikiPage("Страница.md", "# Страница");

        var ctx = Ctx();
        SetMoveDestination(ctx, "data"); // Overwrite не шлётся — по умолчанию «да»: снос существующей цели
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "notes/x.md", Project);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_dataDir).Should().BeTrue(); // data не снесён: отказ до любого следа на диске
        Directory.Exists(Path.Combine(_dataDir, "wiki")).Should().BeTrue(); // вики цела
        File.Exists(page).Should().BeTrue();
        File.Exists(src).Should().BeTrue();           // источник не тронут
    }

    // Та же граница с другой стороны: источник — предок вики (data/). Снос существующей цели
    // (обычного файла) случался до отказа files.Rename — файл терялся зря.
    [Fact]
    public void Move_ИсточникПредокВики_403_ЦельНаМесте()
    {
        var dst = Path.Combine(_root, "docs", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, "обычный файл");
        WriteWikiPage("Страница.md", "# Страница");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/x.md");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "data", Project);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(dst).Should().BeTrue();           // цель не снесена
        Directory.Exists(_dataDir).Should().BeTrue(); // data цел
    }

    // под data/, но мимо вики: заметки — не вики и не предок вики (data/notes не предок
    // data/wiki), широкий гейт MOVE их не цепляет — перенос работает как прежде
    [Fact]
    public void Move_ЗаметкиПодData_РаботаютКакПрежде()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "переносимое");
        var dstDir = Path.Combine(_dataDir, "notes");
        Directory.CreateDirectory(dstDir); // папка заметок есть на диске, как после первой заметки

        var ctx = Ctx();
        SetMoveDestination(ctx, "data/notes/Заметка.md");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project);

        ctx.Response.StatusCode.Should().Be(201);
        File.ReadAllText(Path.Combine(dstDir, "Заметка.md")).Should().Be("переносимое");
        File.Exists(src).Should().BeFalse();
    }

    [Fact]
    public void Move_ОбычныйПерезаписывает_КакПрежде()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "новое");
        var dst = Path.Combine(_root, "docs", "b.txt");
        File.WriteAllText(dst, "старое");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/b.txt");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project);

        ctx.Response.StatusCode.Should().Be(204);
        File.ReadAllText(dst).Should().Be("новое");
        File.Exists(src).Should().BeFalse();
    }
}
