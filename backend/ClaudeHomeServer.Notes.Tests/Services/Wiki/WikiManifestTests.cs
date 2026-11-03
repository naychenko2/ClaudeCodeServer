using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S2b-1 (ADR-035, решение 4): атомарная запись temp+move без мусора и с сохранением цели
// при отказе; манифест раздела keeper/manifest/{section}.json — страница → sha256;
// журнал keeper/log.jsonl — строки дописываются в порядке вызовов, не переписываются.
public class WikiManifestTests : IDisposable
{
    private readonly string _dir;

    public WikiManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_manifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Ожидаемый хэш считается независимо от проверяемого кода: тест через тот же
    // Sha256Hex не отличил бы неверный хэш.
    private static string Sha(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // Ни одного *.tmp-файла во всём дереве пробы. Каталоги с таким именем не считаются:
    // тест сам создаёт один из них (исключение на записи temp).
    private void AssertNoTempFiles() =>
        Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();

    // ─── Атомарная запись ─────────────────────────────────────────────────────

    [Fact]
    public void Write_СоздаётКаталогИФайл_ИНеОставляетTemp()
    {
        var root = Path.Combine(_dir, "w1");
        var rel = "pages/Общее/Заметка.md";

        WikiFileWriter.Write(root, rel, "первое содержимое");

        File.ReadAllText(Path.Combine(root, rel)).Should().Be("первое содержимое");
        AssertNoTempFiles();
    }

    [Fact]
    public void Write_ПерезаписываетСуществующийФайл_БезTemp()
    {
        var root = Path.Combine(_dir, "w2");
        var rel = "keeper/manifest/common.json";
        Directory.CreateDirectory(Path.Combine(root, "keeper", "manifest"));
        File.WriteAllText(Path.Combine(root, rel), "старое");

        WikiFileWriter.Write(root, rel, "новое");

        File.ReadAllText(Path.Combine(root, rel)).Should().Be("новое");
        AssertNoTempFiles();
    }

    [Fact]
    public void Write_ОтказНаЗаписиTemp_ОставляетПрежнееСодержимоеПобайтно()
    {
        var root = Path.Combine(_dir, "w3");
        var rel = "keeper/manifest/common.json";
        var target = Path.Combine(root, rel);
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x00, 0x01, 0xFE }; // BOM и управляющие байты
        File.WriteAllBytes(target, bytes);
        // Имя temp уникально на каждый вызов — загнать его каталогом больше нельзя.
        // Отказ записи temp вызывается причиной, не зависящей от имени: каталог цели
        // read-only — создание в нём нового файла отказано на обеих платформах.
        File.SetAttributes(dir, FileAttributes.ReadOnly);
        try
        {
            Record.Exception(() => WikiFileWriter.Write(root, rel, "новое")).Should().NotBeNull();

            File.ReadAllBytes(target).Should().Equal(bytes); // прежнее содержимое побайтно
            AssertNoTempFiles();
        }
        finally
        {
            // Атрибут возвращается: иначе Dispose не сможет убрать дерево.
            File.SetAttributes(dir, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Write_ИсключениеМеждуЗаписьюTempИMove_ЦельНетронутаTempУбран()
    {
        var root = Path.Combine(_dir, "w4");
        var rel = "keeper/x";
        // Цель — существующий каталог: temp пишется, Move по нему бросает —
        // ровно тот интервал, который проверяет критерий.
        Directory.CreateDirectory(Path.Combine(root, rel));

        Record.Exception(() => WikiFileWriter.Write(root, rel, "новое")).Should().NotBeNull();

        Directory.Exists(Path.Combine(root, rel)).Should().BeTrue(); // цель как была
        AssertNoTempFiles(); // записанный temp уборка сняла
    }

    // ─── Манифест раздела ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("projects/p1", "projects__p1")]
    [InlineData("spheres/s1", "spheres__s1")]
    [InlineData("common", "common")]
    [InlineData("personal", "personal")]
    public void SafeFileName_КлючРазделаСтановитсяИменомФайла(string key, string expected) =>
        WikiManifest.SafeFileName(key).Should().Be(expected);

    [Fact]
    public void Manifest_ДвеСтраницыProjects_РавенХэшамФайлов_CommonНеИзменён()
    {
        var root = Path.Combine(_dir, "w6");
        const string key = "projects/p1";
        var rel1 = "pages/Проекты/p1/Знание.md";
        var rel2 = "pages/Проекты/p1/Другое.md";
        WikiFileWriter.Write(root, rel1, "текст один");
        WikiFileWriter.Write(root, rel2, "текст два");

        WikiManifest.RecordApplied(root, key, new Dictionary<string, string>
        {
            ["Проекты/p1/Знание.md"] = Sha("текст один"),
            ["Проекты/p1/Другое.md"] = Sha("текст два"),
        });

        // Имя файла — безопасная форма ключа раздела.
        var manifestFile = Path.Combine(root, "keeper", "manifest", "projects__p1.json");
        File.Exists(manifestFile).Should().BeTrue();

        var manifest = WikiManifest.TryRead(root, key);
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest.Should().HaveCount(2); // ровно две записи
        manifest.Manifest["Проекты/p1/Знание.md"].Should().Be(Sha(File.ReadAllText(Path.Combine(root, rel1))));
        manifest.Manifest["Проекты/p1/Другое.md"].Should().Be(Sha(File.ReadAllText(Path.Combine(root, rel2))));

        // Другой раздел — другой файл: common не создан и читается «файла нет».
        File.Exists(Path.Combine(root, "keeper", "manifest", "common.json")).Should().BeFalse();
        var absent = WikiManifest.TryRead(root, "common");
        absent.State.Should().Be(WikiManifestState.Missing);
        absent.Manifest.Should().BeEmpty();
        AssertNoTempFiles();
    }

    // ─── Чтение манифеста: три состояния (ревью S2b-1, M1) ────────────────────

    [Fact]
    public void TryRead_ФайлаНет_Missing_БезИсключения()
    {
        var root = Path.Combine(_dir, "w9");

        var result = WikiManifest.TryRead(root, "common");

        result.State.Should().Be(WikiManifestState.Missing); // норма до первого применения
        result.Manifest.Should().BeEmpty();
        AssertNoTempFiles();
    }

    [Theory]
    [InlineData("")] // манифест 0 байт
    [InlineData("   ")] // только пробелы
    [InlineData("{ мусор")] // битый JSON
    [InlineData("[1,2]")] // валидный JSON, не та форма
    [InlineData("null")] // десериализуется в null
    public void TryRead_НевалидныйМанифест_Invalid_БезИсключения_СтраницыНеТронуты(string body)
    {
        var root = Path.Combine(_dir, "w10");
        var pageRel = "pages/Общее/X.md";
        WikiFileWriter.Write(root, pageRel, "ручная правка пользователя");
        var manifestFile = Path.Combine(root, "keeper", "manifest", "common.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFile)!);
        File.WriteAllText(manifestFile, body); // файл «пришёл с диска» испорченным

        var result = WikiManifest.TryRead(root, "common");

        result.State.Should().Be(WikiManifestState.Invalid);
        result.Manifest.Should().BeEmpty();
        // fail-closed: страница, сверяемая с испорченным манифестом, не тронута —
        // «не применять» не должно превратиться в «применить всё заново»
        File.ReadAllText(Path.Combine(root, pageRel)).Should().Be("ручная правка пользователя");
        AssertNoTempFiles();
    }

    [Fact]
    public void RecordApplied_НаНевалидномМанифесте_Отказ_БитыйФайлНеПерезаписан()
    {
        var root = Path.Combine(_dir, "w11");
        var manifestFile = Path.Combine(root, "keeper", "manifest", "common.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFile)!);
        File.WriteAllText(manifestFile, "{ мусор");

        Record.Exception(() => WikiManifest.RecordApplied(root, "common",
            new Dictionary<string, string> { ["Общее/X.md"] = Sha("x") })).Should().NotBeNull();

        // отказ не перезаписал битый файл: его нечитаемые записи не стёрты молча
        File.ReadAllText(manifestFile).Should().Be("{ мусор");
        AssertNoTempFiles();
    }

    // ─── Журнал ───────────────────────────────────────────────────────────────

    [Fact]
    public void Log_ТриЗаписи_ТриСтрокиВалидногоJSONВПорядкеВызовов()
    {
        var root = Path.Combine(_dir, "w8");

        WikiManifest.AppendLog(root, "create", "projects/p1", ["Проекты/p1/Знание.md"], "session-1", "keeper");
        WikiManifest.AppendLog(root, "append", "common", ["Общее/X.md"], null, "owner");
        WikiManifest.AppendLog(root, "link", "personal", [], null, "keeper");

        var lines = File.ReadAllLines(Path.Combine(root, "keeper", "log.jsonl"));
        lines.Should().HaveCount(3); // дописывание: строки не переписаны

        var expected = new[] { ("create", "projects/p1"), ("append", "common"), ("link", "personal") };
        for (var i = 0; i < lines.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]); // валидный JSON
            var e = doc.RootElement;
            e.GetProperty("op").GetString().Should().Be(expected[i].Item1);
            e.GetProperty("section").GetString().Should().Be(expected[i].Item2);
            DateTimeOffset.TryParse(e.GetProperty("ts").GetString(), out _).Should().BeTrue();
        }

        // Поля pages/source — по строкам вызовов.
        var first = JsonDocument.Parse(lines[0]).RootElement;
        first.GetProperty("pages")[0].GetString().Should().Be("Проекты/p1/Знание.md");
        first.GetProperty("source").GetString().Should().Be("session-1");
        first.GetProperty("actor").GetString().Should().Be("keeper");
        var third = JsonDocument.Parse(lines[2]).RootElement;
        third.GetProperty("pages").GetArrayLength().Should().Be(0);
        third.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);

        AssertNoTempFiles();
    }
}
