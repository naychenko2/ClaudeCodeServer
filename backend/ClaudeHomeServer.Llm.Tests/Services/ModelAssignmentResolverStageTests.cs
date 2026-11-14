using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Модель исполнителя по слепку этапа (B3, И4): матрица этапа стоит ПЕРЕД персоной и специальностью,
// уровень этапа заменяет дефолт места, явная Persona.Model сильнее этапа. Читается только
// task.Stage — шаблон резолвер не знает вовсе.
public class ModelAssignmentResolverStageTests
{
    private const string Owner = "user-1";

    private static ModelAssignmentResolver Build()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new() { ["DataPath"] = Path.Combine(dir, "projects.json") });
        var app = new AppSettingsService(config);
        app.Save(new AppSettings { ModelTierStrong = "slot-strong", ModelTierMedium = "slot-medium", ModelTierWeak = "slot-weak" });
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        return new ModelAssignmentResolver(app, userTiers: new UserModelTierResolver(users, app));
    }

    private static TaskItem Task(StageSnapshot? stage, ModelTier? tier = null) =>
        new() { Title = "t", OwnerId = Owner, Stage = stage, ModelTier = tier };

    [Fact]
    public void ExecutorModel_УровеньЗадачи_МатрицаЭтапаСильнееПерсоны()
    {
        var persona = new Persona { TierStrong = "persona-strong" };
        var stage = new StageSnapshot { TierStrong = "stage-strong" };

        Build().ExecutorModel(Task(stage, ModelTier.Strong), persona, Owner).Should().Be("stage-strong");
    }

    [Fact]
    public void ExecutorModel_ЯчейкиЭтапаНет_ПадаетНаПерсонуПотомСлот()
    {
        var persona = new Persona { TierStrong = "persona-strong" };
        var stage = new StageSnapshot { TierWeak = "stage-weak" };
        var sut = Build();

        sut.ExecutorModel(Task(stage, ModelTier.Strong), persona, Owner).Should().Be("persona-strong");
        sut.ExecutorModel(Task(stage, ModelTier.Medium), persona, Owner).Should().Be("slot-medium");
    }

    [Fact]
    public void ExecutorModel_БезУровняЗадачи_УровеньЭтапаЗаменяетДефолтМеста()
    {
        var persona = new Persona { TierStrong = "persona-strong", TierWeak = "persona-weak" };
        var stage = new StageSnapshot { DefaultTier = ModelTier.Weak };

        // дефолт места «исполнитель» — Strong, но этап просит Weak → ячейка персоны слабого уровня
        Build().ExecutorModel(Task(stage), persona, Owner).Should().Be("persona-weak");
    }

    [Fact]
    public void ExecutorModel_ЯвнаяМодельПерсоны_СильнееЭтапа()
    {
        var persona = new Persona { Model = "explicit-model" };
        var stage = new StageSnapshot { TierStrong = "stage-strong", DefaultTier = ModelTier.Strong };

        Build().ExecutorModel(Task(stage), persona, Owner).Should().Be("explicit-model");
    }

    [Fact]
    public void ExecutorModel_БезПерсоныСМатрицейЭтапа_БерётЯчейкуЭтапа()
    {
        var stage = new StageSnapshot { TierMedium = "stage-medium", DefaultTier = ModelTier.Medium };

        Build().ExecutorModel(Task(stage), null, Owner).Should().Be("stage-medium");
    }

    [Fact]
    public void ExecutorModel_БезЭтапаИБезПерсоны_Null_КакРаньше()
    {
        Build().ExecutorModel(Task(null), null, Owner).Should().BeNull();
    }

    [Fact]
    public void ExecutorModel_СлепокБезМатрицыИУровня_НичегоНеМеняет()
    {
        var persona = new Persona { TierStrong = "persona-strong" };

        Build().ExecutorModel(Task(new StageSnapshot { Role = "step" }), persona, Owner).Should().Be("persona-strong");
    }
}
