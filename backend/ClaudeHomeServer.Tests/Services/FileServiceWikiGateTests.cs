using System.Reflection;
using ClaudeHomeServer.Services.Files;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Гейт «в вики пишет только Хранитель» на путях FileService (ADR-035, решение 2).
//
// Корень тестового «проекта» здесь — сам data-корень (DataPath = {dir}/projects.json):
// тот самый патологический случай, ради которого стоит этот гейт — проект на весь домашний
// каталог, тогда относительный путь «wiki/{owner}/…» лежит под data/wiki/ и файловый API
// (files_*, WebDAV, Docs) дотянулся бы до поддерева владельца. В бою data/ вне корней
// проектов — проверка остаётся дешёвой страховкой, а не основным гейтом.
// Таблица методов — default-deny, образец — NotesWikiGateTests и SubsystemBoundaryTests.
public class FileServiceWikiGateTests : IDisposable
{
    private readonly string _dir;        // он же dataDir, он же корень проекта
    private readonly string _wiki;       // {dir}/wiki
    private readonly string _wikiPages;  // {dir}/wiki/u1/pages
    private readonly FileService _sut;

    public FileServiceWikiGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fs_wiki_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wiki = Path.Combine(_dir, "wiki");
        _wikiPages = Path.Combine(_wiki, "u1", "pages");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _sut = new FileService(config: config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── Таблица методов (default-deny) ──────────────────────────────────────

    // Охраняемые — мутирующие точки FileService по ADR-035, решение 2. Rename проверяет
    // обе стороны (как Move в NotesService): вынести из вики нельзя так же, как внести.
    private static readonly string[] GuardedMethods =
    [
        "WriteFile", "WriteFileBytes", "CreateFile", "CreateDirectory", "Delete", "Rename", "RevertFile",
    ];

    // Всё остальное публичное — чтение, предикат или не способно записать под data/wiki/:
    // каждая строка с причиной. Новый публичный метод без строки красит тест-перебор.
    private static readonly Dictionary<string, string> ExcludedMethods = new()
    {
        ["SafeJoinPublic"] = "валидатор пути, ничего не пишет",
        ["WithOpenedPathGuard"] = "копия сервиса с другим сверщиком дескрипторов, ничего не пишет",
        ["NotifyMutated"] = "уведомление подписчиков, ничего не пишет",
        ["List"] = "чтение",
        ["Search"] = "чтение",
        ["Tree"] = "чтение",
        ["ReadFile"] = "чтение",
        ["ReadFileBytes"] = "чтение",
        ["OpenRead"] = "чтение",
        ["IsBinaryFile"] = "предикат по содержимому/расширению",
        ["IsImageFile"] = "предикат по расширению",
        ["IsVideoFile"] = "предикат по расширению (static)",
        ["IsAudioFile"] = "предикат по расширению (static)",
        ["GetDocumentInfo"] = "предикат по расширению",
        ["GetFileSize"] = "чтение метаданных",
        ["GetFileBase64"] = "чтение",
        ["GetDiffAsync"] = "чтение git-диффа",
        ["GetCommitsRaw"] = "чтение git-лога",
    };

    [Fact]
    public void ПубличныеМетодыFileService_ВсеОписаныВТаблице()
    {
        var publicNames = typeof(FileService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            // add_/remove_ события OnMutated и аксессоры свойств — не операции с файлами
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var covered = GuardedMethods.Concat(ExcludedMethods.Keys).Distinct().ToList();
        publicNames.Should().BeEquivalentTo(covered,
            "новый публичный метод FileService обязан быть классифицирован: охраняемый или с причиной (ADR-035, решение 2)");
    }

    // ─── Драйверы: каждый охраняемый метод отказывает свой путь ───────────────

    [Fact]
    public void МутирующиеМетоды_ОтказываютПоПутиПодDataWiki()
    {
        // Подготовка — при перечислении (до вызовов), сами вызовы — Action-и.
        var calls = BuildRefusedCalls().ToList();
        calls.Select(c => c.Name.Split('/')[0]).Distinct()
            .Should().BeEquivalentTo(GuardedMethods, "у каждого охраняемого метода должен быть драйвер");

        foreach (var (name, call) in calls)
        {
            var ex = Record.Exception(call);
            ex.Should().BeOfType<UnauthorizedAccessException>($"{name} обязан отказать путь под data/wiki/").Which
                .Message.Should().Be("В вики пишет только Хранитель");
        }
    }

    private IEnumerable<(string Name, Action Call)> BuildRefusedCalls()
    {
        yield return ("WriteFile", () => _sut.WriteFile(_dir, WikiRel("Write"), "тело"));
        yield return ("WriteFileBytes", () => _sut.WriteFileBytes(_dir, "wiki/u1/media/bytes.bin", new byte[] { 1 }));
        yield return ("CreateFile/без содержимого", () => _sut.CreateFile(_dir, WikiRel("Create")));
        yield return ("CreateFile/с содержимым", () => _sut.CreateFile(_dir, WikiRel("Create-контент"), "шаблон"));
        yield return ("CreateDirectory", () => _sut.CreateDirectory(_dir, "wiki/u1/pages/Папка"));

        // Файл под вики создан мимо сервиса: «унаследованный» — таким он и приходит в бой
        WriteRaw(WikiRel("Удалить"), "# Удалить");
        yield return ("Delete", () => _sut.Delete(_dir, WikiRel("Удалить")));

        // Rename: обе стороны — вынести из вики и внести в неё
        WriteRaw(WikiRel("ИзВики"), "# ИзВики");
        WriteRaw("Наружы.md", "# Наружы");
        yield return ("Rename/из вики", () => _sut.Rename(_dir, WikiRel("ИзВики"), "Наружы-новая.md"));
        yield return ("Rename/в вику", () => _sut.Rename(_dir, "Наружы.md", WikiRel("ВВики")));

        // _dir — не git-репозиторий: отказ стоит до проверки репо и до try, который
        // иначе вернул бы false (отказ вики — не сбой git)
        yield return ("RevertFile", () => _sut.RevertFile(_dir, WikiRel("Revert")));
    }

    // ─── Отказ не оставил следа на диске ─────────────────────────────────────

    [Fact]
    public void Отказ_НеСоздаётНиФайлаНиПапкиПодDataWiki()
    {
        Record.Exception(() => _sut.WriteFile(_dir, WikiRel("Write"), "тело"));
        Record.Exception(() => _sut.WriteFileBytes(_dir, "wiki/u1/media/bytes.bin", new byte[] { 1 }));
        Record.Exception(() => _sut.CreateFile(_dir, WikiRel("Create"), "шаблон"));
        Record.Exception(() => _sut.CreateDirectory(_dir, "wiki/u1/pages"));

        Directory.Exists(_wiki).Should().BeFalse(); // ни одной папки под data/wiki/ не появилось
    }

    [Fact]
    public void Delete_НеСноситФайлПодDataWiki()
    {
        var file = WriteRaw(WikiRel("Удалить"), "# Удалить");

        var ex = Record.Exception(() => _sut.Delete(_dir, WikiRel("Удалить")));

        ex.Should().BeOfType<UnauthorizedAccessException>();
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void Rename_НеДвигаетФайлИзВикиИВВики()
    {
        var inWiki = WriteRaw(WikiRel("ИзВики"), "# ИзВики");
        var outside = WriteRaw("Наружы.md", "# Наружы");

        Record.Exception(() => _sut.Rename(_dir, WikiRel("ИзВики"), "Наружы-новая.md"));
        var ex = Record.Exception(() => _sut.Rename(_dir, "Наружы.md", WikiRel("ВВики")));

        ex.Should().BeOfType<UnauthorizedAccessException>();
        File.Exists(inWiki).Should().BeTrue();                        // из вики не вынесено
        File.Exists(outside).Should().BeTrue();                      // в вику не внесено
        File.Exists(Path.Combine(_wikiPages, "ВВики.md")).Should().BeFalse();
    }

    // ─── Границы самого гейта ────────────────────────────────────────────────

    [Fact]
    public void ОбычныеПутиПроекта_ПишутсяКакПрежде()
    {
        // Тот же сервис с тем же data-корнем: под data/wiki/ отказ, всё остальное — как было
        _sut.WriteFile(_dir, "обычное.md", "тело");
        _sut.WriteFileBytes(_dir, "sub/bytes.bin", new byte[] { 1, 2 });
        _sut.CreateFile(_dir, "созданное.md", "шаблон");
        _sut.CreateDirectory(_dir, "папка/вложенная");
        _sut.Delete(_dir, "созданное.md");
        _sut.Rename(_dir, "обычное.md", "переименованное.md");
        // data/notes/ — не вики: заметки проекта этот гейт не трогает. WriteFile предков не
        // создаёт (его контракт), поэтому папка нужна заранее — как в бою.
        _sut.CreateDirectory(_dir, "notes");
        _sut.WriteFile(_dir, "notes/Заметка.md", "# Заметка");
        // «wikipedia» — не вики (сравнение по сегменту имени, не по подстроке)
        _sut.CreateDirectory(_dir, "wikipedia");
        _sut.WriteFile(_dir, "wikipedia/Статья.md", "# Статья");
        // не git-репо — прежний false, а не отказ
        _sut.RevertFile(_dir, "переименованное.md").Should().BeFalse();

        File.ReadAllText(Path.Combine(_dir, "переименованное.md")).Should().Be("тело");
        File.Exists(Path.Combine(_dir, "sub", "bytes.bin")).Should().BeTrue();
        Directory.Exists(Path.Combine(_dir, "папка", "вложенная")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "созданное.md")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "notes", "Заметка.md")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "wikipedia", "Статья.md")).Should().BeTrue();
    }

    [Fact]
    public void БезРезолваDataКорня_ГейтаНет()
    {
        // DeviceAgent и юнит-конструкции без конфига: data-корня нет и проверить нечем —
        // отказа нет (тот же контракт, что у ClaudeSession). Их ловит не этот гейт, а
        // отсутствие data/ в песочнице (ADR-035, решение 2).
        var svc = new FileService();
        Directory.CreateDirectory(_wikiPages); // WriteFile предков не создаёт — его контракт, не гейт
        Record.Exception(() => svc.WriteFile(_dir, WikiRel("Write"), "тело")).Should().BeNull();
        File.Exists(Path.Combine(_wikiPages, "Write.md")).Should().BeTrue();
    }

    // ─── Помощники ───────────────────────────────────────────────────────────

    private static string WikiRel(string name) => $"wiki/u1/pages/{name}.md";

    // Запись мимо сервиса — «унаследованное» состояние под data/wiki/
    private string WriteRaw(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }
}
