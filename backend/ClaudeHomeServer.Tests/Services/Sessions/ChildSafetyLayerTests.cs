using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Sessions;

// KM-B6c (ADR-041 §4): слой детского хода — правила тьютора всегда, независимо от персоны,
// и ни одной recall-секции.
//
// «5 секций recall» из плана (память персоны, память команды, заметки, досье, граф кода)
// живут в четырёх id: личную память, память команды и досье собирает ОДИН контрибьютор
// — он отдаёт и "recall-memory", и "dossier-recall" (PersonaRecallContributor.cs:117-131);
// заметки — "recall-notes" (NotesRecallContributor), граф — "code-graph" (CodeGraphContributor).
// Проверены все четыре id: снятие любого из трёх гейтов красит свой кейс.
public class ChildSafetyLayerTests
{
    private const string OutputFormat = "Отвечай коротко, пунктами.";
    private const string SpeechExample = "Разберём вместе: с чего начнём?";

    private static Persona Tutor() => new()
    {
        Id = "tutor-1",
        Name = "Тьютор",
        Role = "Тьютор по математике",
        Description = "занимается с ребёнком",
        Contract = new PersonaContract
        {
            Character = "Спокойный и терпеливый.",
            Tone = "по-деловому",
            OutputFormat = OutputFormat,
            SpeechExamples = [SpeechExample],
        },
    };

    // Adult — тот же вызов без признака ребёнка: ровно то поведение, что было до шага.
    private static string BuildLayer(bool child) =>
        PersonaPromptBuilder.BuildCore(Tutor(), "claude", switched: false, greeted: false,
            teamMechanicsBlock: null, voiceMode: false, voiceStyle: null, childSafety: child);

    [Fact]
    public void ДетскийСлой_СодержитСекциюПравилТьютора()
    {
        var layer = BuildLayer(child: true);

        layer.Should().Contain($"## {ChildSafetyPrompts.SectionTitle}");
        layer.Should().Contain(ChildSafetyPrompts.SectionText);
    }

    [Fact]
    public void ВзрослыйСлой_НиСекцииНиОговорки()
    {
        var layer = BuildLayer(child: false);

        layer.Should().NotContain(ChildSafetyPrompts.SectionTitle);
        layer.Should().NotContain(ChildSafetyPrompts.SectionText);
        layer.Should().NotContain(ChildSafetyPrompts.PersonaOverride);
    }

    // Оговорка — ПОСЛЕДНИМ блоком: после «Формата ответов», примеров речи и дисциплинарного
    // слоя провайдера. Порядок здесь и есть правило: без него слоты персоны перебивают его
    // тем же способом, каким слой персоны перебивает секции промпта.
    [Fact]
    public void Оговорка_ПоследнимБлоком_ПослеФорматаОтветовИПримеровРечи()
    {
        var layer = BuildLayer(child: true);

        layer.Should().EndWith(ChildSafetyPrompts.PersonaOverride);
        layer.IndexOf(ChildSafetyPrompts.PersonaOverride)
            .Should().BeGreaterThan(layer.IndexOf("## Формат ответов"), "формат персоны не должен перебивать правило");
        layer.IndexOf(ChildSafetyPrompts.PersonaOverride)
            .Should().BeGreaterThan(layer.IndexOf(OutputFormat));
        layer.IndexOf(ChildSafetyPrompts.PersonaOverride)
            .Should().BeGreaterThan(layer.IndexOf("## Примеры твоих реплик"));
        // Секция — перед слотами характера: рамка задаётся до того, как персона заговаривает своим.
        layer.IndexOf($"## {ChildSafetyPrompts.SectionTitle}")
            .Should().BeLessThan(layer.IndexOf("## Характер"));
    }

    // Голосовая оговорка тоже стоит в конце слоя — правила тьютора ниже неё.
    [Fact]
    public void ОговоркаТьютора_НижеГолосовойОговорки()
    {
        var layer = PersonaPromptBuilder.BuildCore(Tutor(), "claude", switched: false, greeted: false,
            teamMechanicsBlock: null, voiceMode: true, voiceStyle: null, childSafety: true);

        layer.Should().EndWith(ChildSafetyPrompts.PersonaOverride);
        layer.IndexOf(ChildSafetyPrompts.PersonaOverride)
            .Should().BeGreaterThan(layer.IndexOf(VoicePrompts.PersonaOverride));
    }

    // Текст правила под сторожем: лесенка с меткой ступени, правило про вложения и форма
    // маркера стоп-сигнала (её разбирает KM-B6d — форма должна совпадать с константой).
    [Theory]
    [InlineData("программа, а не человек")]
    [InlineData("Подсказка")]
    [InlineData("вложени")]
    [InlineData("позвать взрослого")]
    public void ТекстСекции_ДержитПравила(string required) =>
        ChildSafetyPrompts.SectionText.Should().Contain(required);

    [Fact]
    public void ТекстСекции_ДержитКонстантуМаркераСтопСигнала() =>
        ChildSafetyPrompts.SectionText.Should().Contain(ChildSafetyPrompts.StopSignalMarker);

    // --- Пробел: признак ребёнка доходит до сборщика слоя (не только до текстов) ---

    [Fact]
    public async Task КонтрибьюторСлоя_ПередаетПризнакРебёнкаСборщику()
    {
        var persona = Tutor();
        var resolver = new Mock<IPersonaResolver>();
        resolver.Setup(r => r.Get(persona.Id, "owner-1")).Returns(persona);
        var assembler = new Mock<IPersonaPromptAssembler>();
        assembler.Setup(p => p.Build(persona, null, false, false, null, false, null, true)).Returns("слой");
        assembler.Setup(p => p.Build(persona, null, false, false, null, false, null, false)).Returns("слой");

        var contributor = new PersonaLayerContributor(
            new Mock<IUserStore>().Object, resolver.Object, assembler.Object,
            new Mock<IProjectManager>().Object, null, null);

        await contributor.BuildAsync(Ctx(isChild: true, persona: persona), "реши уравнение");
        await contributor.BuildAsync(Ctx(isChild: false, persona: persona), "реши уравнение");

        assembler.Verify(p => p.Build(persona, null, false, false, null, false, null, true), Times.Once);
        assembler.Verify(p => p.Build(persona, null, false, false, null, false, null, false), Times.Once);
    }

    // --- Отключение recall: работа не делается вовсе (гейт IsEnabled), а не вырезается из склейки ---

    [Fact]
    public void ПамятьПерсоныКомандыИДосье_ДетскомуХодуНеСобираются()
    {
        var contributor = new PersonaRecallContributor(
            new Mock<IPersonaRecallSource>().Object, new Mock<IChatHistoryLoader>().Object,
            new Mock<IProjectManager>().Object, new ConfigurationBuilder().Build(),
            NullLogger<PersonaRecallContributor>.Instance);

        contributor.IsEnabled(Ctx(isChild: true, memoryMcp: true, persona: Tutor())).Should().BeFalse();
        // Взрослому — как раньше: тот же контекст без признака ребёнка включён.
        contributor.IsEnabled(Ctx(isChild: false, memoryMcp: true, persona: Tutor())).Should().BeTrue();
    }

    [Fact]
    public void Заметки_ДетскомуХодуНеСобираются()
    {
        // IsEnabled поля не читает — экземпляра с рабочей инфраструктурой не нужно (тот же
        // приём, что у CodeGraphContributorIsEnabledTests: GetUninitializedObject).
        var notes = (NotesKnowledgeService)RuntimeHelpers.GetUninitializedObject(typeof(NotesKnowledgeService));
        var contributor = new NotesRecallContributor(notes, new ConfigurationBuilder().Build(),
            NullLogger<NotesRecallContributor>.Instance);

        contributor.IsEnabled(Ctx(isChild: true, notesMcp: true)).Should().BeFalse();
        contributor.IsEnabled(Ctx(isChild: false, notesMcp: true)).Should().BeTrue();
    }

    [Fact]
    public void ГрафКода_ДетскомуХодуНеСобирается()
    {
        var provider = (CodeGraphPromptProvider)RuntimeHelpers.GetUninitializedObject(typeof(CodeGraphPromptProvider));
        var gate = new Mock<IPersonaServerToolGate>();
        gate.Setup(g => g.IsServerToolEnabled(It.IsAny<string?>(), It.IsAny<Persona?>(), "codegraph")).Returns(true);
        var contributor = new CodeGraphContributor(provider, gate.Object, NullLogger<CodeGraphContributor>.Instance);

        contributor.IsEnabled(Ctx(isChild: true)).Should().BeFalse();
        contributor.IsEnabled(Ctx(isChild: false)).Should().BeTrue();
    }

    [Fact]
    public void ПривязкиПерсоны_ДетскомуХодуНеСобираются()
    {
        var contributor = new PersonaBindingsContributor(
            new Mock<IPersonaBindingsSource>().Object, NullLogger<PersonaBindingsContributor>.Instance);

        contributor.IsEnabled(Ctx(isChild: true, persona: Tutor())).Should().BeFalse();
        contributor.IsEnabled(Ctx(isChild: false, persona: Tutor())).Should().BeTrue();
    }

    [Fact]
    public void КонтрактыФайловЗадачи_ДетскомуХодуНеСобираются()
    {
        var graphs = (CodeGraphService)RuntimeHelpers.GetUninitializedObject(typeof(CodeGraphService));
        var queries = (CodeGraphQueryService)RuntimeHelpers.GetUninitializedObject(typeof(CodeGraphQueryService));
        var gate = new Mock<IPersonaServerToolGate>();
        gate.Setup(g => g.IsServerToolEnabled(It.IsAny<string?>(), It.IsAny<Persona?>(), "codegraph")).Returns(true);
        var contributor = new ConsumersTailContributor(graphs, queries, gate.Object,
            NullLogger<ConsumersTailContributor>.Instance);

        contributor.IsEnabled(CtxTask(isChild: true)).Should().BeFalse();
        contributor.IsEnabled(CtxTask(isChild: false)).Should().BeTrue();
    }

    private static PromptSessionContext CtxTask(bool isChild) =>
        new(new Session { Id = "chat-1", OwnerId = "owner-1", TaskExecution = true, MessageCount = 1 },
            "owner-1", null, "/tmp/proj", IsChild: isChild);

    private static PromptSessionContext Ctx(bool isChild, bool memoryMcp = false, bool notesMcp = false,
        Persona? persona = null) =>
        new(new Session { Id = "chat-1", OwnerId = "owner-1", PersonaId = persona?.Id }, "owner-1", persona,
            "/tmp/proj", HasNotesMcp: notesMcp, HasMemoryMcp: memoryMcp, IsChild: isChild);
}
