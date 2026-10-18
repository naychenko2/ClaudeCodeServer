using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Частичная правка контракта персоны (задача 93c4c4dc): personas_update обещает «передавай
/// только изменяемые поля», а слоты характера заменялись целиком — не переданные обнулялись.
/// Семантика: отсутствующий слот (null) — не менять, пустая строка / пустой список — очистить.
/// Путь один на MCP и REST-карточку — <see cref="PersonaManager.Update"/>.
/// </summary>
public class PersonaContractMergeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "persona-merge-" + Guid.NewGuid().ToString("N"));
    private readonly PersonaManager _personas;

    public PersonaContractMergeTests()
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        _personas = new PersonaManager(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private Persona CreateFull() => _personas.Create("u1", "Кира", "QA", null, null, null, null,
        PersonaScope.Global, null, null, "Привет!", memoryEnabled: true, contract: new PersonaContract
        {
            Character = "Ты — дотошный QA.",
            Tone = "сухо",
            MustDo = ["проверять"],
            MustNot = ["гадать"],
            OutputFormat = "списком",
            SpeechExamples = ["Где тест?"],
            Instructions = "Регламент.",
        });

    private Persona UpdateContract(string id, PersonaContract patch) => _personas.Update(id, "u1",
        null, null, null, null, null, null, null, null, null, null, null, contract: patch);

    [Fact]
    public void ЧастичнаяПравка_НеПереданныеСлотыОстаютсяКакБыли()
    {
        var p = CreateFull();

        var updated = UpdateContract(p.Id, new PersonaContract { Tone = "тепло" });

        updated.Contract!.Tone.Should().Be("тепло");
        updated.Contract.Character.Should().Be("Ты — дотошный QA.");
        updated.Contract.MustDo.Should().Equal("проверять");
        updated.Contract.MustNot.Should().Equal("гадать");
        updated.Contract.OutputFormat.Should().Be("списком");
        updated.Contract.SpeechExamples.Should().Equal("Где тест?");
        updated.Contract.Instructions.Should().Be("Регламент.");
        updated.Greeting.Should().Be("Привет!");
    }

    [Fact]
    public void ПустаяСтрокаИПустойСписок_ОчищаютСлот()
    {
        var p = CreateFull();

        var updated = UpdateContract(p.Id, new PersonaContract { Tone = "", MustDo = [] });

        updated.Contract!.Tone.Should().BeNull();
        updated.Contract.MustDo.Should().BeNull();
        updated.Contract.Character.Should().Be("Ты — дотошный QA.");
    }

    [Fact]
    public void ОчисткаВсехСлотов_СбрасываетКонтрактВNull()
    {
        var p = CreateFull();

        var updated = UpdateContract(p.Id, new PersonaContract
        {
            Character = "", Tone = "", MustDo = [], MustNot = [], OutputFormat = "",
            SpeechExamples = [], Instructions = "",
        });

        updated.Contract.Should().BeNull();
    }

    [Fact]
    public void McpUpdate_ПередаётТолькоЯвныеСлоты_ПустыеКакОчистку()
    {
        var args = new JsonObject
        {
            ["id"] = "p1",
            ["tone"] = "тепло",
            ["outputFormat"] = "",
            ["mustNot"] = new JsonArray(),
        };

        var req = PersonasToolset.BuildUpdateRequest(args, sessionProjectId: null);

        req!.Contract.Should().NotBeNull();
        req.Contract!.Tone.Should().Be("тепло");
        req.Contract.OutputFormat.Should().Be("", "пустая строка — явная очистка, а не «не менять»");
        req.Contract.MustNot.Should().BeEmpty();
        req.Contract.Character.Should().BeNull("не переданный слот — «не менять»");
        req.Contract.MustDo.Should().BeNull();
        req.Contract.SpeechExamples.Should().BeNull();
    }

    [Fact]
    public void McpUpdate_ЧерезМенеджер_НеЗатираетКонтракт()
    {
        var p = CreateFull();
        var req = PersonasToolset.BuildUpdateRequest(
            new JsonObject { ["id"] = p.Id, ["character"] = "Ты — новая Кира." }, sessionProjectId: null);

        var updated = UpdateContract(p.Id, req!.Contract!);

        updated.Contract!.Character.Should().Be("Ты — новая Кира.");
        updated.Contract.Tone.Should().Be("сухо");
        updated.Contract.MustDo.Should().Equal("проверять");
        updated.Contract.SpeechExamples.Should().Equal("Где тест?");
    }
}
