using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.LocalMedia;

// Граф обучения LoRA: через границу едут только белый список (ADR-026 §4–5)
public class ComfyWorkflowsLoraTests
{
    private const string Owner = "a300cc8f-0074-4000-8000-00000000aaaa";
    private const string OwnerKey = "a300cc8f007440008000" + "00000000aaaa";
    private const string Run = "3f9a1c2b7d10";
    private const string Photo = "0a1b2c3d4e5f6a7b.jpg";
    private static readonly string Folder = $"ccs/{OwnerKey}/{Run}/";

    private static JsonObject Worker(JsonObject graph) => graph["worker"]!["inputs"]!.AsObject();

    [Fact]
    public void Граф_обучения_один_узел_с_белым_списком()
    {
        var g = ComfyWorkflows.LoraTrain(Owner, "andrey", Run, "ccsandrey", "standard", [Folder + Photo], "job1");

        g.Count.Should().Be(1);
        g["worker"]!["class_type"]!.GetValue<string>().Should().Be("CcsAudioWorker");
        var w = Worker(g);
        w["op"]!.GetValue<string>().Should().Be("lora_train");
        w["inputs"]!.GetValue<string>().Should().Be(Folder + Photo);
        w["timeout_minutes"]!.GetValue<int>().Should().Be(240);
        w["filename_prefix"]!.GetValue<string>().Should().Be("job1");
        var p = JsonNode.Parse(w["params_json"]!.GetValue<string>())!.AsObject();
        p.Select(kv => kv.Key).Should().BeEquivalentTo("owner", "slug", "run_id", "trigger", "preset");
        p["owner"]!.GetValue<string>().Should().Be(OwnerKey);
        p["run_id"]!.GetValue<string>().Should().Be(Run);
    }

    [Theory]
    [InlineData("Ccsx")]
    [InlineData("1abc")]
    [InlineData("ab")]
    [InlineData("a-bc")]
    [InlineData("ccs_very_long_trigger_name_over_limit")]
    public void Триггер_вне_формата_отказ(string trigger) =>
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, "andrey", Run, trigger, "standard", [Folder + Photo], "j"));

    [Theory]
    [InlineData("../andrey", Run)]
    [InlineData("andrey/x", Run)]
    [InlineData("andrey", "../../etc/xx")]
    [InlineData("andrey", "ABCDEF123456")]
    [InlineData("andrey", "3f9a1c2b7d1")]
    public void Слаг_и_runId_с_мусором_отказ(string slug, string runId) =>
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, slug, runId, "ccsandrey", "standard", [Folder + Photo], "j"));

    [Fact]
    public void Пустой_список_фото_и_неизвестный_пресет_отказ()
    {
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, "andrey", Run, "ccsandrey", "standard", [], "j"));
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, "andrey", Run, "ccsandrey", "huge", [Folder + Photo], "j"));
    }

    [Theory]
    [InlineData("0a1b2c3d4e5f6a7b.jpg/../../x.jpg")]
    [InlineData("../0a1b2c3d4e5f6a7b.jpg")]
    [InlineData("0a1b2c3d4e5f6a7b.exe")]
    [InlineData("ZZ1b2c3d4e5f6a7b.jpg")]
    public void Имя_фото_с_путём_или_чужим_видом_отказ(string name) =>
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, "andrey", Run, "ccsandrey", "standard", [Folder + name], "j"));

    [Fact]
    public void Фото_чужой_папки_отказ() =>
        Invoking(() => ComfyWorkflows.LoraTrain(Owner, "andrey", Run, "ccsandrey", "standard",
            [$"ccs/{new string('0', 32)}/{Run}/{Photo}"], "j"));

    [Fact]
    public void Удаление_несёт_только_три_сегмента()
    {
        var w = Worker(ComfyWorkflows.LoraDelete(Owner, "andrey", Run, "job2"));
        w["op"]!.GetValue<string>().Should().Be("lora_delete");
        w["inputs"]!.GetValue<string>().Should().BeEmpty();
        w["timeout_minutes"]!.GetValue<int>().Should().Be(1);
        var p = JsonNode.Parse(w["params_json"]!.GetValue<string>())!.AsObject();
        p.Select(kv => kv.Key).Should().BeEquivalentTo("owner", "slug", "run_id");
    }

    [Fact]
    public void Удаление_с_плохим_слагом_отказ() =>
        Invoking(() => ComfyWorkflows.LoraDelete(Owner, "..", Run, "j"));

    [Fact]
    public void Оценка_по_пресету_и_остаток_по_шагу()
    {
        LocalMediaService.LoraTrainEta("standard").Should().Be(6660);
        LocalMediaService.LoraTrainEta("huge").Should().BeNull();
        LocalMediaService.LoraTrainRemaining(0, 2000).Should().Be(4700);
        LocalMediaService.LoraTrainRemaining(2000, 2000).Should().Be(0);
    }

    [Fact]
    public void Обучение_по_умолчанию_выключено_и_не_инструмент_mcp()
    {
        new LocalMediaOptions().LoraTrainEnabled.Should().BeFalse();
        LocalMediaOps.IsKnown("lora_train").Should().BeFalse();
        LocalMediaOps.IsKnown("lora_delete").Should().BeFalse();
    }

    [Fact]
    public void Пространство_имён_прогона_строится_по_контракту()
    {
        LoraNamespace.OwnerKey(Owner).Should().Be(OwnerKey);
        LoraNamespace.RunLoraFile(Owner, "andrey", Run).Should().Be($"ccs/{OwnerKey}/andrey-{Run}/andrey.safetensors");
        LoraNamespace.RunInputFolder(Owner, Run).Should().Be($"ccs/{OwnerKey}/{Run}");
        LoraNamespace.VisibleTo(Owner, LoraNamespace.RunLoraFile(Owner, "andrey", Run)).Should().BeTrue();
        Invoking(() => LoraNamespace.OwnerKey("not-a-guid"));
    }

    private static void Invoking(Action act) => act.Should().Throw<ArgumentException>();
}
