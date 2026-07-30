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
    // Рабочая папка после включения отдельного дерева чата (TryRelocateCwd)
    private const string NewCwd = @"C:\Homes\admin\.worktrees\my-app\wt-feature";
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

    // --- Переезд между рабочими папками одного профиля (worktree чата) ---

    [Fact]
    public void TryRelocateCwd_КопируетВПапкуНовогоCwd()
    {
        // Целевая папка считается от НОВОГО cwd (в отличие от TryMigrate, где она
        // намеренно берётся у источника): --resume ищет транскрипт по уплощённому cwd
        SeedTranscript(_src, content: "line1\nline2");

        var ok = TranscriptMigrator.TryRelocateCwd(_src, Cwd, NewCwd, SessionId, out var error);

        ok.Should().BeTrue(error);
        var dstFile = Path.Combine(_src, "projects", TranscriptMigrator.FlattenCwd(NewCwd), SessionId + ".jsonl");
        File.Exists(dstFile).Should().BeTrue();
        File.ReadAllText(dstFile).Should().Be("line1\nline2");
    }

    [Fact]
    public void TryRelocateCwd_ИсходникОстаётся()
    {
        // Обратный переезд (выключение worktree) дешевле, если копия старого cwd на месте
        var seeded = SeedTranscript(_src);

        TranscriptMigrator.TryRelocateCwd(_src, Cwd, NewCwd, SessionId, out _).Should().BeTrue();

        File.Exists(seeded).Should().BeTrue();
    }

    [Fact]
    public void TryRelocateCwd_ПереноситПапкуСабагентов()
    {
        var srcFile = SeedTranscript(_src);
        var subagents = Path.Combine(Path.GetDirectoryName(srcFile)!, SessionId, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-1.jsonl"), "{}");

        TranscriptMigrator.TryRelocateCwd(_src, Cwd, NewCwd, SessionId, out _).Should().BeTrue();

        File.Exists(Path.Combine(_src, "projects", TranscriptMigrator.FlattenCwd(NewCwd),
            SessionId, "subagents", "agent-1.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void TryRelocateCwd_ТранскриптНеНайден_ОшибкаБезИзменений()
    {
        // Вызывающий (SetWorktreeAsync) по false откатывает уже созданное дерево —
        // значит папку нового cwd мы после неудачи оставлять не должны
        var ok = TranscriptMigrator.TryRelocateCwd(_src, Cwd, NewCwd, SessionId, out var error);

        ok.Should().BeFalse();
        error.Should().Contain(SessionId);
        Directory.Exists(Path.Combine(_src, "projects", TranscriptMigrator.FlattenCwd(NewCwd)))
            .Should().BeFalse();
    }

    [Fact]
    public void TryRelocateCwd_КонтейнерныйCwd_УплощаетсяПоСлешам()
    {
        // У container-пользователя CLI видит контейнерный путь (/projects/…), поэтому
        // и переезд считается по нему же — уплощение одинаково для обоих разделителей
        const string containerCwd = "/projects/foo";
        const string containerWt = "/projects/foo/.worktrees/wt-bar";
        SeedTranscript(_src, flat: TranscriptMigrator.FlattenCwd(containerCwd));

        var ok = TranscriptMigrator.TryRelocateCwd(_src, containerCwd, containerWt, SessionId, out var error);

        ok.Should().BeTrue(error);
        TranscriptMigrator.FlattenCwd(containerCwd).Should().Be("-projects-foo");
        File.Exists(Path.Combine(_src, "projects", TranscriptMigrator.FlattenCwd(containerWt),
            SessionId + ".jsonl")).Should().BeTrue();
    }

    // --- Уборка транскрипта при удалении чата ---

    [Fact]
    public void FindAllTranscripts_НеДублируетНайденноеПоСоглашению()
    {
        // Файл лежит там, где его ждет соглашение, и попадает же под фолбэк-скан —
        // в результате он должен быть один раз, иначе счетчик удаленных врал бы
        var seeded = SeedTranscript(_src);

        TranscriptMigrator.FindAllTranscripts([_src], Cwd, SessionId)
            .Should().BeEquivalentTo([seeded]);
    }

    [Fact]
    public void DeleteEverywhere_УдаляетТранскриптПоСоглашению()
    {
        var seeded = SeedTranscript(_src);

        TranscriptMigrator.DeleteEverywhere([_src], Cwd, SessionId).Should().Be(1);

        File.Exists(seeded).Should().BeFalse();
    }

    [Fact]
    public void DeleteEverywhere_УдаляетКопииВоВсехКорнях()
    {
        // Копии остаются после смены провайдера (TryMigrate исходник не удаляет) —
        // уборка обязана пройти по всем профилям, а не только по текущему
        var inSrc = SeedTranscript(_src);
        var inDst = SeedTranscript(_dst);

        TranscriptMigrator.DeleteEverywhere([_src, _dst], Cwd, SessionId).Should().Be(2);

        File.Exists(inSrc).Should().BeFalse();
        File.Exists(inDst).Should().BeFalse();
    }

    [Fact]
    public void DeleteEverywhere_БезCwd_НаходитСканом()
    {
        // Рабочую папку сессии определить не удалось (проект удален) — остается скан
        var seeded = SeedTranscript(_src);

        TranscriptMigrator.DeleteEverywhere([_src], cwd: null, SessionId).Should().Be(1);

        File.Exists(seeded).Should().BeFalse();
    }

    [Fact]
    public void DeleteEverywhere_УноситПапкуСабагентов()
    {
        var seeded = SeedTranscript(_src);
        var sessionDir = Path.Combine(Path.GetDirectoryName(seeded)!, SessionId);
        Directory.CreateDirectory(Path.Combine(sessionDir, "subagents"));
        File.WriteAllText(Path.Combine(sessionDir, "subagents", "agent-1.jsonl"), "{}");

        TranscriptMigrator.DeleteEverywhere([_src], Cwd, SessionId).Should().Be(1);

        Directory.Exists(sessionDir).Should().BeFalse();
    }

    [Fact]
    public void DeleteEverywhere_ЧужиеТранскриптыВТойЖеПапкеНеТрогает()
    {
        // Ключевой инвариант: один ~/.claude делят несколько инстансов сервера и
        // интерактивные сессии пользователя. Бьем строго по {csid}.jsonl, а папку —
        // никогда, иначе уборка снесла бы чужую историю
        var mine = SeedTranscript(_src);
        var dir = Path.GetDirectoryName(mine)!;
        var foreign = Path.Combine(dir, "e4d1f0aa-0000-4000-8000-000000000001.jsonl");
        File.WriteAllText(foreign, "{\"type\":\"user\"}");

        TranscriptMigrator.DeleteEverywhere([_src], Cwd, SessionId).Should().Be(1);

        File.Exists(mine).Should().BeFalse();
        File.Exists(foreign).Should().BeTrue();
        Directory.Exists(dir).Should().BeTrue();
    }

    [Fact]
    public void DeleteEverywhere_БезТранскрипта_НольБезИсключения()
    {
        TranscriptMigrator.DeleteEverywhere([_src, _dst], Cwd, SessionId).Should().Be(0);
    }

    [Fact]
    public void DeleteEverywhere_НесуществующийКорень_НольБезИсключения()
    {
        // Профиль провайдера создается лениво: корня может не быть вовсе
        var missing = Path.Combine(_tempDir, "no-such-profile");

        TranscriptMigrator.DeleteEverywhere([missing], Cwd, SessionId).Should().Be(0);
    }

    [Fact]
    public void DeleteEverywhere_ПовторКорня_НеЗавышаетСчетчик()
    {
        // Корни в списке способны повторяться (профиль подписки и ~/.claude указывают на
        // одну папку). File.Delete на уже удаленном файле молчит, и счетчик врал бы в логе
        SeedTranscript(_src);

        TranscriptMigrator.DeleteEverywhere([_src, _src], Cwd, SessionId).Should().Be(1);
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"sub\dir\id")]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteEverywhere_КлючНеИмяФайла_НичегоНеДелает(string badId)
    {
        // sessions.json правится и руками — путь в ключе увел бы удаление за пределы профиля
        var victim = Path.Combine(_tempDir, "evil.jsonl");
        File.WriteAllText(victim, "не трогать");

        TranscriptMigrator.DeleteEverywhere([_src], Cwd, badId).Should().Be(0);
        TranscriptMigrator.FindAllTranscripts([_src], Cwd, badId).Should().BeEmpty();

        File.Exists(victim).Should().BeTrue();
    }
}
