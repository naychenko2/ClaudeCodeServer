using System.Diagnostics;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Окно TOCTOU (ревью 4.5, CRITICAL #1): путь проверен, а до открытия файл или каталог
/// подменили ссылкой наружу. Подмена делается тестовым швом ровно между проверкой и
/// операцией; решает сверка открытого дескриптора в FileService — ни байта наружу на
/// чтении, ни байта вне корня на записи.
/// </summary>
public sealed class AgentToctouTests : IDisposable
{
    private readonly AgentSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private Project Project => new() { Id = "p1", RootPath = _box.Project };
    private string Secret => Path.Combine(_box.Outside, "secret.txt");
    private string OutsideDir => Path.Combine(_box.Outside, "dir");

    // Шов срабатывает один раз: подмена после проверки, дальше всё как есть
    private AgentProjectFiles FilesSwapping(string relative, string target, bool directory)
    {
        var files = new AgentProjectFiles(new FileService(), _box.Policy());
        var swapped = false;
        files.AfterCheck = _ =>
        {
            if (swapped) return;
            swapped = true;
            var path = Path.Combine(_box.Project, relative);
            if (directory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            _box.Link(path, target, directory);
        };
        return files;
    }

    [SkippableFact]
    public async Task ФайлПодменёнСсылкойНаружу_ЧтениеВсехВидов_Отказ()
    {
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.ReadFileAsync(Project, "a.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.ReadFileBytesAsync(Project, "a.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.GetContentAsync(Project, "a.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.OpenReadAsync(Project, "a.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [SkippableFact]
    public async Task ФайлПодменёнСсылкойНаружу_Запись_Отказ_ЧужойФайлЦел()
    {
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.WriteFileAsync(Project, "a.txt", "затёрто"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await FilesSwapping("a.txt", Secret, false).Invoking(f => f.WriteFileBytesAsync(Project, "a.txt", [1, 2, 3]))
            .Should().ThrowAsync<AgentPathRefusedException>();

        // Не усечён при открытии и не дописан
        File.ReadAllText(Secret).Should().Be("секрет");
    }

    [SkippableFact]
    public async Task КаталогПодменёнСсылкойНаружу_СозданиеПереименованиеУдаление_Отказ()
    {
        Directory.CreateDirectory(Path.Combine(_box.Project, "d"));
        Task Create(AgentProjectFiles f) => f.CreateFileAsync(Project, "d/new.txt", "x");
        Task Mkdir(AgentProjectFiles f) => f.CreateDirectoryAsync(Project, "d/sub");
        Task Delete(AgentProjectFiles f) => f.DeleteAsync(Project, "d/inner.txt");
        Task Rename(AgentProjectFiles f) => f.RenameAsync(Project, "a.txt", "d/moved.txt");

        foreach (var op in new Func<AgentProjectFiles, Task>[] { Create, Mkdir, Delete, Rename })
        {
            var files = FilesSwapping("d", OutsideDir, true);
            await files.Invoking(op).Should().ThrowAsync<AgentPathRefusedException>();
            var link = Path.Combine(_box.Project, "d");
            if (OperatingSystem.IsWindows()) Directory.Delete(link);
            else File.Delete(link);
            Directory.CreateDirectory(Path.Combine(_box.Project, "d"));
        }

        Directory.GetFileSystemEntries(OutsideDir).Select(Path.GetFileName).Should().Equal("inner.txt");
        File.ReadAllText(Path.Combine(OutsideDir, "inner.txt")).Should().Be("внутри чужого");
        File.ReadAllText(Path.Combine(_box.Project, "a.txt")).Should().Be("привет");
    }

    [Fact]
    public async Task БезПодмены_ОперацииИдутКакПрежде()
    {
        var files = new AgentProjectFiles(new FileService(), _box.Policy());

        await files.WriteFileAsync(Project, "a.txt", "новое");
        await files.CreateFileAsync(Project, "d/e/new.txt", "x");
        await files.RenameAsync(Project, "d/e/new.txt", "d/moved.txt");

        (await files.ReadFileAsync(Project, "a.txt")).Should().Be("новое");
        (await files.ReadFileAsync(Project, "d/moved.txt")).Should().Be("x");
        await using (var stream = (await files.OpenReadAsync(Project, "a.txt")).Content)
            stream.Length.Should().Be(10);
        await files.DeleteAsync(Project, "d");
        Directory.Exists(Path.Combine(_box.Project, "d")).Should().BeFalse();
    }

    /// <summary>
    /// Жёсткая ссылка — второе имя того же файла, «снаружи» у неё нет: открытый дескриптор
    /// честно указывает внутрь корня. Поведение зафиксировано явно — чтение ИДЁТ. Защита не
    /// здесь, а в «roots add»: корень, куда пишут другие пользователи машины, не принимается.
    /// </summary>
    [SkippableFact]
    public async Task ЖёсткаяСсылкаНаФайлВнеКорня_НеОтличимаОтФайла_Читается()
    {
        var link = Path.Combine(_box.Project, "hard.txt");
        var created = OperatingSystem.IsWindows()
            ? Run("cmd", "/c", "mklink", "/H", link, Secret)
            : Run("ln", Secret, link);
        Skip.IfNot(created, "Жёсткую ссылку здесь создать нельзя (другой том или нет прав)");

        var files = new AgentProjectFiles(new FileService(), _box.Policy());

        (await files.ReadFileAsync(Project, "hard.txt")).Should().Be("секрет");
    }

    private static bool Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
