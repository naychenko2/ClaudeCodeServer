using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Источник результата для истории (ADR-030 §3): локальная генерация. Тесты — на фейковом
// TimeProvider и в temp-каталоге: 410 project_gone / local_project, успех (файл + content-type
// по сигнатуре), expired (файл старше 7 дней), чужой n, запись без job_id
public class LocalMediaHistorySourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "lmhist_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    // Фейк шва «папка проекта»: возвращает заданный probe (состояние + корень)
    private sealed class ProbeAccess(MediaHistoryProjectProbe? probe) : ILocalMediaProjectAccess
    {
        public string? ResolveRoot(string ownerId, string projectId) => probe?.RootPath;
        public void NotifyWritten(string root, string relativePath) { }
        public MediaHistoryProjectProbe? ProbeProject(string ownerId, string projectId) => probe;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static MediaHistoryRef Ref() => new("p1", "owner-1", "proj-1", "job-1", "chat-1");

    private static LocalMediaJob JobWithOneOutput() => new()
    {
        Id = "job-1",
        OwnerId = "owner-1",
        ProjectId = "proj-1",
        Op = LocalMediaOps.GenerateImage,
        PromptId = "p1",
        Status = LocalMediaStatuses.Completed,
        Outputs = [new LocalMediaOutput { Path = "result.png" }],
    };

    private LocalMediaJobStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        return new LocalMediaJobStore(config, NullLogger<LocalMediaJobStore>.Instance);
    }

    // 410 project_gone: проект удалён (probe Gone)
    [Fact]
    public async Task ProjectGone_410()
    {
        var store = NewStore();
        store.Add(JobWithOneOutput());
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.Gone, null)), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.ProjectGone);
    }

    // 410 local_project: проект переехал на устройство (probe Local)
    [Fact]
    public async Task LocalProject_410()
    {
        var store = NewStore();
        store.Add(JobWithOneOutput());
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.Local, null)), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.LocalProject);
    }

    // Успех: файл на сервере, content-type по сигнатуре (PNG), variantCount=1
    [Fact]
    public async Task Успех_FайлИContentType()
    {
        var root = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "result.png");
        File.WriteAllBytes(file, LocalMediaTestImages.Png(64, 64));

        var store = NewStore();
        store.Add(JobWithOneOutput());
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.OnServer, root)), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().BeNull();
        res.HostPath.Should().Be(file);
        res.ContentType.Should().Be("image/png");
        res.VariantCount.Should().Be(1);
    }

    // expired: файл старше 7 дней (last-write 8 дней назад, часы ManualTime)
    [Fact]
    public async Task Expired_410()
    {
        var root = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "result.png");
        File.WriteAllBytes(file, LocalMediaTestImages.Png(64, 64));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));

        var store = NewStore();
        store.Add(JobWithOneOutput());
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.OnServer, root)), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Expired);
    }

    // Чужой n (n >= variantCount) — missing
    [Fact]
    public async Task ЧужойN_Missing()
    {
        var store = NewStore();
        store.Add(JobWithOneOutput());
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.OnServer, "/tmp/x")), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 5, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }

    // Запись без job_id (старые прогоны) — missing
    [Fact]
    public async Task БезJobId_Missing()
    {
        var store = NewStore();
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.OnServer, "/tmp/x")), new ManualTime());
        var res = await src.ResolveAsync(new MediaHistoryRef("p1", "owner-1", "proj-1", null, "chat-1"), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }

    // Обход пути через Outputs[].Path: «../secret.png» уходит за корень проекта. SafePath.Join
    // бросает UnauthorizedAccessException → 410 missing. Мутация «убрать SafePath» (Path.Combine)
    // найдёт приманку secret.png (PNG) и отдаст её — тест покраснеет
    [Fact]
    public async Task ОбходПутиЧерезOutputs_Missing()
    {
        var root = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(root);
        // Приманка за корнем: без SafePath.Join путь «../secret.png» её нашёл бы и отдал
        File.WriteAllBytes(Path.Combine(_tempDir, "secret.png"), LocalMediaTestImages.Png(64, 64));

        var store = NewStore();
        var job = JobWithOneOutput();
        job.Outputs = [new LocalMediaOutput { Path = "../secret.png" }];
        store.Add(job);
        var src = new LocalMediaHistorySource(store,
            new ProbeAccess(new MediaHistoryProjectProbe(MediaHistoryProjectState.OnServer, root)), new ManualTime());
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }
}
