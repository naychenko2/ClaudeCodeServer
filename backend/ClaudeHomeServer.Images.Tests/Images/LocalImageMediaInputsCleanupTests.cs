using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Фото, загруженные адаптером редактора в input ComfyUI (лица людей), попадают в стор задач
// и убираются общей чисткой LocalMediaCleanup — своего чистильщика у адаптера нет
public class LocalImageMediaInputsCleanupTests : IDisposable
{
    private readonly FakeComfy _comfy = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "local_inputs_cleanup_" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _output;
    private readonly LocalMediaJobStore _store;
    private readonly LocalMediaCleanup _cleanup;
    private readonly LocalImageMediaAdapter _adapter;

    public LocalImageMediaInputsCleanupTests()
    {
        _input = Directory.CreateDirectory(Path.Combine(_tempDir, "comfy", "input")).FullName;
        _output = Directory.CreateDirectory(Path.Combine(_tempDir, "comfy", "output")).FullName;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        _store = new LocalMediaJobStore(config);
        _cleanup = new LocalMediaCleanup(_store, NullLogger<LocalMediaCleanup>.Instance);
        _adapter = new LocalImageMediaAdapter(new ComfyClient(new FakeComfyFactory(_comfy), config), config,
            NullLogger<LocalImageMediaAdapter>.Instance, store: _store);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private LocalMediaOptions Options() => new() { JobRetentionDays = 7, ComfyInputDir = _input, ComfyOutputDir = _output };

    // Файлы лежат в input так же, как их оставил бы ComfyUI после загрузки
    private List<string> TouchUploads()
    {
        var files = new List<string>();
        foreach (var path in _comfy.UploadPaths)
        {
            var full = Path.Combine(_input, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "лицо");
            files.Add(full);
        }
        return files;
    }

    private static byte[][] Photos(int n) => [.. Enumerable.Range(0, n).Select(_ => LocalMediaTestImages.Png(8, 8))];

    [Fact]
    public async Task Ракурс_лица_входы_попадают_в_стор_и_чистка_их_убирает()
    {
        _comfy.History["prompt-1"] = new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true, ["messages"] = new JsonArray() },
            ["outputs"] = new JsonObject { ["pose"] = new JsonObject { ["text"] = new JsonArray("ccs_face_pose=error") } },
        };

        await _adapter.FacePoseAsync(Photos(2), CancellationToken.None);

        var job = _store.ListByOp(LocalMediaOps.EditorInputs).Should().ContainSingle().Subject;
        job.ComfyInputs.Should().Equal(_comfy.UploadPaths);
        var files = TouchUploads();

        _cleanup.Run(Options(), DateTime.UtcNow);

        files.Should().OnlyContain(f => !File.Exists(f));
    }

    [Fact]
    public async Task Ракурс_лица_сбой_на_второй_загрузке_первое_фото_тоже_убирается()
    {
        _comfy.FailUploadsAfter = 1;

        var result = await _adapter.FacePoseAsync(Photos(2), CancellationToken.None);

        result.Error.Should().NotBeNullOrEmpty();
        _comfy.UploadPaths.Should().ContainSingle();
        var files = TouchUploads();
        _cleanup.Run(Options(), DateTime.UtcNow);
        files.Should().OnlyContain(f => !File.Exists(f));
    }

    [Fact]
    public async Task Правка_входы_ждут_очереди_а_потом_убираются_чисткой()
    {
        var submitted = await _adapter.SubmitAsync(
            new LocalImageRequest(LocalImageOp.Edit, "убери провод", Photos(1), Aspect: null, Count: 1,
                FaceReferences: Photos(1)), default);

        submitted.Error.Should().BeNull();
        var job = _store.ListByOp(LocalMediaOps.EditorInputs).Should().ContainSingle().Subject;
        job.ComfyInputs.Should().Equal(_comfy.UploadPaths).And.HaveCount(2);
        var files = TouchUploads();

        _cleanup.Run(Options(), DateTime.UtcNow);
        files.Should().OnlyContain(f => File.Exists(f), "задача могла ещё стоять в очереди ComfyUI");

        _cleanup.Run(Options(), DateTime.UtcNow.AddHours(3));
        files.Should().OnlyContain(f => !File.Exists(f));
    }
}
