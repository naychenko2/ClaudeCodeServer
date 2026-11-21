using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Загрузка ребёнка в папку предмета ручкой `upload` рельсы (Б11b, ADR-041 ред. 6, решения
/// 56/89/90): граница пути — ChildPathPolicy (тот же отказ, что у остальных ручек рельсы),
/// запись — общий писатель ChildTextCopyService: тип по сигнатуре, размер файла, метр папки
/// и текстовая копия по ChildPathPolicy.TextCopyOf.
///
/// Что фиксируют тесты (критерии c1–c3 + правило имён):
/// 1. PDF с текстом → 200 и ровно .ccs-text/лист.pdf.txt; docx → 200 и .ccs-text/x.docx.txt;
///    docm (макрос) → 415; exe → 415; файл больше maxAttachmentBytes → 413; «Материалы/»,
///    «Для репетитора/» и .ccs-text/ ребёнку для записи закрыты → 403 телом child_path;
/// 2. гонка двух загрузок у предела папки: принята ровно одна, вторая — 413 folder_limit,
///    20 повторов. Мутация «снять блок метра» в ChildTextCopyService.SaveAsync (строки
///    var sem = LockFor(root) / await sem.WaitAsync(ct) / sem.Release() в finally) —
///    краснеет: приняты обе;
/// 3. сбой конвертера: файл на диске, .ccs-text/ пуст, 200 с textCopy=false;
/// 4. имя занято каталогом → 409 exists; свой файл перезаписывается; взрослый upload не
///    изменился: ни типа, ни метра, ни копии, тело ответа — прежнее;
/// 5. (Х-W4-2, ревью 49941fdc п.2) перезапись того же файла у потолка → 200 (прежний
///    размер вычитается из метра); дельта сверх потолка → 413, прежний файл не тронут.
///
/// Конвертер — скрипт на весь класс: печатает маркер для любого входа и падает (код 1, пусто)
/// на имени «notext» — тот же приём, что «echo DOCX-MARKER» и «ccs-no-such-converter» в
/// тестах стора материалов: проверяется путь писателя, а не настоящий markitdown.
/// Лимиты класса: файл — 4096 байт (ChildMode:MaxAttachmentBytes), папка — 1000 байт: файлы
/// тестов — сотни байт, гонка берёт метр двумя по 600.
/// </summary>
[Trait("Category", "Host")]
public class ChildFilesUploadTests : IClassFixture<ChildFilesUploadTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            Directory.CreateDirectory(TempDir);
            var script = Path.Combine(TempDir, OperatingSystem.IsWindows() ? "ccs-copy-marker.bat" : "ccs-copy-marker.sh");
            if (OperatingSystem.IsWindows())
            {
                // %P:notext=% — замена подстроки пустой: отличие от %P значит «имя содержит notext».
                File.WriteAllText(script, "@echo off\r\nset P=%1\r\nif not \"%P:notext=%\"==\"%P%\" exit /b 1\r\necho CCS-TEXT-MARKER\r\n");
            }
            else
            {
                File.WriteAllText(script, "#!/bin/sh\ncase \"$1\" in\n  *notext*) exit 1 ;;\nesac\necho CCS-TEXT-MARKER\n");
            }
            ExtraConfig["Markitdown:Command"] = OperatingSystem.IsWindows() ? $"cmd /c {script}" : $"bash {script}";
            ExtraConfig[ChildLimitsCalculator.ConfigMaxAttachmentBytesKey] = "4096";
            ExtraConfig[ChildTextCopyService.ConfigMaxSubjectBytesKey] = "1000";
        }
    }

    private const string ChildPassword = "kid-upload-password";

    private readonly Hosts _host;

    public ChildFilesUploadTests(Hosts host) => _host = host;

    private UserStore Users => _host.Services.GetRequiredService<UserStore>();

    // --- Фикстура ---------------------------------------------------------------

    // Ребёнок с одним предметом и его проектом-предметом — образец NewChild в
    // ChildFilesRailTests. Имя предмета — tag теста: состояние одного теста не переходит
    // в другой на общем хосте класса.
    private (string ChildId, HttpClient Child, Project Project, string Root) NewChild(string tag)
    {
        var username = "kid.upload." + tag;
        var kid = Users.Add(username, ChildPassword, "user");
        var subject = new ChildSubject { Id = tag, Label = "Предмет " + tag, Glyph = "Аа", Color = "info" };
        Users.SetChildProfile(kid.Id, new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
            Spaces = { [ChildSpaces.Lessons] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }], DailyMinutes = 600,
            } },
            Subjects = [subject],
        }, resetRules: false).Should().BeTrue("профиль ребёнка пишется публичным путём стора");

        var provided = _host.Services.GetRequiredService<ChildSubjectProjectProvisioner>()
            .EnsureSubjectProjectAsync(kid.Id, subject).GetAwaiter().GetResult();
        provided.Should().NotBeNull("проект-предмета обеспечивает только его провижер");
        var project = provided!;
        ProjectCapabilities.IsChildSubject(project).Should().BeTrue();
        return (kid.Id, _host.CreateAuthenticatedClient(username, ChildPassword), project, project.RootPath!);
    }

    // --- Фикстуры байтов ------------------------------------------------------

    // PDF-фикстура — валидный UTF-8 с префиксом %PDF-: тип по сигнатуре, а текстовая часть
    // нужна, чтобы «нет текста» было отличимо от «не PDF».
    private static byte[] PdfBytes(string name, int pad = 0) =>
        Encoding.UTF8.GetBytes("%PDF-1.4\n" + name + new string('x', pad) + " текст листа\n");

    // docx — zip с записью word/document.xml; docm — он же плюс word/vbaProject.bin
    // (макросы: не в allow-list загрузки, решение 45).
    private static byte[] OfficeZip(bool macros)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            void Write(string entryName, string content)
            {
                var entry = zip.CreateEntry(entryName);
                using var w = entry.Open();
                w.Write(Encoding.UTF8.GetBytes(content));
            }

            Write("word/document.xml", "<w:document/>");
            if (macros) Write("word/vbaProject.bin", "bin");
        }
        return ms.ToArray();
    }

    // exe — DOS-заголовок с нулевыми байтами: не картинка, не PDF, не UTF-8 без нулей, не zip.
    private static readonly byte[] ExeBytes = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

    // Размер в байтах равен длине строки: фикстуры ASCII, чтобы метр и лимит читались тестом.
    private static byte[] TextBytes(int size) => Encoding.UTF8.GetBytes(new string('x', size));

    // --- Помощники ручки ------------------------------------------------------

    private static async Task<HttpResponseMessage> Upload(HttpClient http, string projectId, string? path,
        string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        var url = $"/api/projects/{projectId}/files/upload" + (path is null ? "" : $"?path={Uri.EscapeDataString(path)}");
        return await http.PostAsync(url, form);
    }

    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    // Файлы папки предмета относительными путями: «что осталось после полного вызова» —
    // пустые папки провижера в этот список не входят.
    private static List<string> FilesUnder(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();

    // --- c1: типы, размеры, граница пути --------------------------------------

    [Fact]
    public async Task PdfСТекстом_200_ИРовноКопияПоTextCopyOf()
    {
        var (_, child, project, root) = NewChild("u1");
        var resp = await Upload(child, project.Id, null, "лист.pdf", PdfBytes("лист"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = Json(await Body(resp));
        body.GetProperty("path").GetString().Should().Be("лист.pdf");
        body.GetProperty("kind").GetString().Should().Be("pdf");
        body.GetProperty("textCopy").GetBoolean().Should().BeTrue();

        // Конечное состояние полного цикла: ровно оригинал и его копия по TextCopyOf
        // («лист.pdf» → «.ccs-text/лист.pdf.txt»); второй формы копии нигде нет.
        File.Exists(Path.Combine(root, "лист.pdf")).Should().BeTrue();
        var copy = Path.Combine(root, ".ccs-text", "лист.pdf.txt");
        File.Exists(copy).Should().BeTrue();
        File.ReadAllText(copy).Should().Contain("CCS-TEXT-MARKER");
        // Пустые папки провижера («Материалы/», «Для репетитора/») — не часть результата:
        // сравниваются файлы папки, их ровно два.
        FilesUnder(root).Should().BeEquivalentTo("лист.pdf", ".ccs-text/лист.pdf.txt");
    }

    [Fact]
    public async Task Docx_200_ИКопияXDocxTxt_РасширениеИИмяНеСрезаны()
    {
        var (_, child, project, root) = NewChild("u2");
        var resp = await Upload(child, project.Id, null, "x.docx", OfficeZip(macros: false));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = Json(await Body(resp));
        body.GetProperty("path").GetString().Should().Be("x.docx", "расширение не расходится с типом, имя остаётся");
        body.GetProperty("kind").GetString().Should().Be("office");
        body.GetProperty("textCopy").GetBoolean().Should().BeTrue();

        File.Exists(Path.Combine(root, "x.docx")).Should().BeTrue();
        File.ReadAllText(Path.Combine(root, ".ccs-text", "x.docx.txt")).Should().Contain("CCS-TEXT-MARKER");
    }

    [Theory]
    [InlineData("docm", "u3a")]
    [InlineData("exe", "u3b")]
    public async Task МакросыИНеизвестныйТип_415_БезФайлаНаДиске(string kind, string tag)
    {
        var (_, child, project, root) = NewChild(tag);
        string name;
        byte[] bytes;
        if (kind == "docm")
        {
            name = "лист.docm";
            bytes = OfficeZip(macros: true);
        }
        else
        {
            name = "prog.exe";
            bytes = ExeBytes;
        }

        var resp = await Upload(child, project.Id, null, name, bytes);
        resp.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await Body(resp)).Should().Contain("unsupported_type");
        File.Exists(Path.Combine(root, name)).Should().BeFalse("отказ — до любого эффекта");
        Directory.Exists(Path.Combine(root, ".ccs-text")).Should().BeFalse("копии отказанного файла нет");
    }

    [Fact]
    public async Task ФайлБольшеMaxAttachmentBytes_413_ИФайлаНаДискеНет()
    {
        var (_, child, project, root) = NewChild("u4");
        // Лимит класса — 4096 байт (ChildMode:MaxAttachmentBytes); фикстура — 5000.
        var resp = await Upload(child, project.Id, null, "большой.txt", TextBytes(5000));

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Json(await Body(resp)).GetProperty("code").GetString().Should().Be(ChildTextCopyService.CodeTooLarge);
        File.Exists(Path.Combine(root, "большой.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Материалы", "u5a")]
    [InlineData("Для репетитора", "u5b")]
    [InlineData(".ccs-text", "u5c")]
    public async Task ЗащищённыеПапки_РебёнкуДляЗаписиЗакрыты_403ТеломРельсы(string folder, string tag)
    {
        var (_, child, project, root) = NewChild(tag);
        Directory.CreateDirectory(Path.Combine(root, folder));

        var resp = await Upload(child, project.Id, folder, "лист.pdf", PdfBytes("лист"));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // То же тело отказа, что у PUT content / create / mkdir той же границы.
        (await Body(resp)).Should().Contain("child_path");
        Directory.GetFiles(Path.Combine(root, folder)).Should().BeEmpty("отказ — до записи");
    }

    // --- c2: гонка двух загрузок у предела папки -------------------------------

    [Fact]
    public async Task ГонкаДвухЗагрузокУПределаПапки_ПринятаРовноОдна_20Повторов()
    {
        for (var iter = 0; iter < 20; iter++)
        {
            var (_, child, project, root) = NewChild($"r{iter}");
            try
            {
                // Метр класса — 1000 байт; PDF 917 и текст 900: каждый свой, вместе — нет.
                // PDF идёт первым и держит метр дольше (под ним конвертер), поэтому текстовый
                // доходит уже до занятого метра. Без блока метра он прочитал бы пустую папку
                // и прошёл бы: мутация «снять блок» красит ровно это.
                var pdf = Upload(child, project.Id, null, "лист.pdf", PdfBytes("лист", 900));
                var txt = Upload(child, project.Id, null, "заметка.txt", TextBytes(900));
                var responses = await Task.WhenAll(pdf, txt);

                var codes = ((int)responses[0].StatusCode, (int)responses[1].StatusCode);
                switch (codes)
                {
                    case (200, 413):
                    case (413, 200):
                        break;
                    default:
                        Assert.Fail($"итерация {iter}: принято {codes} — ровно одна загрузка");
                        break;
                }

                var refused = responses.First(r => r.StatusCode == HttpStatusCode.RequestEntityTooLarge);
                (await Body(refused)).Should().Contain(ChildTextCopyService.CodeFolderLimit, $"итерация {iter}");

                var acceptedPath = Json(await Body(responses.First(r => r.StatusCode == HttpStatusCode.OK)))
                    .GetProperty("path").GetString()!;
                File.Exists(Path.Combine(root, acceptedPath)).Should().BeTrue($"итерация {iter}: принятый файл на диске");
                FilesUnder(root).Where(p => !p.StartsWith(".ccs-text/", StringComparison.Ordinal))
                    .Should().HaveCount(1, $"итерация {iter}: отказанный ничего не оставил");
            }
            finally
            {
                // Уборка — дело теста: следующий повтор начинает с пустой папки.
                await _host.Services.GetRequiredService<ProjectDeletionService>().DeleteAsync(project);
            }
        }
    }

    // --- Х-W4-2: перезапись у потолка — не рост объёма (ревью 49941fdc, п.2) -----

    [Fact]
    public async Task ПерезаписьРовноУПотолка_200_ОбъёмПапкиНеРастёт()
    {
        var (_, child, project, root) = NewChild("u9");

        // Метр класса — 1000 байт: первая запись ровно на границе (равное — пройдено).
        (await Upload(child, project.Id, null, "заметка.txt", TextBytes(1000)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Перезапись того же файла тем же размером: содержимое папки не меняется —
        // размер прежнего файла вычитается из метра до проверки. Без вычитания это
        // было бы 1000+1000 > 1000 → 413 (ложный отказ); мутация «вернуть двойной счёт»
        // (убрать used -= FileSizeOrZero(originalAbs)) краснеет здесь.
        var again = await Upload(child, project.Id, null, "заметка.txt", TextBytes(1000));
        again.StatusCode.Should().Be(HttpStatusCode.OK, "перезапись у потолка — не рост объёма");
        File.ReadAllText(Path.Combine(root, "заметка.txt")).Should().HaveLength(1000, "вернулась вторая запись");
    }

    [Fact]
    public async Task ПерезаписьСДельтойСверхПотолка_413_ПрежнийФайлНеТронут()
    {
        var (_, child, project, root) = NewChild("u10");

        (await Upload(child, project.Id, null, "заметка.txt", TextBytes(500)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Дельта 501 байт при остатке 500: вычитание не отменяет потолок —
        // 1000−500+1001 > 1000 → 413, прежний файл и его размер остаются.
        var resp = await Upload(child, project.Id, null, "заметка.txt", TextBytes(1001));
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Json(await Body(resp)).GetProperty("code").GetString().Should().Be(ChildTextCopyService.CodeFolderLimit);
        File.ReadAllText(Path.Combine(root, "заметка.txt")).Should().HaveLength(500, "отказ — до любого эффекта");
    }

    // --- c3: сбой конвертации --------------------------------------------------

    [Fact]
    public async Task СбойКонвертера_ФайлСохранёнКопииНет_200TextCopyFalse()
    {
        var (_, child, project, root) = NewChild("u6");
        // Фикстура названа «notext»: тестовый конвертер на таком имени падает (код 1, пусто).
        var resp = await Upload(child, project.Id, null, "notext.pdf", PdfBytes("скан"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "файла без текстовой копии это не отменяет: его читает оригинал");
        var body = Json(await Body(resp));
        body.GetProperty("path").GetString().Should().Be("notext.pdf");
        body.GetProperty("textCopy").GetBoolean().Should().BeFalse();

        File.Exists(Path.Combine(root, "notext.pdf")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, ".ccs-text")).Should().BeFalse("полусостояния нет: копии нет вовсе");
    }

    // --- Правило имён и взрослый маршрут ---------------------------------------

    [Fact]
    public async Task КаталогНаИмени_409Exists_СвойФайлПерезаписывается()
    {
        var (_, child, project, root) = NewChild("u7");
        Directory.CreateDirectory(Path.Combine(root, "папка"));

        var conflict = await Upload(child, project.Id, null, "папка", TextBytes(10));
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(conflict)).Should().Contain("exists");
        Directory.GetFiles(Path.Combine(root, "папка")).Should().BeEmpty("отказ — ничего не тронуто");

        // Перезапись своего файла — правило взрослого upload: под замком пути, без 409.
        (await Upload(child, project.Id, null, "заметка.txt", TextBytes(10))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Upload(child, project.Id, null, "заметка.txt", TextBytes(20))).StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(root, "заметка.txt")).Should().HaveLength(20, "вернулась вторая запись");
    }

    [Fact]
    public async Task ВзрослыйUploadСвоегоПроекта_НеИзменился()
    {
        var client = _host.CreateAuthenticatedClient();
        var root = Path.Combine(_host.TempDir, "adult-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Create без CreateDirectory отказывает несуществующую папку — не часть проверки
        var created = await client.PostAsJsonAsync("/api/projects", new { name = "обычный-upload", rootPath = root });
        created.EnsureSuccessStatusCode();
        var projectId = Json(await Body(created)).GetProperty("id").GetString()!;
        try
        {
            var resp = await Upload(client, projectId, null, "лист.pdf", PdfBytes("лист"));
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            // Взрослый путь не тронут: ни гейта типа, ни метра, ни копии — прежний пустой ответ.
            (await Body(resp)).Should().BeEmpty();
            File.Exists(Path.Combine(root, "лист.pdf")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, ".ccs-text")).Should().BeFalse();
        }
        finally
        {
            await client.DeleteAsync($"/api/projects/{projectId}");
        }
    }

    // Метка [ChildAllowed] на ручке: без неё ребёнок упёрся бы в гейт И1 до контроллера
    // (тот же 403 child_mode, что у закрытых ручек в ChildFilesRailTests).
    [Fact]
    public async Task МеткаРучки_РебёнокПроходитГейт_ДоКонтроллера()
    {
        var (_, child, project, _) = NewChild("u8");
        var resp = await Upload(child, project.Id, null, "лист.txt", TextBytes(10));
        (await Body(resp)).Should().NotContain("child_mode");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
