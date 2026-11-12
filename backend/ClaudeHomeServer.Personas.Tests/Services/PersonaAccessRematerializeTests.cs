using System.Security.Cryptography;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// A1c: после сохранения специальности перематериализация переписывает персистентные права
// наследующих персон этой специальности эффективным шаблоном (снимок для отката кода).
// Override не трогается; specialty-settings.json — никогда не пишется (И1)
public class PersonaAccessRematerializeTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _tempDir;
    private readonly SpecialtySettingsStore _specialty;
    private readonly PersonaManager _sut;

    public PersonaAccessRematerializeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_remat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            ["Ollama:Model"] = "",
        });
        _specialty = TestSpecialtyStore.Create(config);
        _sut = new PersonaManager(config, NullLogger<PersonaManager>.Instance, specialty: _specialty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_tempDir, "specialty-settings.json");
    private string PersonasPath => Path.Combine(_tempDir, "personas.json");

    private void SetTemplate(PersonaAccess access, List<string>? tools, List<string>? disallowed)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            Access = access, Tools = tools, DisallowedTools = disallowed,
        };
        _specialty.SetGlobal(layer);
    }

    // Персона с флагом наследования и материализованным Full (дефолт Create); флаг ставим
    // в кэше напрямую, как в A1a-тестах (путь записки флага через REST — A1d, не здесь)
    private Persona MakeInheriting(PersonaSpecialty specialty = PersonaSpecialty.Reviewer, bool inherits = true)
    {
        var created = _sut.Create(Owner, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, true, specialty: specialty);
        _sut.GetRaw(created.Id, Owner)!.InheritsAccess = inherits;
        return created;
    }

    private Persona Cache(string id) => _sut.GetRaw(id, Owner)!;

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Rematerialize_НаследующаяПерсона_ПолучаетЭффективныйШаблонВПерсистентныеПоля()
    {
        var p = MakeInheriting(); // персистентный Full — снимок протухает при новой правке
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        var raw = Cache(p.Id);
        raw.Access.Should().Be(PersonaAccess.Custom);
        raw.Tools.Should().Equal("notes");
        raw.DisallowedTools.Should().Equal("Bash");
    }

    // c1: персона с InheritsAccess=false после сохранения специальности не изменилась
    [Fact]
    public void Rematerialize_Override_НеИзмениласьПослеСохраненияСпециальности()
    {
        var p = MakeInheriting(inherits: false);
        Cache(p.Id).Access = PersonaAccess.ReadOnly; // свой override
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        Cache(p.Id).Access.Should().Be(PersonaAccess.ReadOnly, "override перематериализация не трогает");
        Cache(p.Id).DisallowedTools.Should().BeNull();
    }

    [Fact]
    public void Rematerialize_ЧужаяСпециальность_НеТронута()
    {
        var other = MakeInheriting(PersonaSpecialty.Planner);
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        Cache(other.Id).Access.Should().Be(PersonaAccess.Full);
    }

    // None — дефолт кода Full без ограничений (та же конвенция, что у Hydrate): протухший
    // снимок None-персоны приводится к нему
    [Fact]
    public void Rematerialize_СпециальностьNone_СнимокПолныйБезОграничений()
    {
        var p = MakeInheriting(PersonaSpecialty.None);
        Cache(p.Id).Access = PersonaAccess.ReadOnly; // протухший снимок

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.None);

        Cache(p.Id).Access.Should().Be(PersonaAccess.Full);
        Cache(p.Id).Tools.Should().BeNull();
        Cache(p.Id).DisallowedTools.Should().BeNull();
    }

    // И1: перематериализация пишет только персоны, никогда specialty-settings.json
    [Fact]
    public void Rematerialize_ФайлНастроекСпециальностей_НеТронут()
    {
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        MakeInheriting();
        var hashBefore = Hash(SettingsPath);

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        Hash(SettingsPath).Should().Be(hashBefore);
    }

    // Снимок уже свежий — второй вызов ничего не пишет (одна запись стора на изменение,
    // без изменений — записи нет)
    [Fact]
    public void Rematerialize_БезИзменений_СторПерсонНеПерезаписывается()
    {
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        MakeInheriting();
        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);
        var mtime = File.GetLastWriteTimeUtc(PersonasPath);

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        File.GetLastWriteTimeUtc(PersonasPath).Should().Be(mtime);
    }

    // c2: гонка сохранения специальности с переключением персоны в «своё». Правильный порядок
    // сериализован одним _saveLock (флаг читается под ним, как и запись Update): переключённая
    // в override персона не затирается ни при какой интерполяции — итог каждой итерации
    // «Full + флаг false». Мутация «проверять InheritsAccess вне лока» даёт интерполяцию, где
    // перематериализация пишет снимок уже опущенному флагу (Custom) → красный
    [Fact]
    public async Task Rematerialize_ПараллельноСOverride_OverrideЖив()
    {
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        var overridden = MakeInheriting();
        // Филлеры чужой специальности: их много и они не попадают в обход перематериализации,
        // но замедляют валидацию handle внутри лока Update — гонка стабильно попадает в окно
        // «флаг прочитан, ещё не записан»
        var fillers = Enumerable.Range(0, 60)
            .Select(i => _sut.Create(Owner, $"Filler {i}", null, null, null, null, null,
                PersonaScope.Global, null, null, null, true, specialty: PersonaSpecialty.Planner,
                handle: $"filler-{i:D2}")).ToList();

        var erased = 0;
        for (var i = 0; i < 10; i++)
        {
            // Сброс: целевая персона снова «наследуёт» с полными правами (отличаются от шаблона)
            _sut.Update(overridden.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
                access: PersonaAccess.Full, inheritsAccess: true);

            // Параллельные: сохранение специальности (перематериализация) и пользовательский
            // переход в «своё» со своими полными правами. Правильный порядок сериализован одним
            // _saveLock: итог любой интерполяции — Full + флаг false
            var remat = Task.Run(() => _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer));
            var user = Task.Run(() => _sut.Update(overridden.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
                access: PersonaAccess.Full, inheritsAccess: false, handle: "my-own-handle"));
            await Task.WhenAll(remat, user);

            var raw = Cache(overridden.Id);
            raw.InheritsAccess.Should().BeFalse("переход в override не отменяется перематериализацией");
            if (raw.Access != PersonaAccess.Full) Interlocked.Increment(ref erased);
        }

        erased.Should().Be(0, "override не затёрт ни одной параллельной перематериализацией");
        Cache(fillers[0].Id).Access.Should().Be(PersonaAccess.Full, "чужая специальность не тронута");
    }

    // Две подряд правки специальности — итог по последней (снимок не протухает)
    [Fact]
    public void Rematerialize_ДвеПодрядПравкиСпециальности_ИтогПоПоследней()
    {
        var p = MakeInheriting();
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        Cache(p.Id).Access.Should().Be(PersonaAccess.ReadOnly);
        Cache(p.Id).Tools.Should().Equal("tasks");
        Cache(p.Id).DisallowedTools.Should().BeNull();
    }

    // 200+ персон одной специальности — одна запись стора, а не N: все обновлены за один вызов
    [Fact]
    public void Rematerialize_МногоПерсон_ВсеОбновленыЗаОдинВызов()
    {
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        var ids = Enumerable.Range(0, 200).Select(_ => MakeInheriting().Id).ToList();

        _sut.RematerializeSpecialtyAccess(PersonaSpecialty.Reviewer);

        ids.Should().HaveCount(200);
        ids.Select(id => Cache(id).Access).Should().OnlyContain(a => a == PersonaAccess.Custom);
    }
}
