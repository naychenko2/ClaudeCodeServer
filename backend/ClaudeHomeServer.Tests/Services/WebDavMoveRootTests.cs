using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Дефект e69f9de1 (ревью W3-0): MOVE с Destination = URI корня проекта со завершающим слэшем
// резолвил цель в корень, снос существующей цели сносил весь проект, а files.Rename падал
// FileNotFoundException → 404. Вердикт dd6ec8ba (ревью W3-0b): в корень резолвится не только
// пустой путь — "docs%2F.." после unescape даёт "docs/..", и SafeJoin даёт тот же корень.
// Отказ стоит на разрешённом пути (JoinBelowRoot отвергает корень), а не на пустоте строки,
// и стоит до любого следа на диске, по образцу DELETE/LOCK: 403, проект цел.
//
// Раскладка здесь — боевая: корень проекта ОТДЕЛЁН от data-корня (соседний каталог), поэтому
// гейт вики о корне проекта молчит (он предок вики только в раскладке «проект = домашний
// каталог с data/», как в WebDavWikiGateTests). Единственный стопор — гейт корня в HandleMove:
// в той, вики-гейтовой раскладке новый случай закрылся бы старым гейтом и мутация его не
// покраснила бы.
public class WebDavMoveRootTests : IDisposable
{
    private const string Project = "p1";
    // Пользователь WebDAV (S2d-6b-3): COPY/MOVE сверяют пути с его приватными вложениями
    private const string DavUser = "u1";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "webdav-move-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;    // корень проекта — не содержит data/
    private readonly string _dataDir; // data-корень — соседний, тот же резолв от DataPath, что у FileService

    public WebDavMoveRootTests()
    {
        _root = Path.Combine(_home, "project");
        _dataDir = Path.Combine(_home, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_dataDir, "wiki")); // вики реальна и отделена: гейт жив, но о корне молчит
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* временная папка */ }
    }

    private DefaultHttpContext Ctx()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dataDir, "projects.json") })
            .Build();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider(),
        };
        ctx.Request.Method = "MOVE";
        return ctx;
    }

    // Destination — абсолютный URI с /projects/{name}/ (ParseDestination), как шлёт клиент
    private static void SetMoveDestination(DefaultHttpContext ctx, string relPath)
    {
        ctx.Request.Method = "MOVE";
        ctx.Request.Headers["Destination"] = $"http://localhost/projects/{Project}/{relPath}";
    }

    [Fact]
    public void Move_ЦельКореньПроекта_Overwrite_403_ПроектЦел_ИсточникНаМесте()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var keep = Path.Combine(_root, "keep.txt"); // другой файл проекта: снос был рекурсивным
        File.WriteAllText(keep, "файл проекта");

        var ctx = Ctx();
        SetMoveDestination(ctx, ""); // URI корня со завершающим слэшем → ParseDestination отдаёт ""
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_root).Should().BeTrue();  // проект цел: отказ до любого следа на диске
        File.Exists(keep).Should().BeTrue();
        File.Exists(src).Should().BeTrue();         // источник не тронут
        File.ReadAllText(src).Should().Be("обычный файл");
        Directory.Exists(_dataDir).Should().BeTrue();
    }

    // Та же граница с другой стороны: источник — корень проекта (MOVE /projects/{name}/
    // куда-то). Корень не двигается: files.Rename с пустым relPath не зовётся.
    [Fact]
    public void Move_ИсточникКореньПроекта_403_КореньНеДвигается_ЦельНаМесте()
    {
        var dst = Path.Combine(_root, "docs", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, "обычный файл");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/b.txt");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_root).Should().BeTrue();
        File.Exists(dst).Should().BeTrue(); // цель не снесена
        File.ReadAllText(dst).Should().Be("обычный файл");
    }

    // Вердикт dd6ec8ba (ревью W3-0b): в корень резолвится не только пустой путь.
    // "docs%2F..": %2F для Uri — не разделитель, точечная нормализация его не трогает,
    // после UnescapeDataString остаётся "docs/.." — непустая строка, которая SafeJoin
    // резолвит в корень. В старом коде (проверка пустоты строки) этот вариант — красный:
    // 404 и снесённый проект. Перебор записей — по образцу пробы Глеба: всё, что
    // резолвится в корень, даёт 403, каталог проекта и все файлы целы, источник на месте.
    // "%2E%2E" — не корень: Uri нормализует %2E%2E до ".." и снимает сегмент проекта,
    // Destination оказывается за пределами проекта → 400 от ParseDestination (прежнее
    // поведение чужих Destination, к гейту корня отношения не имеет).
    [Theory]
    [InlineData("", 403)]
    [InlineData(".", 403)]
    [InlineData("%2E", 403)]
    [InlineData("%2E%2E", 400)] // после нормализации URI — чужая Destination, не корень
    [InlineData("docs/..", 403)]
    [InlineData("./", 403)]
    [InlineData("%2F", 403)]
    [InlineData("docs%2F..", 403)] // красный в старом коде: 404, проект снесён
    public void Move_ЦельРезолвитсяВКорень_ЛюбаяЗаписьDestination_403_ПроектЦел_ИсточникНаМесте(string destTail, int expected)
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var keep = Path.Combine(_root, "keep.txt"); // другой файл проекта: снос был рекурсивным
        File.WriteAllText(keep, "файл проекта");

        var ctx = Ctx();
        SetMoveDestination(ctx, destTail);
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(expected);
        Directory.Exists(_root).Should().BeTrue();  // проект цел: отказ до любого следа на диске
        File.Exists(keep).Should().BeTrue();
        File.Exists(src).Should().BeTrue();         // источник не тронут
        File.ReadAllText(src).Should().Be("обычный файл");
        Directory.Exists(_dataDir).Should().BeTrue();
    }

    // Та же граница с другой стороны, не пустая строка: relPath = "docs/.." — это тоже
    // корень. Источник-корень не двигается, цель не снесена.
    [Fact]
    public void Move_ИсточникРезолвитсяВКорень_docsДвеТочки_403_ЦельНаМесте()
    {
        var dst = Path.Combine(_root, "docs", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst, "обычный файл");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/b.txt");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/..", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(_root).Should().BeTrue();
        File.Exists(dst).Should().BeTrue(); // цель не снесена
        File.ReadAllText(dst).Should().Be("обычный файл");
    }

    // Дефект 0e766a8a (ревью W3-0b): Destination, резолвящийся в сам источник. URI-
    // нормализация («docs/../docs/a.txt» → «docs/a.txt») делает цель тем же путём, что
    // источник: existed=true, Overwrite:T сносил цель, то есть сам источник, files.Rename
    // следом падал FileNotFoundException → 404, файл потерян. Теперь — 403: RFC 4918 §9.9.4
    // рекомендует этот статус для «source and destination resources are the same», файл цел:
    // проверка стоит по разрешённым путям, до любого сноса. Без правки этот тест красный
    // (404 и удалённый файл) — он же мутация c3.
    [Theory]
    [InlineData("docs/a.txt")]          // буквальный самоперенос
    [InlineData("docs/../docs/a.txt")]  // через точечные сегменты: Uri нормализует в источник
    [InlineData("docs/sub/../a.txt")]   // то же через несуществующий сегмент
    public void Move_ЦельРезолвитсяВТотЖеФайл_Overwrite_403_ФайлНаМесте(string destTail)
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var keep = Path.Combine(_root, "keep.txt");
        File.WriteAllText(keep, "файл проекта");

        var ctx = Ctx();
        SetMoveDestination(ctx, destTail);
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(src).Should().BeTrue();  // источник не тронут: отказ до любого следа на диске
        File.ReadAllText(src).Should().Be("обычный файл");
        File.Exists(keep).Should().BeTrue();
    }

    // Та же граница для каталога: перенос каталога в себя и внутрь самого себя — явного
    // запрета в RFC 4918 нет, но операция невозможна на уровне ФС. Без правки снос
    // существующей цели сносил поддерево источника («docs/inner» терялся с файлом), а
    // Directory.Move всё равно падал (rename каталога в его собственный потомок).
    [Theory]
    [InlineData("docs")]         // каталог в себя
    [InlineData("docs/inner")]   // в существующий потомок
    [InlineData("docs/new")]     // в несуществующий потомок — тот же отказ, каталог цел
    public void Move_КаталогВнутрьСамогоСебя_403_КаталогЦел(string destTail)
    {
        var srcDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(srcDir);
        var file = Path.Combine(srcDir, "a.txt");
        File.WriteAllText(file, "обычный файл");
        var inner = Path.Combine(srcDir, "inner");
        Directory.CreateDirectory(inner);
        var innerFile = Path.Combine(inner, "b.txt");
        File.WriteAllText(innerFile, "файл внутри");

        var ctx = Ctx();
        SetMoveDestination(ctx, destTail);
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(srcDir).Should().BeTrue(); // каталог цел: отказ до любого следа на диске
        File.Exists(file).Should().BeTrue();
        Directory.Exists(inner).Should().BeTrue();
        File.Exists(innerFile).Should().BeTrue();
    }

    // Сравнение «сам в себя» — по правилам регистра ФС хоста, не по строгости строк: тот же
    // ComparisonFor, что в SafePath (SafePath.cs:54). На Windows ФС регистронезависима:
    // «docs/A.TXT» — тот же файл, самоперенос → 403. На Linux это разные файлы: обычный
    // перенос → 201. Тест утверждает поведение каждой ОС — замена компаратора на фиксированный
    // красит его на другой платформе (CI — Linux, разработка — Windows).
    [Fact]
    public void Move_РегистрЦели_КакПравилаФСХоста_СамопереносТолькоТамГдеФСНеЧувствительна()
    {
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        var dst = Path.Combine(_root, "docs", "A.TXT");

        var ctx = Ctx();
        SetMoveDestination(ctx, "docs/A.TXT");
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        if ("a.txt".Equals("A.TXT", SafePath.ComparisonFor(OperatingSystem.IsWindows())))
        {
            ctx.Response.StatusCode.Should().Be(403); // Windows: цель — тот же файл, самоперенос
            File.ReadAllText(src).Should().Be("обычный файл");
        }
        else
        {
            ctx.Response.StatusCode.Should().Be(201); // Linux: разные файлы — обычный перенос
            File.ReadAllText(dst).Should().Be("обычный файл");
            File.Exists(src).Should().BeFalse();
        }
    }

    // Вердикт ac807e61 (доработка W3-0c), MAJOR: цель названа через симлинк-каталог на
    // источник — «link» → «docs», «link/a.txt» и «docs/a.txt» — ОДИН файл на диске, но
    // текстово разные пути. Без разыменования проверки молчат: existed=true, Overwrite:T
    // сносит реальный источник (File.Delete по пути через ссылку сносит цель ссылки),
    // files.Rename падает FileNotFoundException → 404, файл потерян (проба Глеба:
    // srcAlive=False). Теперь — 403 по разыменованному пути, как текстовое самопереносное
    // сравнение выше: файл цел, следа на диске нет. На текстовом сравнении этот тест
    // красный — он же мутация c3.
    [Fact]
    public void Move_ЦельЧерезСимлинКаталогНаИсточник_403_ФайлНаМесте()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий; CI и дев-машины — Linux
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), "docs"); // относительная цель — от каталога ссылки

        var ctx = Ctx();
        SetMoveDestination(ctx, "link/a.txt");
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(src).Should().BeTrue(); // источник цел: отказ до любого следа на диске
        File.ReadAllText(src).Should().Be("обычный файл");
        Directory.Exists(Path.Combine(_root, "link")).Should().BeTrue(); // и сама ссылка цела
    }

    // Та же граница для каталога через ссылку: «other» → симлинк на «docs». MOVE docs →
    // other/x — перенос каталога в самого себя через ссылку: текстово «other/x» не внутри
    // «docs», проверка молчала; на ФС это rename в собственный потомок (EINVAL), а снос
    // существующей цели снёс бы поддерево источника. Отказ 403 по разыменованному пути,
    // каталог цел.
    [Fact]
    public void Move_КаталогВнутрьСебяЧерезСимлин_403_КаталогЦел()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        var srcDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(srcDir);
        var file = Path.Combine(srcDir, "a.txt");
        File.WriteAllText(file, "обычный файл");
        Directory.CreateSymbolicLink(Path.Combine(_root, "other"), "docs");

        var ctx = Ctx();
        SetMoveDestination(ctx, "other/x");
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        Directory.Exists(srcDir).Should().BeTrue(); // каталог цел: отказ до любого следа на диске
        File.Exists(file).Should().BeTrue();
    }

    // Цикл симлинков в цели («a» → «b», «b» → «a»): путь неразрешим (бюджет разыменования).
    // Fail-closed 403 — не 500 и не 404 с снесённым источником. Первая линия обороны здесь
    // гейт вики (IsWikiPath fail-closed на неразрешимом пути), каноническая проверка —
    // вторая, она работает и когда гейт молчит (нет конфигурации). Тест утверждает
    // видимый контракт, а не конкретную строку.
    [Fact]
    public void Move_ЦиклСимлинковВЦели_403_ИсточникНаМесте()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        var src = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "обычный файл");
        Directory.CreateSymbolicLink(Path.Combine(_root, "a"), "b");
        Directory.CreateSymbolicLink(Path.Combine(_root, "b"), "a");

        var ctx = Ctx();
        SetMoveDestination(ctx, "a/x");
        ctx.Request.Headers["Overwrite"] = "T";
        WebDavHandler.HandleMove(ctx, new FileService(), _root, "docs/a.txt", Project, DavUser);

        ctx.Response.StatusCode.Should().Be(403);
        File.Exists(src).Should().BeTrue();
        File.ReadAllText(src).Should().Be("обычный файл");
    }
}
