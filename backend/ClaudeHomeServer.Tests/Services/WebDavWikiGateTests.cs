using System.Text;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Гейт «в вики пишет только Хранитель» на PUT и COPY (ревью S1b, MAJOR 1): оба пишут на диск
// мимо FileService (FileStream + File.Move / File.Copy) и раньше дотянулись до data/wiki/
// напрямую. data-корень — тот же резолв от DataPath, что у FileService; корень проекта здесь
// НАД data-корнем, чтобы относительный путь имел форму боя «data/wiki/…» («проект на весь
// домашний каталог»). Отказ — 403 и пустой диск: ни файла, ни каталога, ни временного файла.
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
}
