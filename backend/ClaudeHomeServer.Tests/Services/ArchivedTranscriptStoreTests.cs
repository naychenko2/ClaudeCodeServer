using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 0 плана «Архив чатов» v4: копия транскрипта в data/archived-transcripts при
// архивации, возврат по путям, резолвленным на момент возврата, уборка копии при
// удалении чата. Все критерии — из §6 плана (юниты блока «копия транскрипта»).
public class ArchivedTranscriptStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _root;
    private readonly string _profileA;
    private readonly string _profileB;

    private const string Csid = "17d2c868-5897-4c32-9019-cf5c6912fc9b";
    private const string Cwd = @"C:\Projects\my-app";
    // За время в архиве проект переехал в worktree — cwd на момент возврата другой
    private const string NewCwd = @"C:\Homes\admin\.worktrees\my-app\wt-feature";

    public ArchivedTranscriptStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "archived_transcripts_tests_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "archived-transcripts");
        _profileA = Path.Combine(_tempDir, "profile-a");
        _profileB = Path.Combine(_tempDir, "profile-b");
        Directory.CreateDirectory(_profileA);
        Directory.CreateDirectory(_profileB);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private ArchivedTranscriptStore NewStore() => new(_root);

    private string SeedTranscript(string root, string content = "{\"type\":\"user\"}", string? flat = null)
    {
        var dir = Path.Combine(root, "projects", flat ?? TranscriptMigrator.FlattenCwd(Cwd));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Csid + ".jsonl");
        File.WriteAllText(file, content);
        return file;
    }

    private string SeedSubagents(string transcriptFile, string fileName = "agent-1.jsonl")
    {
        var dir = Path.Combine(Path.GetDirectoryName(transcriptFile)!, Csid, "subagents");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, fileName);
        File.WriteAllText(file, "{}");
        return file;
    }

    // --- Archive: валидация ключа и гейты ---

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"sub\dir\id")]
    [InlineData("")]
    [InlineData("   ")]
    public void Archive_КлючВнеБелогоСписка_ФайлаНет(string badId)
    {
        SeedTranscript(_profileA);
        var store = NewStore();

        store.Archive(badId, desktopChat: false, [_profileA], Cwd).Should().BeFalse();

        Directory.Exists(_root).Should().BeFalse();
        // Не задет и файл за пределами архива — ключ мог увести путь наружу
        var evil = Path.Combine(_tempDir, "evil.jsonl");
        File.Exists(evil).Should().BeFalse();
    }

    [Fact]
    public void Archive_ДесктопныйЧат_КопиюНеДелает()
    {
        // Гейт по DesktopChat, а не по размеру: в jsonl десктопных чатов — кадры
        // рабочего стола, наружу (бэкап едет в облако) их не отдаём
        SeedTranscript(_profileA);
        var store = NewStore();

        store.Archive(Csid, desktopChat: true, [_profileA], Cwd).Should().BeFalse();

        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public void Archive_ТранскриптаНет_FalseБезОшибки()
    {
        // Ходов не было либо CLI уже вычистил — чат архивируется, карточка честно
        // предупредит про устаревший контекст
        NewStore().Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeFalse();
    }

    // --- Archive: копирование ---

    [Fact]
    public void Archive_КопируетТранскриптВПлоскийАрхив()
    {
        SeedTranscript(_profileA, content: "line1\nline2");

        NewStore().Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        var copy = Path.Combine(_root, Csid + ".jsonl");
        File.Exists(copy).Should().BeTrue();
        File.ReadAllText(copy).Should().Be("line1\nline2");
    }

    [Fact]
    public void Archive_КопируетПапкуСабагентов()
    {
        var seeded = SeedTranscript(_profileA);
        SeedSubagents(seeded);

        NewStore().Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        File.Exists(Path.Combine(_root, Csid, "subagents", "agent-1.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void Archive_НесколькоКопий_БерётСамуюПолную()
    {
        // Копии остаются после смены провайдера и переездов cwd — архивируем самую
        // длинную, а не первую найденную
        SeedTranscript(_profileA, content: "short");
        SeedTranscript(_profileB, content: "much longer transcript with the whole conversation");

        NewStore().Archive(Csid, desktopChat: false, [_profileA, _profileB], Cwd).Should().BeTrue();

        File.ReadAllText(Path.Combine(_root, Csid + ".jsonl"))
            .Should().Be("much longer transcript with the whole conversation");
    }

    [Fact]
    public void Archive_БезCwd_НаходитСканом()
    {
        // Рабочую папку определить не удалось (проект удалён) — фолбэк-скан профилей
        var seeded = SeedTranscript(_profileA);

        NewStore().Archive(Csid, desktopChat: false, [_profileA], cwd: null).Should().BeTrue();

        File.Exists(Path.Combine(_root, Csid + ".jsonl")).Should().BeTrue();
        File.Exists(seeded).Should().BeTrue(); // источник не трогаем — это копия, не переезд
    }

    [Fact]
    public void Archive_ВышеПорогаРазмера_КопиюНеДелает()
    {
        // Порог — защита места на диске, отдельная от десктопного гейта причина
        var seeded = SeedTranscript(_profileA, content: "x".PadRight(1000, 'x'));
        var store = NewStore();
        store.MaxCopyBytes = 10;

        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeFalse();

        Directory.Exists(_root).Should().BeFalse();
        File.Exists(seeded).Should().BeTrue();
    }

    [Fact]
    public void Archive_БолееПолнаяКопияУжеВАрхиве_НеЗатираетсяУсечённым()
    {
        // Возврат, новые ходы, повторная архивация — источник длиннее и перезаписывается;
        // а вот усечённый (повреждённый/пустой профиль) полную копию не затирает
        SeedTranscript(_profileA, content: "full history line1\nline2\nline3");
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        SeedTranscript(_profileA, content: "short"); // профиль перезаписали короче
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        File.ReadAllText(Path.Combine(_root, Csid + ".jsonl")).Should().Be("full history line1\nline2\nline3");
    }

    [Fact]
    public void Archive_ПовторнаПослеНовыхХодов_ПерезаписываетКопию()
    {
        SeedTranscript(_profileA, content: "v1");
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        SeedTranscript(_profileA, content: "v1\nv2-new-turn");
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        File.ReadAllText(Path.Combine(_root, Csid + ".jsonl")).Should().Be("v1\nv2-new-turn");
    }

    // --- Restore: резолв цели на момент возврата ---

    [Fact]
    public void Restore_ЦельРезолвитсяНаМоментВозвратаПрофильИCwdСменились()
    {
        // Ключевой инвариант шага 0: архивировали из профиля A со старым cwd, а вернули
        // в профиль B с новым cwd (смена провайдера + worktree за время в архиве) —
        // путь назначения считается от аргументов ВОЗВРАТА, исходный не запоминается
        var seeded = SeedTranscript(_profileA, content: "line1\nline2");
        SeedSubagents(seeded);
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();
        // Симуляция ретенции CLI: исходный транскрипт вычищен
        File.Delete(seeded);

        store.Restore(Csid, _profileB, NewCwd).Should().BeTrue();

        var dstFile = Path.Combine(_profileB, "projects", TranscriptMigrator.FlattenCwd(NewCwd), Csid + ".jsonl");
        File.Exists(dstFile).Should().BeTrue();
        File.ReadAllText(dstFile).Should().Be("line1\nline2");
        // Папка сабагентов возвращается вместе с транскриптом
        File.Exists(Path.Combine(Path.GetDirectoryName(dstFile)!, Csid, "subagents", "agent-1.jsonl"))
            .Should().BeTrue();
        // Копия остаётся в архиве — повторная архивация дешевле, срыв ФС не лишает чат страховки
        File.Exists(Path.Combine(_root, Csid + ".jsonl")).Should().BeTrue();
    }

    [Fact]
    public void Restore_ТотЖеПрофильИCwd_КладётНаПрежнееМесто()
    {
        SeedTranscript(_profileA, content: "line1");
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();
        File.Delete(Path.Combine(_profileA, "projects", TranscriptMigrator.FlattenCwd(Cwd), Csid + ".jsonl"));

        store.Restore(Csid, _profileA, Cwd).Should().BeTrue();

        File.Exists(Path.Combine(_profileA, "projects", TranscriptMigrator.FlattenCwd(Cwd), Csid + ".jsonl"))
            .Should().BeTrue();
    }

    [Fact]
    public void Restore_КопииНет_False()
    {
        // Чат архивирован до появления стора либо десктопный — штатно, без исключения
        NewStore().Restore(Csid, _profileA, Cwd).Should().BeFalse();
    }

    [Fact]
    public void Restore_ПриёмникНеКорочеАрхива_НеЗатирается()
    {
        // Чат вернули раньше ретенции: CLI сам доработал и его история не короче копии —
        // перезапись не нужна (цель уже достигнута)
        SeedTranscript(_profileA, content: "v1");
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        var dstFile = Path.Combine(_profileA, "projects", TranscriptMigrator.FlattenCwd(Cwd), Csid + ".jsonl");
        File.WriteAllText(dstFile, "v1\nv2-new-turns-after-restore"); // живая история длиннее

        store.Restore(Csid, _profileA, Cwd).Should().BeTrue();

        File.ReadAllText(dstFile).Should().Be("v1\nv2-new-turns-after-restore");
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("sub/dir/id")]
    [InlineData("")]
    public void Restore_КлючВнеБелогоСписка_FalseБезФайла(string badId)
    {
        NewStore().Restore(badId, _profileA, Cwd).Should().BeFalse();
        Directory.Exists(Path.Combine(_profileA, "projects")).Should().BeFalse();
    }

    // --- HasCopy ---

    [Fact]
    public void HasCopy_ОтражаетНаличиеКопии()
    {
        var store = NewStore();
        store.HasCopy(Csid).Should().BeFalse();

        SeedTranscript(_profileA);
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();

        store.HasCopy(Csid).Should().BeTrue();
    }

    // --- Delete: уборка при удалении чата ---

    [Fact]
    public void Delete_УноситКопиюИПапкуСабагентов()
    {
        var seeded = SeedTranscript(_profileA);
        SeedSubagents(seeded);
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();
        store.HasCopy(Csid).Should().BeTrue();

        store.Delete(Csid);

        File.Exists(Path.Combine(_root, Csid + ".jsonl")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, Csid)).Should().BeFalse();
        store.HasCopy(Csid).Should().BeFalse();
    }

    [Fact]
    public void Delete_ЧужаяКопияВАрхивеНеТрогается()
    {
        // Один корень на все чаты: бьём строго по {csid}, соседние копии живут
        SeedTranscript(_profileA);
        var store = NewStore();
        store.Archive(Csid, desktopChat: false, [_profileA], Cwd).Should().BeTrue();
        var foreign = Path.Combine(_root, "e4d1f0aa-0000-4000-8000-000000000001.jsonl");
        File.WriteAllText(foreign, "не трогать");

        store.Delete(Csid);

        File.Exists(foreign).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("sub/dir/id")]
    [InlineData("")]
    public void Delete_КлючВнеБелогоСписка_НичегоНеДелает(string badId)
    {
        var evil = Path.Combine(_tempDir, "evil.jsonl");
        File.WriteAllText(evil, "не трогать");

        NewStore().Delete(badId);

        File.Exists(evil).Should().BeTrue();
    }

    [Fact]
    public void Delete_КопииНет_ТихоБезИсключения()
    {
        var store = NewStore();
        var act = () => store.Delete(Csid);
        act.Should().NotThrow();
    }
}
