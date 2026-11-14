using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// P1b (разрез 2, §2.2/§2.3, инварианты Г4/Г5): Persona.PromptSections пишется только через
/// PersonaManager.Create/Update и только по каталогу SpecialtyPromptPresets. Неизвестный id и
/// текст сверх SectionTextLimit — ArgumentException (контроллер отдаст 400 в P1d), персона при
/// этом не тронута; дубли id схлопываются (побеждает последняя запись), пустой override и
/// пустой список — «всё как у специальности» (null). Смена специальности сбрасывает слой целиком;
/// та же специальность (и Specialty=None без смены) — не сброс. Слой персоны не пишет в профиль
/// специальности (Г5).
/// </summary>
public class PersonaPromptSectionsWriteTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _tempDir;
    private readonly string _personasPath;
    private readonly string _specialtyPath;
    private readonly PersonaManager _sut;

    public PersonaPromptSectionsWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_promptsec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            ["Ollama:Model"] = "",
        });
        _personasPath = config["PersonasPath"]!;
        // Путь стора — рядом с DataPath (SpecialtySettingsStore.cs:170); Г5 мерит именно его файл
        _specialtyPath = Path.Combine(Path.GetDirectoryName(_personasPath)!, "specialty-settings.json");
        _sut = new PersonaManager(config, NullLogger<PersonaManager>.Instance,
            specialty: TestSpecialtyStore.Create(config));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Запись кэша, а не гидратированная выдача: слой пишется в запись (Г4).</summary>
    private Persona Cache(string id) => _sut.GetRaw(id, Owner)!;

    private static PromptSectionOverride Section(string id, bool? enabled = null, string? text = null) =>
        new(id, enabled, text);

    // Позиционные аргументы как в боевом вызове сервиса (name..memoryEnabled), именованными —
    // только проверяемые поля (образец PersonaInheritsAccessWriteTests)
    private Persona Update(string id, string? name = null, PersonaSpecialty? specialty = null,
        List<PromptSectionOverride>? promptSections = null) =>
        _sut.Update(id, Owner, name, null, null, null, null, null, null, null, null, null, null,
            specialty: specialty, promptSections: promptSections);

    private Persona CreatePersona(PersonaSpecialty specialty = PersonaSpecialty.Reviewer,
        List<PromptSectionOverride>? promptSections = null) =>
        _sut.Create(Owner, "Тест", null, null, null, null, null, PersonaScope.Global, null, null, null, true,
            specialty: specialty, promptSections: promptSections);

    private string HashOfSpecialtyFile() =>
        File.Exists(_specialtyPath)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(_specialtyPath))))
            : "<нет файла>";

    [Fact]
    public void Create_СекцииИзКаталога_TrimИКаноническийId_ПишутсяВЗапись()
    {
        var p = CreatePersona(promptSections:
        [
            Section("HISTORY", true, "  свой текст  "),
            Section("codeGraph", false),
        ]);

        var raw = Cache(p.Id).PromptSections!;
        raw.Should().HaveCount(2, "обе записи задают параметр");
        raw[0].Id.Should().Be("history", "id нормализуется к каноническому из каталога");
        raw[0].Enabled.Should().BeTrue();
        raw[0].Text.Should().Be("свой текст", "Text — трим, как слоты контракта");
        raw[1].Id.Should().Be("codeGraph");
        raw[1].Enabled.Should().BeFalse();
        raw[1].Text.Should().BeNull("Text не задан — текст остаётся за специальностью");
    }

    [Fact]
    public void Update_НеизвестныйId_Отказ_ПерсонаИФайлНеТронуты()
    {
        var p = CreatePersona(promptSections: [Section("history", true, "записанное")]);
        Update(p.Id, name: "Имя");

        Action act = () => Update(p.Id, promptSections: [Section("noSuchSection", true, "текст")]);
        act.Should().Throw<ArgumentException>().WithMessage("*noSuchSection*");

        var raw = Cache(p.Id);
        raw.PromptSections!.Single().Id.Should().Be("history", "отказ не трогает записанное");
        raw.Name.Should().Be("Имя");
        File.ReadAllText(_personasPath).Should().NotContain("noSuchSection", "отказ — файл не переписан");
    }

    [Fact]
    public void ТекстСекции_РовноВЛимитеПроходит_СверхЛимитаОтказ()
    {
        var atLimit = new string('а', SpecialtyPromptPresets.SectionTextLimit);
        var tooLong = new string('а', SpecialtyPromptPresets.SectionTextLimit + 1);
        var p = CreatePersona(promptSections: [Section("history", true, atLimit)]);

        Cache(p.Id).PromptSections![0].Text.Should().HaveLength(SpecialtyPromptPresets.SectionTextLimit,
            "граница лимита включительна: тот же предел, что у валидации стора и UI-счётчика");

        Action act = () => Update(p.Id, promptSections: [Section("history", true, tooLong)]);
        act.Should().Throw<ArgumentException>().WithMessage($"*{SpecialtyPromptPresets.SectionTextLimit}*");

        Cache(p.Id).PromptSections![0].Text.Should().HaveLength(SpecialtyPromptPresets.SectionTextLimit,
            "отказ не трогает записанное");
    }

    [Fact]
    public void ДублиIdСхлопываются_ПобедляетПоследняяЗапись_ПустаяСнимаетЗначение()
    {
        var p = CreatePersona(promptSections:
        [
            Section("history", true, "первый"),
            Section("history", false, "второй"),
            Section("codeGraph", true, "текст"),
            Section("codeGraph"),
        ]);

        var raw = Cache(p.Id).PromptSections!;
        raw.Should().HaveCount(1, "дубль схлопнут, пустой override (ни Enabled, ни Text) выбрасывается");
        raw[0].Id.Should().Be("history");
        raw[0].Enabled.Should().BeFalse("побеждает последняя запись");
        raw[0].Text.Should().Be("второй");
    }

    [Fact]
    public void ПустойСписокИОдинПустойOverride_ВсёКакУСпециальности_Null()
    {
        var withText = CreatePersona(promptSections: [Section("history", true, "текст")]);
        Update(withText.Id, promptSections: []);
        Cache(withText.Id).PromptSections.Should().BeNull("пустой список — явный сброс к «как у специальности»");

        var emptyOnly = CreatePersona(promptSections: [Section("history")]);
        emptyOnly.PromptSections.Should().BeNull("пустой override — ничего не задаёт, записи нет");

        CreatePersona().PromptSections.Should().BeNull("поле не передано: поведение прочих путей не меняется");
    }

    [Fact]
    public void Update_СменаСпециальности_СлойСброшен_ПризнакВРезультате()
    {
        var p = CreatePersona(promptSections: [Section("history", true, "ролевые тексты"), Section("codeGraph", false)]);
        Cache(p.Id).PromptSections.Should().NotBeNull("до смены слой есть — его и проверяем");

        var updated = Update(p.Id, specialty: PersonaSpecialty.Planner);

        updated.PromptSections.Should().BeNull(
            "смена специальности: свои секции — тексты старой роли, они сброшены целиком (§2.3)");
        var raw = Cache(p.Id);
        raw.Specialty.Should().Be(PersonaSpecialty.Planner);
        raw.PromptSections.Should().BeNull("и в записи");
    }

    [Fact]
    public void Update_ТаЖеСпециальностьИлиБезНеё_СлойНеТронут()
    {
        var p = CreatePersona(promptSections: [Section("history", true, "своё")]);

        Update(p.Id, name: "Переименованная");
        Update(p.Id, specialty: PersonaSpecialty.Reviewer); // то же значение — не «смена»

        var raw = Cache(p.Id);
        raw.Name.Should().Be("Переименованная");
        raw.PromptSections!.Single().Id.Should().Be("history",
            "слоя касается только РЕАЛЬНАЯ смена специальности (тот же признак, что у Apply)");
    }

    [Fact]
    public void Update_СменаСпециальностиИСекцииОднимВызовом_СекцииПоверхНовойСпециальности()
    {
        var p = CreatePersona(promptSections: [Section("history", true, "старая роль")]);

        var updated = Update(p.Id, specialty: PersonaSpecialty.Planner,
            promptSections: [Section("processes", true, "новая роль")]);

        var written = updated.PromptSections!.Single();
        written.Id.Should().Be("processes");
        written.Text.Should().Be("новая роль");
        Cache(p.Id).Specialty.Should().Be(PersonaSpecialty.Planner);
    }

    [Fact]
    public void SpecialtyNone_СлойХранится_СбросаНет()
    {
        var p = CreatePersona(specialty: PersonaSpecialty.None, promptSections: [Section("history", true, "своё")]);

        Update(p.Id, name: "Без специальности");
        Update(p.Id, specialty: PersonaSpecialty.None); // то же значение — не смена

        Cache(p.Id).PromptSections!.Single().Id.Should().Be("history",
            "у None слой хранится, но не действует (секций специальности нет) — нормализация та же, сброса нет");
    }

    [Fact]
    public void UpdateСоСменойСпециальности_НеМеняетХешSpecialtySettings()
    {
        var before = HashOfSpecialtyFile();

        var p = CreatePersona(promptSections: [Section("history", true, "своё"), Section("codeGraph", false)]);
        Update(p.Id, promptSections: [Section("processes", true, "ещё")]);
        Update(p.Id, specialty: PersonaSpecialty.Planner); // и сброс слоя — тоже

        HashOfSpecialtyFile().Should().Be(before,
            "Г5: слой персоны живёт в personas.json и не пишет в профиль специальности");
    }

    [Fact]
    public async Task Update_СменаСпециальностиПараллельноССекциями_ИтогОднаИзСериализаций()
    {
        var p = CreatePersona();

        // 50 пар: два Update одной персоны, оба меняют специальность и приносят свой слой.
        // Под _saveLock мутации неперемешиваемы, поэтому итог — одна из двух сериализаций ЦЕЛИКОМ:
        // (planner + её слой) или (backend + её слой). Смесь «специальность одного, слой другого»
        // и потерянная правка — без лока возможны (проверено мутацией), с локом — нет.
        for (var i = 0; i < 50; i++)
        {
            Update(p.Id, specialty: PersonaSpecialty.Reviewer);
            Update(p.Id, promptSections: [Section("history", true, "общий")]);

            var a = Task.Run(() => Update(p.Id, specialty: PersonaSpecialty.Planner,
                promptSections: [Section("processes", true, "слой плана")]));
            var b = Task.Run(() => Update(p.Id, specialty: PersonaSpecialty.BackendExecutor,
                promptSections: [Section("codeGraph", false, null)]));
            await Task.WhenAll(a, b);

            var raw = Cache(p.Id);
            var whole =
                (raw.Specialty == PersonaSpecialty.Planner && OnlySection(raw, "processes")) ||
                (raw.Specialty == PersonaSpecialty.BackendExecutor && OnlySection(raw, "codeGraph"));
            whole.Should().BeTrue(
                $"итог гонки — целая сериализация: specialty={raw.Specialty}, " +
                $"слои=[{string.Join(", ", (raw.PromptSections ?? []).Select(s => s.Id))}]");
        }
    }

    private static bool OnlySection(Persona raw, string id) =>
        raw.PromptSections is { Count: 1 } && raw.PromptSections[0].Id == id;
}
