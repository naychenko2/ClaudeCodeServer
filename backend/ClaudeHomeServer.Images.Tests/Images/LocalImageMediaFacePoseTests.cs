using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Ракурс лица через ComfyUI (ADR-027, §3): разбор ответа узла CcsFacePose по именам полей,
// подача только в пустую очередь, Busy/Unavailable — явные результаты, а не исключения
public class LocalImageMediaFacePoseTests
{
    private readonly FakeComfy _comfy = new();

    private LocalImageMediaAdapter Build(bool enabled = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        return new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
    }

    private static byte[][] Photos(int n) => [.. Enumerable.Range(0, n).Select(_ => LocalMediaTestImages.Png(8, 8))];

    private void Answer(string text, string promptId = "prompt-1")
    {
        _comfy.Pending.Remove(promptId);
        _comfy.History[promptId] = new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true, ["messages"] = new JsonArray() },
            ["outputs"] = new JsonObject { ["pose"] = new JsonObject { ["text"] = new JsonArray(text) } },
        };
    }

    private static readonly string[] One = ["a.png"];
    private static readonly string[] Two = ["a.png", "b.png"];

    [Fact]
    public void Разбор_читает_оси_по_именам_а_не_по_порядку_ключей()
    {
        var poses = LocalImageMediaAdapter.ParseFacePoses(
            ["ccs_face_pose={\"a.png\":{\"roll\":3,\"pitch\":-20.5,\"yaw\":47.25,\"extra\":1}}"], One);

        poses.Should().ContainSingle().Which.Should().Be(new LocalFacePose(Yaw: 47.25, Pitch: -20.5, Roll: 3));
    }

    [Fact]
    public void Разбор_отдаёт_позы_в_порядке_входа_а_не_ответа_и_узнаёт_файл_по_пути()
    {
        var poses = LocalImageMediaAdapter.ParseFacePoses(
            ["ccs_face_pose={\"/in/b.png\":{\"yaw\":5,\"pitch\":6,\"roll\":7},\"a.png\":{\"yaw\":1,\"pitch\":2,\"roll\":3}}"], Two);

        poses.Should().Equal(new LocalFacePose(1, 2, 3), new LocalFacePose(5, 6, 7));
    }

    [Fact]
    public void Разбор_все_поля_null_значит_лица_нет()
    {
        var poses = LocalImageMediaAdapter.ParseFacePoses(
            ["ccs_face_pose={\"a.png\":{\"yaw\":null,\"pitch\":null,\"roll\":null},\"b.png\":{\"yaw\":1,\"pitch\":2,\"roll\":3}}"], Two);

        poses.Should().HaveCount(2);
        poses![0].Should().BeNull();
        poses[1].Should().Be(new LocalFacePose(1, 2, 3));
    }

    [Theory]
    [InlineData("ничего")]
    [InlineData("ccs_face_pose=error")]
    [InlineData("ccs_face_pose=[]")]
    [InlineData("ccs_face_pose={}")]
    [InlineData("ccs_face_pose={\"a.png\":42}")]
    [InlineData("ccs_face_pose={\"a.png\":{\"yaw\":1,\"pitch\":2}}")]
    [InlineData("ccs_face_pose={\"a.png\":{\"yaw\":1,\"pitch\":null,\"roll\":3}}")]
    [InlineData("ccs_face_pose={\"a.png\":{\"yaw\":\"1\",\"pitch\":2,\"roll\":3}}")]
    [InlineData("ccs_face_pose={\"другой.png\":{\"yaw\":1,\"pitch\":2,\"roll\":3}}")]
    public void Негодный_ответ_не_превращается_в_лица_нет(string text)
    {
        LocalImageMediaAdapter.ParseFacePoses([text], One).Should().BeNull();
    }

    [Fact]
    public async Task Ответ_узла_доходит_до_вызывающего_в_порядке_входа()
    {
        // Имена загруженных файлов несут случайный суффикс, поэтому ответ собираем после загрузки
        var answering = Task.Run(async () =>
        {
            while (_comfy.Prompts.Count == 0) await Task.Delay(10);
            var names = _comfy.UploadPaths.ToArray();
            Answer($"ccs_face_pose={{\"{names[1]}\":{{\"yaw\":null,\"pitch\":null,\"roll\":null}},\"{names[0]}\":{{\"yaw\":10,\"pitch\":0,\"roll\":0}}}}");
        });
        var result = await Build().FacePoseAsync(Photos(2), CancellationToken.None);
        await answering;

        result.Error.Should().BeNull();
        result.Busy.Should().BeFalse();
        result.Poses.Should().HaveCount(2);
        result.Poses![0].Should().Be(new LocalFacePose(10, 0, 0));
        result.Poses[1].Should().BeNull();
        _comfy.Uploads.Should().HaveCount(2);
        var inputs = _comfy.Prompts.Single()["prompt"]!["pose"]!["inputs"]!["references"]!.GetValue<string>().Split('\n');
        inputs.Should().HaveCount(2, "в граф уходят имена загруженных фото, по одному на строку");
    }

    [Fact]
    public async Task Занятая_очередь_это_Busy_и_в_ComfyUI_ничего_не_уходит()
    {
        _comfy.Running.Add("чужой-прогон");
        var result = await Build().FacePoseAsync(Photos(1), CancellationToken.None);

        result.Busy.Should().BeTrue();
        result.Poses.Should().BeNull();
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Недоступный_ComfyUI_это_Busy_а_не_исключение()
    {
        _comfy.Down = true;
        var result = await Build().FacePoseAsync(Photos(1), CancellationToken.None);

        result.Busy.Should().BeTrue();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Выключенные_локальные_модели_это_ошибка_без_Busy()
    {
        var result = await Build(enabled: false).FacePoseAsync(Photos(1), CancellationToken.None);

        result.Busy.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        _comfy.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Потолок_ожидания_это_Busy()
    {
        var adapter = Build();
        adapter.FacePoseTimeoutOverride = TimeSpan.FromMilliseconds(200);
        // История не появится: задача «висит»
        var result = await adapter.FacePoseAsync(Photos(1), CancellationToken.None);

        result.Busy.Should().BeTrue();
    }

    [Fact]
    public async Task Не_картинка_отвергается_до_загрузки()
    {
        var result = await Build().FacePoseAsync([[1, 2, 3]], CancellationToken.None);

        result.Error.Should().NotBeNullOrEmpty();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Ошибка_графа_в_ComfyUI_возвращается_ошибкой()
    {
        _comfy.History["prompt-1"] = new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = "error", ["completed"] = false, ["messages"] = new JsonArray() },
            ["outputs"] = new JsonObject(),
        };
        var result = await Build().FacePoseAsync(Photos(1), CancellationToken.None);

        result.Poses.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
        result.Busy.Should().BeFalse();
    }
}
