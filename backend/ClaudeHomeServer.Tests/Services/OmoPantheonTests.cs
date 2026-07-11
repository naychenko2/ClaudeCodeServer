using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Подключаемая команда «Пантеон OmO»: каталог, идемпотентный connect, авто-обновление регламентов
public class OmoPantheonTests : IDisposable
{
    private const string OwnerId = "user-1";
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly PersonaManager _personas;

    public OmoPantheonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pantheon_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _personas = new PersonaManager(_config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Каталог_ВосемьРолейСПолнымиРегламентами()
    {
        OmoPantheonCatalog.All.Should().HaveCount(8);
        OmoPantheonCatalog.All.Should().OnlyContain(t =>
            !string.IsNullOrWhiteSpace(t.Contract.Instructions)
            && !string.IsNullOrWhiteSpace(t.Name)
            && t.Key.StartsWith("omo-"));
    }

    [Fact]
    public void Connect_СоздаётГлобальныхСГотовымиИменамиИЧистымиHandle()
    {
        var personas = _personas.ConnectPantheon(OwnerId);

        personas.Should().HaveCount(8);
        personas.Should().OnlyContain(p => p.Scope == PersonaScope.Global && p.TemplateKey != null);
        var momus = personas.Single(p => p.TemplateKey == "omo-momus");
        momus.Name.Should().Be("Мом");
        momus.Handle.Should().Be("mom");
        momus.Access.Should().Be(PersonaAccess.ReadOnly);
        momus.Contract!.Instructions.Should().Be(OmoPantheonCatalog.Get("omo-momus")!.Contract.Instructions);
        momus.TemplateInstructionsHash.Should().Be(
            PersonaManager.HashInstructions(momus.Contract.Instructions));
    }

    [Fact]
    public void Connect_Повторный_БезДублей()
    {
        var first = _personas.ConnectPantheon(OwnerId);
        var second = _personas.ConnectPantheon(OwnerId);

        second.Select(p => p.Id).Should().BeEquivalentTo(first.Select(p => p.Id));
        _personas.GetByOwner(OwnerId).Should().HaveCount(8);
    }

    [Fact]
    public void Connect_ЧастичныйПослеУдаления_ДосоздаётТолькоНедостающих()
    {
        var first = _personas.ConnectPantheon(OwnerId);
        var oracle = first.Single(p => p.TemplateKey == "omo-oracle");
        _personas.Delete(oracle.Id, OwnerId);

        var second = _personas.ConnectPantheon(OwnerId);

        second.Should().HaveCount(8);
        second.Single(p => p.TemplateKey == "omo-oracle").Id.Should().NotBe(oracle.Id);
        _personas.GetByOwner(OwnerId).Should().HaveCount(8);
    }

    [Fact]
    public void Connect_НеизвестныйКлюч_Исключение()
    {
        var act = () => _personas.ConnectPantheon(OwnerId, ["omo-zeus"]);
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void АвтоОбновление_НетронутаяОбновляется_ПравленаяПришпилена()
    {
        var personas = _personas.ConnectPantheon(OwnerId);
        var untouched = personas.Single(p => p.TemplateKey == "omo-momus");
        var pinned = personas.Single(p => p.TemplateKey == "omo-oracle");

        // Правка пользователя: инструкция Оракула отличается от поставленной из каталога
        _personas.Update(pinned.Id, OwnerId, null, null, null, null, null, null, null, null,
            null, null, null, contract: new PersonaContract
            {
                Character = pinned.Contract!.Character,
                Instructions = "Мой собственный регламент.",
            });

        // Имитация устаревшего стора: у обеих персон инструкция и hash от «прошлой версии каталога»
        var stale = "Старый регламент из прошлой версии каталога.";
        untouched.Contract!.Instructions = stale;
        untouched.TemplateInstructionsHash = PersonaManager.HashInstructions(stale);
        var pinnedInstructions = _personas.Get(pinned.Id, OwnerId)!.Contract!.Instructions;

        // Мутация untouched сделана в обход Update — сохраняем стор вручную,
        // затем «рестарт сервера»: новый PersonaManager над тем же стором → RefreshPantheonInstructions
        typeof(PersonaManager).GetMethod("Save",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_personas, null);
        var restarted = new PersonaManager(_config);

        var freshUntouched = restarted.Get(untouched.Id, OwnerId)!;
        freshUntouched.Contract!.Instructions.Should()
            .Be(OmoPantheonCatalog.Get("omo-momus")!.Contract.Instructions, "нетронутая подтягивается из каталога");
        freshUntouched.TemplateInstructionsHash.Should()
            .Be(PersonaManager.HashInstructions(freshUntouched.Contract.Instructions));

        restarted.Get(pinned.Id, OwnerId)!.Contract!.Instructions.Should()
            .Be(pinnedInstructions, "правленая пользователем пришпилена");
    }
}
