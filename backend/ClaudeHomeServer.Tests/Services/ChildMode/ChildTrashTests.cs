using System.Text.Json;
using ClaudeHomeServer.Services.ChildMode;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Б10c (ADR-041 ред. 5, решение 54): корзина «.ccs-trash/» папки предмета — прежняя версия
/// файла перед Write/Edit репетитора и удалением ребёнка рельсой; хранение 30 дней.
/// Конечное состояние проверяется после полного цикла: снимок → перезапись → восстановление →
/// список, а не сразу после вызова.
///
/// Мутации (что краснеет без соответствующей правки в ChildTrash.cs):
/// (а) «Restore без снимка файла на месте» — Restore_WithNewFileOnPlace_NewItselfGoesToTrash_NothingIsLost
///     (второй записи нет, содержимое нового файла теряется);
/// (б) «Sweep не при снимке» — SweepAtSnapshot_RemovesExpired_MutationB (просроченная запись
///     пережила следующий снимок);
/// (в) «Restore не читает мету, копия наугад по id» — Restore_RestoresOriginalPath_NotTheEntryName_MutationC
///     (цель не та);
/// (г) «разрешать Write при сбое снимка» — краснеет в ChildPermissionDecisionTests
///     (СбойСнимка_WriteСуществующегоФайла_ОтклонёнБезИсключения, Llm.Tests);
/// (д) «Sweep чистит ровно 30 дней включительно» (now − at &lt; Retention) и «чистит и свежие» —
///     краснеют в Sweep_Boundary_ExactlyRetention_IsKept_JustOver_IsRemoved;
/// (е) «Sweep ловит только IOException» — Sweep_UnremovableExpiredDir_SnapshotStillSucceeds_NoThrow
///     (UAF от Directory.Delete вылетает через Snapshot) и
///     Sweep_TrashNotListable_Zero_NoThrow_AndSnapshotStillSucceeds (UAF от GetDirectories);
///     оба — SkippableFact: под root (права его не связывают) случай не проходит молча;
/// (ж) «Restore без actor» — Restore_NestedSnapshotCarriesActorOfOriginalEntry;
///     «исключение вложенного снимка наружу» — Restore_NestedSnapshotFailure_ReturnsFalse_NoThrow.
///
/// Контракт (зелёный без правки, вне списка мутаций): Restore_BadEntryId_False_NoThrow —
/// старший код отклонял те входы побочно: «..» — catch SafePath.Join (был уже в f674fdd25),
/// остальные — отсутствием меты; тест держит контракт формы id, а не проверяет явную проверку.
/// </summary>
public class ChildTrashTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public ChildTrashTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "ccs-trash-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_home, "Предметы", "subj-1");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* уже удалён */ }
    }

    private string WriteOriginal(string rel, byte[] content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private string TrashDirOf(string entryId) => Path.Combine(_root, ChildTrash.TrashDir, entryId);

    /// <summary>Содержимое снимка записи (путь внутри записи — тот же относительный).</summary>
    private byte[] StoredBytes(string entryId, string rel) =>
        File.ReadAllBytes(Path.Combine(TrashDirOf(entryId), rel));

    /// <summary>Запись «как от лежалых времён»: каталог с меткой 40 дней назад, meta.json и файл.</summary>
    private string PlantOldEntry(string rel, byte[] content, DateTimeOffset at)
    {
        var id = at.ToUniversalTime().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var dir = TrashDirOf(id);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!);
        File.WriteAllBytes(Path.Combine(dir, rel), content);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
        {
            Path = rel,
            At = at,
            Actor = "kid-1",
        }));
        return id;
    }

    // --- основной цикл: снимок → метa → восстановление 1:1 ---

    [Fact]
    public void Snapshot_KeepsOriginalBytes_AndMetaNamesPathTimeActor()
    {
        var content = Bytes("первая версия — конспект урока\n");
        var path = WriteOriginal("Заметки/конспект.md", content);

        var id = ChildTrash.Snapshot(_root, "Заметки/конспект.md", "kid-1");

        using var scope = new AssertionScope();
        id.Should().NotBeNull("существующий файл снимается");
        id.Should().MatchRegex(@"^\d{17}-[0-9a-f]{8}$", "id — {yyyyMMddHHmmssfff}-{guid8}");
        StoredBytes(id!, "Заметки/конспект.md").Should().Equal(content, "снимок — байты исходника");
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(TrashDirOf(id!), "meta.json"))).RootElement;
        meta.GetProperty("Path").GetString().Should().Be("Заметки/конспект.md", "мета называет исходный путь");
        meta.GetProperty("Actor").GetString().Should().Be("kid-1", "мета называет, кто");
        DateTimeOffset.TryParse(meta.GetProperty("At").GetString(), out var at).Should().BeTrue();
        (DateTimeOffset.UtcNow - at).Should().BeLessThan(TimeSpan.FromMinutes(5), "мета называет время снимка");
        File.ReadAllBytes(path).Should().Equal(content, "оригинал снимок не трогает");
    }

    [Fact]
    public void Snapshot_OfNewFile_NoRecord_NothingToProtect()
    {
        var id = ChildTrash.Snapshot(_root, "Заметки/нового-нет.md", "kid-1");

        using var scope = new AssertionScope();
        id.Should().BeNull("нового файла защищать нечем");
        ChildTrash.List(_root).Should().BeEmpty();
    }

    [Fact]
    public void SnapshotThenRestore_ReturnsOriginalBytesOneToOne()
    {
        var original = Bytes("первая версия\n");
        var path = WriteOriginal("конспект.md", original);
        var id = ChildTrash.Snapshot(_root, "конспект.md", "kid-1");
        id.Should().NotBeNull();

        File.WriteAllBytes(path, Bytes("затёрто репетитором"));

        ChildTrash.Restore(_root, id!).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(original, "восстановление — байты исходника 1:1");
    }

    [Fact]
    public void Restore_WithNewFileOnPlace_NewItselfGoesToTrash_NothingIsLost()
    {
        var v1 = Bytes("первая версия\n");
        var v2 = Bytes("затёрто репетитором\n");
        var path = WriteOriginal("Заметки/конспект.md", v1);
        var id1 = ChildTrash.Snapshot(_root, "Заметки/конспект.md", "kid-1");
        id1.Should().NotBeNull();

        File.WriteAllBytes(path, v2);

        // Мутация (а): «Restore без снимка существующего файла» — ниже краснеет:
        // второй записи нет, v2 теряется безвозвратно.
        ChildTrash.Restore(_root, id1!).Should().BeTrue();

        using var scope = new AssertionScope();
        File.ReadAllBytes(path).Should().Equal(v1, "на месте — восстановленная прежняя версия");
        var entries = ChildTrash.List(_root);
        entries.Should().HaveCount(2, "снимок первой версии + снимок затёртой — ничего не потеряно");
        entries.Should().Contain(e => StoredBytes(e.Id, "Заметки/конспект.md").SequenceEqual(v2),
            "новая версия сама легла в корзину");
    }

    [Fact]
    public void Restore_RestoresOriginalPath_NotTheEntryName_MutationC()
    {
        var content = Bytes("содержимое\n");
        var path = WriteOriginal("Заметки/вложенный/файл.md", content);
        var id = ChildTrash.Snapshot(_root, "Заметки/вложенный/файл.md", "kid-1");
        id.Should().NotBeNull();

        File.Delete(path);

        ChildTrash.Restore(_root, id!).Should().BeTrue();
        // Мутация (в): «копия наугад по id, путь из меты не читать» — краснеет: цели нет.
        File.ReadAllBytes(path).Should().Equal(content);
    }

    [Fact]
    public void Restore_OfAbsentEntry_False_NoThrow()
    {
        ChildTrash.Restore(_root, "20261119000000000-deadbeef").Should().BeFalse();
    }

    // --- List: только настоящие записи, порядок — по времени ---

    [Fact]
    public void List_SkipsForeignDirsAndBrokenMeta_ListsRealEntriesByIdOrder()
    {
        WriteOriginal("а.txt", Bytes("а"));
        var id1 = ChildTrash.Snapshot(_root, "а.txt", "kid-1");
        id1.Should().NotBeNull();
        Thread.Sleep(2); // метка id — с миллисекундами: порядок должен читаться по id
        WriteOriginal("б.txt", Bytes("б"));
        var id2 = ChildTrash.Snapshot(_root, "б.txt", "kid-1");
        id2.Should().NotBeNull();

        Directory.CreateDirectory(Path.Combine(_root, ChildTrash.TrashDir, "не-запись"));
        var broken = Path.Combine(_root, ChildTrash.TrashDir, "20260101000000000-badbad00");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "meta.json"), "{ не json");

        var entries = ChildTrash.List(_root);

        using var scope = new AssertionScope();
        // Equal(params) иначе съел бы строку причины как третий ожидаемый элемент.
        entries.Select(e => e.Id).Should().Equal(new[] { id1, id2 }, "чужой каталог и битая мета — не записи");
        entries[0].OriginalPath.Should().Be("а.txt");
        entries[0].Actor.Should().Be("kid-1");
        string.CompareOrdinal(entries[1].Id, entries[0].Id).Should().BeGreaterThan(0,
            "id начинается с метки времени — порядок хронологический");
    }

    // --- Sweep: 30 дней, границы half-open [from, to) ---

    [Fact]
    public void Sweep_RemovesOlderThanRetention_KeepsFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var old = PlantOldEntry("старый.txt", Bytes("старое"), now - TimeSpan.FromDays(31));
        var fresh = PlantOldEntry("свежий.txt", Bytes("свежее"), now - TimeSpan.FromDays(1));

        var removed = ChildTrash.Sweep(_root, now);

        using var scope = new AssertionScope();
        removed.Should().Be(1);
        Directory.Exists(TrashDirOf(old)).Should().BeFalse("просроченная снесена");
        Directory.Exists(TrashDirOf(fresh)).Should().BeTrue("свежая цела");
        ChildTrash.List(_root).Select(e => e.Id).Should().Equal(fresh);
    }

    [Fact]
    public void Sweep_Boundary_ExactlyRetention_IsKept_JustOver_IsRemoved()
    {
        // now — с точностью метки id (миллисекунды): метка на диске теряет доли миллисекунды,
        // граница «ровно Retention» существует только в той же шкале, что и имя записи.
        var raw = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(raw.Ticks - raw.Ticks % TimeSpan.TicksPerMillisecond, raw.Offset);
        // Ровно Retention — не «старше 30 дней»: жива (half-open: смерть строго за границей).
        var boundary = PlantOldEntry("граница.txt", Bytes("граница"), now - ChildTrash.Retention);
        var justOver = PlantOldEntry("за-границей.txt", Bytes("за границей"), now - ChildTrash.Retention - TimeSpan.FromMilliseconds(1));

        var removed = ChildTrash.Sweep(_root, now);

        using var scope = new AssertionScope();
        removed.Should().Be(1);
        Directory.Exists(TrashDirOf(boundary)).Should().BeTrue("ровно 30 дней — ещё не старше");
        Directory.Exists(TrashDirOf(justOver)).Should().BeFalse("на миллисекунду старше — снесена");
    }

    [Fact]
    public void SweepAtSnapshot_RemovesExpired_MutationB()
    {
        // Отдельного таймера нет: просроченное сносится следующим снимком (Sweep при Snapshot).
        var old = PlantOldEntry("старый.txt", Bytes("старое"), DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        WriteOriginal("новое.txt", Bytes("новое"));

        ChildTrash.Snapshot(_root, "новое.txt", "kid-1").Should().NotBeNull();

        using var scope = new AssertionScope();
        Directory.Exists(TrashDirOf(old)).Should().BeFalse("снимок сам снёс просроченное");
        ChildTrash.List(_root).Should().ContainSingle("свежая запись цела");
    }

    [Fact]
    public void Sweep_NoTrashDir_Zero_NoThrow()
    {
        ChildTrash.Sweep(_root, DateTimeOffset.UtcNow).Should().Be(0);
    }

    // --- чистка корзины не отменяет снимок (доработка по ревью 1558aca4) ---

    /// <summary>Unix-права на каталоге; false — платформа не Unix (Windows: ACL, не chmod).</summary>
    private static bool TryLock(string dir, UnixFileMode mode)
    {
        try { File.SetUnixFileMode(dir, mode); return true; }
        catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ограничивают ли раннера Unix-права: каталог 0000, в нём пробный mkdir. У root (uid 0,
    /// CAP_DAC_OVERRIDE) права не работают — без этого проверки случай «неудаляемого каталога»
    /// под root прошёл бы молча (сначала — Skip, потом — красные утверждения: тот же приём,
    /// что у Skip.IfNot(linkCreated, …) в PluginDraftDiffTests.cs:323).
    /// </summary>
    private bool UnixPermissionsBindRunner()
    {
        var probe = Path.Combine(_root, "probe");
        Directory.CreateDirectory(probe);
        if (!TryLock(probe, UnixFileMode.None)) return false;
        bool bound;
        try { Directory.CreateDirectory(Path.Combine(probe, "in")); bound = false; }
        catch (UnauthorizedAccessException) { bound = true; }
        // права обратно: Dispose должен достать пробный каталог.
        TryLock(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bound;
    }

    [SkippableFact]
    public void Sweep_UnremovableExpiredDir_SnapshotStillSucceeds_NoThrow()
    {
        // Мутация c1: «ловить только IOException» — краснеет: UAF от Directory.Delete
        // вылетает наружу через Snapshot.
        Skip.If(!UnixPermissionsBindRunner(),
            "раннер не связан Unix-правами (root на Linux или Windows): неудаляемость не построить — случай не проходит молча");
        var old = PlantOldEntry("старый.txt", Bytes("старое"), DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        var poison = PlantOldEntry("отрава.txt", Bytes("отравленное"), DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        // Невыводимое содержимое: каталог 0500 внутри записи — его содержимое не удалить
        // (нет прав на запись в нём), и сама запись по recursive-удалению не уходит.
        var locked = Path.Combine(TrashDirOf(poison), "закрытый");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "данные.txt"), "чужое содержимое без прав на удаление");
        TryLock(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute); // 0500

        var path = WriteOriginal("конспект.md", Bytes("прежнее"));
        var id = ChildTrash.Snapshot(_root, "конспект.md", "kid-1");

        using var scope = new AssertionScope();
        id.Should().NotBeNull("снимок существующего файла не отменяется чисткой корзины");
        StoredBytes(id!, "конспект.md").Should().Equal(Bytes("прежнее"), "снимок сделан");
        Directory.Exists(TrashDirOf(old)).Should().BeFalse("удаляемая просроченная снесена как умели");
        Directory.Exists(TrashDirOf(poison)).Should().BeTrue("неудаляемая — проходим мимо, она живёт дальше");
        File.ReadAllBytes(path).Should().Equal(Bytes("прежнее"), "оригинал не тронут");
        TryLock(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // Dispose достанет
    }

    [SkippableFact]
    public void Sweep_TrashNotListable_Zero_NoThrow_AndSnapshotStillSucceeds()
    {
        // Мутация c1 (вторая половина): «ловить только IOException» — краснеет: UAF от
        // GetDirectories вылетает через Snapshot.
        Skip.If(!UnixPermissionsBindRunner(),
            "раннер не связан Unix-правами (root на Linux или Windows): нечитаемость не построить — случай не проходит молча");
        // 0300 на корзине: перечислить нельзя (нет прав на чтение), Exists — да (нужен только
        // проход по _root). Это случай «корзину не перечислить», а не «корзины нет».
        var trash = Path.Combine(_root, ChildTrash.TrashDir);
        Directory.CreateDirectory(trash);
        TryLock(trash, UnixFileMode.UserWrite | UnixFileMode.UserExecute); // 0300

        var act = () => ChildTrash.Sweep(_root, DateTimeOffset.UtcNow);
        act.Should().NotThrow("чистка не бросает наружу даже когда корзину не перечислить");
        act().Should().Be(0);

        // Тот же инвариант со стороны операции: нечитаемая корзина не запрещает снимок
        // (write в корзине есть — запись создать можно).
        var path = WriteOriginal("конспект.md", Bytes("прежнее"));
        ChildTrash.Snapshot(_root, "конспект.md", "kid-1").Should().NotBeNull();
    }

    [Fact]
    public void Restore_NestedSnapshotCarriesActorOfOriginalEntry()
    {
        var v1 = Bytes("первая версия\n");
        var v2 = Bytes("затёрто репетитором\n");
        var path = WriteOriginal("конспект.md", v1);
        var id1 = ChildTrash.Snapshot(_root, "конспект.md", "kid-1");
        id1.Should().NotBeNull();
        File.WriteAllBytes(path, v2);

        // Мутация (ж1): «Restore без actor» — краснеет: в мете снимка на месте actor пустой.
        ChildTrash.Restore(_root, id1!).Should().BeTrue();
        var newEntry = ChildTrash.List(_root).Single(e => e.Id != id1);
        newEntry.Actor.Should().Be("kid-1", "вложенный снимок — от того же, кто спросил исходный");
    }

    [SkippableFact]
    public void Restore_NestedSnapshotFailure_ReturnsFalse_NoThrow()
    {
        // Мутация (ж2): «IOException/UAF вложенного снимка наружу» — краснеет: Throw вместо false.
        Skip.If(!UnixPermissionsBindRunner(),
            "раннер не связан Unix-правами (root на Linux или Windows): сбой снимка не построить");
        var v1 = Bytes("первая версия\n");
        var path = WriteOriginal("конспект.md", v1);
        var id1 = ChildTrash.Snapshot(_root, "конспект.md", "kid-1");
        id1.Should().NotBeNull();

        // 0500 на корзине: мету существующей записи прочитать можно, новый каталог снимка
        // в ней создать нельзя — это и есть сбой вложенного снимка.
        TryLock(Path.Combine(_root, ChildTrash.TrashDir),
            UnixFileMode.UserRead | UnixFileMode.UserExecute); // 0500

        ChildTrash.Restore(_root, id1!).Should().BeFalse("сбой снимка — не перезаписываем, наружу — false");
        File.ReadAllBytes(path).Should().Equal(v1, "сбой снимка не перетёр файл");
        TryLock(Path.Combine(_root, ChildTrash.TrashDir),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // Dispose достанет
    }

    [Fact]
    public void Restore_BadEntryId_False_NoThrow()
    {
        // Контракт формы id (п.3 ревью 1558aca4): с «/», «.» или пустой — не запись этой корзины.
        // Явная проверка в Restore:83 (GetFileName) отсекает пустой id и любой с «/» (на Windows
        // — и с «\»); «.», «..» и «nested\deep» на Linux её проходят и отсекаются ниже по пути:
        // «..» — SafePath (ChildTrash.cs:87), «.» и «\»-имя — отсутствием меты. Старший код
        // отклонял эти же входы побочно: «..» — тем же catch SafePath (он был уже в f674fdd25),
        // остальные — отсутствием меты.
        using var scope = new AssertionScope();
        ChildTrash.Restore(_root, "").Should().BeFalse("пустой id — не запись");
        ChildTrash.Restore(_root, ".").Should().BeFalse("«.» — не запись");
        ChildTrash.Restore(_root, "..").Should().BeFalse("«..» — не запись");
        ChildTrash.Restore(_root, "nested/deep").Should().BeFalse("id с «/» — не запись");
        ChildTrash.Restore(_root, @"nested\deep").Should().BeFalse("id с «\\» — не запись");
    }

    // --- конкурентность ---

    [Fact]
    public async Task TwoConcurrentSnapshots_SameFile_TwoDistinctReadableRecords()
    {
        // Блокировок по файлу нет и не нужно: имена уникальны по метке и guid-суффиксу.
        var path = WriteOriginal("конкурс.md", Bytes("версия"));
        for (var i = 0; i < 20; i++)
        {
            var first = Task.Run(() => ChildTrash.Snapshot(_root, "конкурс.md", "kid-1"));
            var second = Task.Run(() => ChildTrash.Snapshot(_root, "конкурс.md", "kid-1"));
            await Task.WhenAll(first, second);
            var r1 = await first;
            var r2 = await second;

            using var scope = new AssertionScope();
            r1.Should().NotBeNull($"снимок {i} №1");
            r2.Should().NotBeNull($"снимок {i} №2");
            r1.Should().NotBe(r2, "две записи одного файла — разные id");
            StoredBytes(r1!, "конкурс.md").Should().Equal(Bytes("версия"), $"запись {r1} читается");
            StoredBytes(r2!, "конкурс.md").Should().Equal(Bytes("версия"), $"запись {r2} читается");
        }
        File.ReadAllBytes(path).Should().Equal(Bytes("версия"), "снимок оригинал не трогает");
        ChildTrash.List(_root).Should().HaveCount(40);
    }

    [Fact]
    public async Task SweepDuringRestore_EitherRestoredOrNotFound_NoException()
    {
        var v1 = Bytes("прежняя\n");
        var path = WriteOriginal("гонка.md", v1);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            // Каждая итерация начинает с существующего файла: в прошлой мог победить NotFound
            // и файл остался несуществующим.
            WriteOriginal("гонка.md", v1);
            var id = ChildTrash.Snapshot(_root, "гонка.md", "kid-1");
            id.Should().NotBeNull($"снимок {i}");
            File.Delete(path);

            // Та же запись: чистка считает её просроченной, восстановление её читает.
            var sweep = Task.Run(() => ChildTrash.Sweep(_root, now + ChildTrash.Retention + TimeSpan.FromMinutes(1)));
            var restore = Task.Run(() => ChildTrash.Restore(_root, id!));
            await Task.WhenAll(sweep, restore);
            var removed = await sweep;
            var restored = await restore;

            using var scope = new AssertionScope();
            removed.Should().Be(1, $"снимок {i}: запись просрочена для этого now — Sweep её видит");
            if (restored)
                File.ReadAllBytes(path).Should().Equal(v1, $"снимок {i}: Restore успешен — содержимое на месте");
            else
                Directory.Exists(TrashDirOf(id!)).Should().BeFalse($"снимок {i}: NotFound — запись уже снесена");
        }
    }

    // --- сбой записи (контракт Core-половины: IOException наружу, вызывающий отказывает) ---

    [Fact]
    public void Snapshot_WriteFailure_ThrowsIOException_CallerMustDeny()
    {
        // «Диск» переносимо: .ccs-trash занят ФАЙЛОМ — каталог записи создать нельзя,
        // File.Copy/Directory.CreateDirectory дают IOException. Мутация (г) — «разрешать
        // Write при сбое снимка» — краснеет на стороне вызывающего (Llm.Tests).
        WriteOriginal("конспект.md", Bytes("прежнее"));
        File.WriteAllText(Path.Combine(_root, ChildTrash.TrashDir), "не каталог");

        var act = () => ChildTrash.Snapshot(_root, "конспект.md", "kid-1");
        act.Should().Throw<IOException>();
    }
}
