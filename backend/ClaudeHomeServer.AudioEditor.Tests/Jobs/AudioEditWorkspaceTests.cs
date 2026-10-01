using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Рабочая папка задач: кеш на 7 дней вне бэкапа; задачи, чьи файлы держат версии нитей, не чистятся
public sealed class AudioEditWorkspaceTests : IDisposable
{
    private const string Owner = "owner-1";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "audio-workspace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public void Рабочая_папка_отдельный_корень_от_нитей_и_префов()
    {
        // Бэкап в Main исключает рабочую папку по константе Core: модуль обязан класть файлы туда же
        AudioEditWorkspace.DirName.Should().Be(AudioEditorPaths.WorkspaceDirName);
        new[] { AudioThreadStore.DirName, AudioPrefsStore.DirName }.Should().NotContain(AudioEditWorkspace.DirName);
    }

    [Fact]
    public void Чистка_сносит_задачи_старше_недели_кроме_свежих_и_удержанных()
    {
        var workspace = new AudioEditWorkspace(Path.Combine(_data, AudioEditWorkspace.DirName))
        {
            RetainedJobs = owner => owner == Owner ? new HashSet<string> { "kept" } : new HashSet<string>(),
        };
        var now = DateTime.UtcNow;
        var old = Job(workspace, "old", now - TimeSpan.FromDays(8));
        var fresh = Job(workspace, "fresh", now - TimeSpan.FromDays(6));
        var kept = Job(workspace, "kept", now - TimeSpan.FromDays(30));

        workspace.Sweep(now);

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(fresh).Should().BeTrue();
        Directory.Exists(kept).Should().BeTrue("файлы версии живой нити живут столько же, сколько нить");
    }

    [Fact]
    public void Задачи_версий_нитей_владельца_удерживаются()
    {
        var threads = new AudioThreadStore(Path.Combine(_data, AudioThreadStore.DirName));
        var thread = threads.Open(Owner, "chat-1", "voice/a.mp3", null, null).Thread!;
        threads.AddLaunch(Owner, "chat-1", thread.Id,
            new AudioThreadLaunch("job-1", AudioThreadVersion.OriginId, DateTime.UtcNow, AudioThreadLaunchStatus.Running, "human", null, null));
        threads.FinishLaunch(Owner, "chat-1", thread.Id, "job-1", AudioThreadLaunchStatus.Done,
            [(1, [new AudioVersionFile(AudioFileRoles.Main, "audio-editor/owner-1/job-1/v1.mp3")])]);

        threads.ReferencedJobs(Owner).Should().BeEquivalentTo(["job-1"]);
        threads.ReferencedJobs("owner-2").Should().BeEmpty();
    }

    private static string Job(AudioEditWorkspace workspace, string jobId, DateTime writtenAt)
    {
        var dir = workspace.JobDir(Owner, jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "v1.mp3"), "x");
        Directory.SetLastWriteTimeUtc(dir, writtenAt);
        return dir;
    }
}
