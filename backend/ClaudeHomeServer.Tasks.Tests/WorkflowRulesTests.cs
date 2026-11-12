using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Чистые правила шаблона процесса (B1a, разрез ai-fabrika-workflow-cut-2026-11 §1):
// unit-тесты без стенда — WorkflowRules не трогает хранилище и HTTP.
public class WorkflowRulesTests
{
    private static WorkflowStage Stage(
        string role = TaskRoles.Step,
        string? presence = WorkflowStagePresence.On) =>
        new() { Role = role, Presence = presence! };

    private static List<string> ValidateOne(WorkflowStage stage) =>
        WorkflowRules.Validate(new WorkflowTemplate { Name = "тест", Stages = [stage] });

    [Fact]
    public void ВалидныйЭтап_БезОшибок()
    {
        var stage = Stage(); // step — Presence только on (несъёмная роль)
        stage.Specialty = PersonaSpecialty.BackendExecutor;
        stage.Prompt = "держи границы задачи";
        stage.TierStrong = "qwen3.8-27b"; // id модели — легальная лексика ячейки (ADR-007)
        stage.StallMinutes = 30;
        stage.StallPolicy = WorkflowStallPolicies.NudgeAlert;

        ValidateOne(stage).Should().BeEmpty();
    }

    // c2: несъёмные роли — ни off, ни optional (6 случаев). Мутация «убрать final из
    // NonSkippable» красит ровно два случая final.
    [Theory]
    [InlineData(TaskRoles.Step, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Step, WorkflowStagePresence.Optional)]
    [InlineData(TaskRoles.Merge, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Merge, WorkflowStagePresence.Optional)]
    [InlineData(TaskRoles.Final, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Final, WorkflowStagePresence.Optional)]
    public void НесъёмнаяРоль_СнятаяИлиОпциональная_Ошибка(string role, string presence)
    {
        var errors = ValidateOne(Stage(role, presence));

        errors.Should().ContainSingle()
            .Which.Should().Contain(role).And.Contain(presence);
    }

    // Съёмные роли — off и optional проходят: фаст-трек живёт именно на этом.
    [Theory]
    [InlineData(TaskRoles.Design, WorkflowStagePresence.Off)]
    [InlineData(TaskRoles.Qa, WorkflowStagePresence.Optional)]
    public void СъёмнаяРоль_ПропускРазрешён(string role, string presence) =>
        ValidateOne(Stage(role, presence)).Should().BeEmpty();

    // Роли вне маршрута: rework берёт этап step, эпик и consult — не этап шаблона.
    [Theory]
    [InlineData(TaskRoles.Rework)]
    [InlineData(TaskRoles.Feature)]
    [InlineData(TaskRoles.Consult)]
    [InlineData("")]
    public void НеизвестнаяРольЭтапа_Ошибка(string role) =>
        ValidateOne(Stage(role)).Should().ContainSingle()
            .Which.Should().Contain("неизвестная роль");

    [Fact]
    public void ДубльРоли_Ошибка() =>
        WorkflowRules.Validate(new WorkflowTemplate
        {
            Name = "тест",
            Stages = [Stage(), Stage()],
        }).Should().ContainSingle().Which.Should().Contain("дважды");

    [Theory]
    [InlineData(null)]
    [InlineData("always")]
    public void НеизвестныйPresence_Ошибка(string? presence) =>
        ValidateOne(Stage(TaskRoles.Review, presence)).Should().ContainSingle()
            .Which.Should().Contain("неизвестное Presence");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void СпециальностьИлиПерсона_ПоОдной_Проходит(bool withSpecialty, bool withPersonaId)
    {
        var stage = Stage();
        if (withSpecialty) stage.Specialty = PersonaSpecialty.Tester;
        if (withPersonaId) stage.PersonaId = "persona-1";

        ValidateOne(stage).Should().BeEmpty();
    }

    [Fact]
    public void СпециальностьИПерсонаОдновременно_Ошибка()
    {
        var stage = Stage();
        stage.Specialty = PersonaSpecialty.Tester;
        stage.PersonaId = "persona-1";

        ValidateOne(stage).Should().ContainSingle().Which.Should().Contain("взаимоисключающи");
    }

    // c4: лексика ячейки — как у специальности: "tier:*" в ячейке этапа запрещён
    // (та же проверка SpecialtySettingsStore.ValidateModelCell).
    [Theory]
    [InlineData("TierStrong")]
    [InlineData("TierMedium")]
    [InlineData("TierWeak")]
    public void ЯчейкаЭтапаTierМаршрут_Ошибка(string cellName)
    {
        var stage = Stage();
        typeof(WorkflowStage).GetProperty(cellName)!.SetValue(stage, "tier:strong");

        ValidateOne(stage).Should().ContainSingle()
            .Which.Should().Contain("не может быть tier:*");
    }

    // c4: белый список политик простоя (строки, не enum).
    [Theory]
    [InlineData("ignore")]
    [InlineData("stop")]
    public void НеизвестнаяПолитикаПростоя_Ошибка(string policy)
    {
        var stage = Stage();
        stage.StallPolicy = policy;

        ValidateOne(stage).Should().ContainSingle().Which.Should().Contain("политика простоя");
    }

    [Theory]
    [InlineData(WorkflowStallPolicies.NudgeAlert)]
    [InlineData(WorkflowStallPolicies.Alert)]
    [InlineData(WorkflowStallPolicies.StopAlert)]
    public void БелыйСписокПолитикПростоя_Проходит(string policy)
    {
        var stage = Stage();
        stage.StallPolicy = policy;

        ValidateOne(stage).Should().BeEmpty();
    }

    // c4: промпт этапа — строго за 4000.
    [Fact]
    public void ПромптДлиннееЛимита_Ошибка()
    {
        var stage = Stage();
        stage.Prompt = new string('а', WorkflowRules.PromptLimit + 1);

        ValidateOne(stage).Should().ContainSingle().Which.Should().Contain("длиннее 4000");
    }

    // c3 (И3 «таймаут нельзя обойти»): null/0/отрицательное — дефолт конфига (null),
    // ниже пола — пол, выше потолка — кламп. Мутация «снять верхний кламп» красит
    // ровно случай 999.
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 5)]
    [InlineData(3, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(240, 240)]
    [InlineData(241, 240)]
    [InlineData(999, 240)]
    public void NormalizeStall_КлампПол(int? minutes, int? expected) =>
        WorkflowRules.NormalizeStall(minutes).Should().Be(expected);
}
