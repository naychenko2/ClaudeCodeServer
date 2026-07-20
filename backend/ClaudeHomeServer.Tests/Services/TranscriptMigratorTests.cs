using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class TranscriptMigratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _src;
    private readonly string _dst;

    public TranscriptMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "migrator_tests_" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_tempDir, "src-profile");
        _dst = Path.Combine(_tempDir, "dst-profile");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private const string Cwd = @"C:\Projects\my-app";
    private const string SessionId = "abc-123";

    private string SeedTranscript(string root, string? flat = null, string content = "{\"type\":\"user\"}")
    {
        var dir = Path.Combine(root, "projects", flat ?? TranscriptMigrator.FlattenCwd(Cwd));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SessionId + ".jsonl");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void FlattenCwd_ЗаменяетНеАлфавитноЦифровыеНаДефис()
    {
        TranscriptMigrator.FlattenCwd(@"C:\Projects\my-app").Should().Be("C--Projects-my-app");
    }

    [Fact]
    public void FindTranscript_ПоСоглашениюОбУплощении()
    {
        var seeded = SeedTranscript(_src);
        TranscriptMigrator.FindTranscript(_src, Cwd, SessionId).Should().Be(seeded);
    }

    [Fact]
    public void FindTranscript_ФолбэкСканПоЧужойПапке()
    {
        // Раскладка не по соглашению (другая версия CLI) — транскрипт всё равно находится
        var seeded = SeedTranscript(_src, flat: "some-legacy-layout");
        TranscriptMigrator.FindTranscript(_src, Cwd, SessionId).Should().Be(seeded);
    }

    [Fact]
    public void FindTranscript_НетПапкиProjects_Null()
    {
        TranscriptMigrator.FindTranscript(_src, Cwd, SessionId).Should().BeNull();
    }

    [Fact]
    public void TryMigrate_КопируетТранскриптВЦелевойПрофиль()
    {
        SeedTranscript(_src, content: "line1\nline2");

        var ok = TranscriptMigrator.TryMigrate(_src, _dst, Cwd, SessionId, out var error);

        ok.Should().BeTrue(error);
        var dstFile = Path.Combine(_dst, "projects", TranscriptMigrator.FlattenCwd(Cwd), SessionId + ".jsonl");
        File.Exists(dstFile).Should().BeTrue();
        File.ReadAllText(dstFile).Should().Be("line1\nline2");
    }

    [Fact]
    public void TryMigrate_ПереноситПапкуСабагентов()
    {
        var srcFile = SeedTranscript(_src);
        var subagents = Path.Combine(Path.GetDirectoryName(srcFile)!, SessionId, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-1.jsonl"), "{}");

        TranscriptMigrator.TryMigrate(_src, _dst, Cwd, SessionId, out _).Should().BeTrue();

        File.Exists(Path.Combine(_dst, "projects", TranscriptMigrator.FlattenCwd(Cwd),
            SessionId, "subagents", "agent-1.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void TryMigrate_БезТранскрипта_FalseСПричиной()
    {
        var ok = TranscriptMigrator.TryMigrate(_src, _dst, Cwd, SessionId, out var error);

        ok.Should().BeFalse();
        error.Should().Contain(SessionId);
    }

    [Fact]
    public void TryMigrate_ПовторнаяМиграция_ПерезаписываетЦель()
    {
        // Туда-обратно (фейловер, потом возврат): копия не должна падать на существующем файле
        SeedTranscript(_src, content: "v2");
        SeedTranscript(_dst, content: "v1");

        var ok = TranscriptMigrator.TryMigrate(_src, _dst, Cwd, SessionId, out var error);

        ok.Should().BeTrue(error);
        File.ReadAllText(Path.Combine(_dst, "projects", TranscriptMigrator.FlattenCwd(Cwd), SessionId + ".jsonl"))
            .Should().Be("v2");
    }

    [Fact]
    public void TryMigrate_ФолбэкРаскладка_СохраняетИмяПапкиИсточника()
    {
        // Найдено фолбэк-сканом → копируем под тем же именем папки: у CLI этой версии
        // своё соглашение об уплощении, пересчитанное имя он мог бы не найти
        SeedTranscript(_src, flat: "legacy-layout");

        TranscriptMigrator.TryMigrate(_src, _dst, Cwd, SessionId, out _).Should().BeTrue();

        File.Exists(Path.Combine(_dst, "projects", "legacy-layout", SessionId + ".jsonl")).Should().BeTrue();
    }
}
