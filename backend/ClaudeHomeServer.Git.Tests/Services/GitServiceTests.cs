using System.Diagnostics;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Интеграционные тесты GitService на настоящем git CLI: временный репозиторий на диске,
// запуск через LocalProcessRunner (среда local; container-путь проверяется смоуком).
[Trait("Category", "Slow")]
public class GitServiceTests : IAsyncLifetime, IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => Local;
    }

    private readonly string _repo;
    private readonly GitService _git = new(new LocalOnlyFactory());
    // Временные папки сверх _repo (bare-«сервер» и второй клон в тестах публикации)
    private readonly List<string> _extraDirs = [];

    public GitServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "gitsvc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        await RawGit("config", "user.email", "test@test");
        await RawGit("config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nдва\nтри\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b1\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var dir in _extraDirs.Append(_repo))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
        }
    }

    // Прямой git для арранжей (без ассертов на сам GitService); в произвольной папке — RawGitIn
    private Task RawGit(params string[] args) => RawGitIn(_repo, args);

    [Fact]
    public async Task Status_Разбирает_Staged_Unstaged_Untracked()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nДВА\nтри\n"); // unstaged M
        await File.WriteAllTextAsync(Path.Combine(_repo, "new.txt"), "новый\n");        // untracked
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b2\n");
        await _git.StageAsync(null, _repo, "b.txt");                                     // staged M

        var st = await _git.StatusAsync(null, _repo);

        st.IsRepo.Should().BeTrue();
        st.Branch.Should().Be("main");
        st.Staged.Should().ContainSingle(f => f.Path == "b.txt" && f.Status == "M");
        st.Unstaged.Should().ContainSingle(f => f.Path == "a.txt" && f.Status == "M");
        st.Untracked.Should().ContainSingle(f => f.Path == "new.txt");
    }

    // Новая папка должна разворачиваться в файлы (-uall). Без флага git отдаёт её одной записью
    // «assets/», и панель изменений открывала путь папки как файл — чтение файла падало.
    // Имя папки нейтральное: дефолтный .gitignore из InitAsync игнорирует служебные (.claude и пр.).
    [Fact]
    public async Task Status_Новая_Папка_Разворачивается_В_Файлы()
    {
        var dir = Path.Combine(_repo, "assets");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "one.txt"), "раз\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "two.txt"), "два\n");

        var st = await _git.StatusAsync(null, _repo);

        st.Untracked.Select(f => f.Path).Should().Contain(["assets/one.txt", "assets/two.txt"]);
        st.Untracked.Should().NotContain(f => f.Path.EndsWith('/'));
    }

    // Решение по перфу листинга (27.08): untracked больше не замеряются per-file
    // (`git diff --no-index` на каждый — секунды на проектах без .gitignore).
    // Контракт: untracked приходят с Added/Deleted = null, tracked — с числами.
    [Fact]
    public async Task Status_Untracked_БезПострочнойСтатистики()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "u1.txt"), "раз\nдва\nтри\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nДВА\nтри\n"); // tracked unstaged

        var st = await _git.StatusAsync(null, _repo);

        var untracked = st.Untracked.Should().ContainSingle(f => f.Path == "u1.txt").Which;
        untracked.Added.Should().BeNull();
        untracked.Deleted.Should().BeNull();
        var modified = st.Unstaged.Should().ContainSingle(f => f.Path == "a.txt").Which;
        modified.Added.Should().NotBeNull("tracked-файлы по-прежнему получают +N/−M");
    }

    [Fact]
    public async Task Commit_Кириллица_И_Amend()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "правка\n");
        await _git.StageAllAsync(null, _repo);
        var sha1 = await _git.CommitAsync(null, _repo, "фикс: правка файла а");

        var log = await _git.LogAsync(null, _repo, 5);
        log[0].Subject.Should().Be("фикс: правка файла а");

        // Amend меняет сообщение последнего коммита, не плодя новый
        var sha2 = await _git.CommitAsync(null, _repo, "фикс: правка файла а (уточнено)", amend: true);
        var log2 = await _git.LogAsync(null, _repo, 5);
        log2[0].Subject.Should().Be("фикс: правка файла а (уточнено)");
        log2.Count.Should().Be(log.Count);
        sha2.Should().NotBe(sha1);
    }

    // ---------- Инъекция git-опций через ref-подобные параметры (блокер приёмки волны 3.1) ----------

    // branch уходит в argv как есть: элемент с ведущим «-» git разбирает как ОПЦИЮ, и
    // «--output=<путь>» в git log перезаписывает произвольный файл мимо SafeJoin и границ
    // проекта (воспроизведено на живом git в задаче волны 3.1). Валидация — в GitService,
    // один слой для MCP (git_log) и REST (/git/log): проверяем и непопадание файла на диск.
    [Fact]
    public async Task Log_ВеткаОпция_Отвергается_И_НеСоздаётФайл()
    {
        var target = Path.Combine(Path.GetTempPath(),
            "gitsvc_inject_" + Guid.NewGuid().ToString("N") + ".txt");

        var act = () => _git.LogAsync(null, _repo, 5, $"--output={target}");

        await act.Should().ThrowAsync<GitCommandException>()
            .WithMessage("*Некорректная ревизия*");
        File.Exists(target).Should().BeFalse(
            "git не должен был выполниться вовсе — файл вне репо не создаётся и не затирается");
    }

    // Валидация не ломает штатные ревизии: имя ветки, sha и пусто (текущая)
    [Fact]
    public async Task Log_ВеткаИSha_ПроходятВалидацию()
    {
        var head = (await _git.LogAsync(null, _repo, 1))[0].Sha;

        (await _git.LogAsync(null, _repo, 5, "main")).Should().NotBeEmpty("имя ветки валидно");
        (await _git.LogAsync(null, _repo, 5, head)).Should().NotBeEmpty("sha валиден");
        (await _git.LogAsync(null, _repo, 5)).Should().NotBeEmpty("пусто — текущая ветка");
    }

    // Тот же паттерн в соседних методах: подконтрольная строка в argv обязательна
    // выглядеть как ref — иначе «--force» в checkout/create-branch менял бы поведение git
    [Fact]
    public async Task CheckoutИCreateBranch_ИмяСОпцией_Отвергается()
    {
        await FluentActions.Awaiting(() => _git.CheckoutAsync(null, _repo, "--force"))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");
        await FluentActions.Awaiting(() => _git.CreateBranchAsync(null, _repo, "-b", "main"))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");
        await FluentActions.Awaiting(() => _git.CreateBranchAsync(null, _repo, "ok", "--detach"))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");
    }

    // IGitRefSnapshotStore — публичный контракт для будущих вертикалей (комментарий в
    // DossierBranch.cs: «сторонняя вертикаль заведёт свой клон DossierBranch и позовёт те
    // же методы со своими значениями»). Те же ревизии, что в Log/Checkout/CreateBranch,
    // доходят до argv в RefExists/LocalTip/ResolveRef/PushRef — каждая обязана отбить
    // fullRef/candidate, начинающийся с «-», до того как git увидит его как флаг.
    [Fact]
    public async Task IGitRefSnapshotStore_RefСОпцией_Отвергается()
    {
        var target = Path.Combine(Path.GetTempPath(),
            "gitsvc_rfs_inject_" + Guid.NewGuid().ToString("N") + ".txt");

        // RefExists: invalid input — программная ошибка, throw (не молча false «ветки нет»)
        await FluentActions.Awaiting(() => _git.RefExistsAsync(null, _repo, $"--output={target}"))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");
        File.Exists(target).Should().BeFalse(
            "git не должен был выполниться вовсе — файл вне репо не создаётся и не затирается");

        // LocalTip: тот же контракт
        await FluentActions.Awaiting(() => _git.LocalTipAsync(null, _repo, "--force"))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");

        // ResolveRef: валидация в цикле — битый кандидат в списке тоже отвергается,
        // даже если перед ним стоял валидный (порядок «битый после валидного» не
        // прикрывает инъекцию)
        await FluentActions.Awaiting(() => _git.ResolveRefAsync(null, _repo,
            ["main", $"--output={target}"]))
            .Should().ThrowAsync<GitCommandException>().WithMessage("*Некорректная ревизия*");
    }

    // ---------- Метрика охвата паспортов: CountRecentCommitsAsync (GET /dossiers) ----------

    // Обе даты коммита в прошлом: --since фильтрует по committer date, поэтому сдвигаем
    // и author-дату тоже — коммит честно «старый» с обеих сторон
    private static readonly Dictionary<string, string> OldDates = new()
    {
        ["GIT_AUTHOR_DATE"] = "2020-01-01T12:00:00",
        ["GIT_COMMITTER_DATE"] = "2020-01-01T12:00:00",
    };

    [Fact]
    public async Task CountRecentCommits_Окно_Файл_И_Follow()
    {
        // Свежий коммит поверх фикстурного: в окне 7 суток лежат оба
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "правка\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "свежий коммит");
        (await _git.CountRecentCommitsAsync(null, _repo, days: 7)).Should().Be(2);

        // Файловый фильтр: знаменатель — история одного файла (b.txt трогал только init,
        // a.txt — init и свежий)
        (await _git.CountRecentCommitsAsync(null, _repo, days: 7, "b.txt")).Should().Be(1);
        (await _git.CountRecentCommitsAsync(null, _repo, days: 7, "a.txt")).Should().Be(2);

        // --follow: переименование не рвёт историю файла
        await RawGit("mv", "a.txt", "c.txt");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "переименовали a в c");
        (await _git.CountRecentCommitsAsync(null, _repo, days: 7, "c.txt"))
            .Should().Be(3, "история c.txt включает и коммиты a.txt до переименования");
    }

    // Отдельное репо с монотонной историей: корень датирован в прошлое, tip — сейчас.
    // --since у git — traversal-эвристика по датам, рассчитанная ровно на такой порядок
    // (старое ниже свежего); инвертированные даты она не обязана считать честно.
    [Fact]
    public async Task CountRecentCommits_ИсключаетКоммитыВнеОкна()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitsvc_since_" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(dir);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        await RawGitIn(dir, "config", "user.email", "test@test");
        await RawGitIn(dir, "config", "user.name", "Тест");

        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "старое\n");
        await _git.StageAllAsync(null, dir);
        await RawGitIn(dir, OldDates, "commit", "-m", "старый корень");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "новое\n");
        await _git.StageAllAsync(null, dir);
        await RawGitIn(dir, "commit", "-m", "свежий коммит");

        (await _git.CountRecentCommitsAsync(null, dir, days: 7))
            .Should().Be(1, "в окне только свежий коммит, корень 2020 года исключён");
        (await _git.CountRecentCommitsAsync(null, dir, days: 7, "a.txt"))
            .Should().Be(1, "файловая ветка тоже отбрасывает старый коммит");
    }

    [Fact]
    public async Task StageHunk_Индексирует_Только_Патч()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "ноль\nдва\nтри\nчетыре\n");
        var diff = await _git.DiffFileAsync(null, _repo, "a.txt", staged: false);
        diff.Should().NotBeNull();

        await _git.StageHunkAsync(null, _repo, diff!);
        var st = await _git.StatusAsync(null, _repo);
        st.Staged.Should().ContainSingle(f => f.Path == "a.txt");

        // Обратно: unstage того же патча очищает индекс
        await _git.UnstageHunkAsync(null, _repo, diff!);
        var st2 = await _git.StatusAsync(null, _repo);
        st2.Staged.Should().BeEmpty();
        st2.Unstaged.Should().ContainSingle(f => f.Path == "a.txt");
    }

    [Fact]
    public async Task Stash_Push_List_Pop()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "отложим\n");
        await _git.StashPushAsync(null, _repo, "проба стэша");

        (await _git.StatusAsync(null, _repo)).Unstaged.Should().BeEmpty();
        var list = await _git.StashListAsync(null, _repo);
        list.Should().ContainSingle(s => s.Message.Contains("проба стэша"));

        await _git.StashPopAsync(null, _repo, 0);
        (await _git.StatusAsync(null, _repo)).Unstaged.Should().ContainSingle(f => f.Path == "a.txt");
        (await _git.StashListAsync(null, _repo)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revert_Создаёт_Обратный_Коммит()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "плохая правка\n");
        await _git.StageAllAsync(null, _repo);
        var bad = await _git.CommitAsync(null, _repo, "плохой коммит");

        await _git.RevertCommitAsync(null, _repo, bad);

        var log = await _git.LogAsync(null, _repo, 5);
        log[0].Subject.Should().StartWith("Revert");
        (await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt"))).Should().NotContain("плохая правка");
    }

    [Fact]
    public async Task Blame_Отдаёт_Автора_Каждой_Строки()
    {
        var blame = await _git.BlameAsync(null, _repo, "a.txt");
        blame.Should().NotBeEmpty();
        blame.Should().OnlyContain(l => l.Author == "Тест" && l.ShortSha.Length == 7);
        blame.Select(l => l.Content).Should().ContainInOrder("один", "два", "три");
    }

    [Fact]
    public async Task Branch_Create_Checkout_List()
    {
        await _git.CreateBranchAsync(null, _repo, "feature/test", from: null);
        var branches = await _git.BranchesAsync(null, _repo);
        branches.Should().Contain(b => b.Name == "feature/test" && b.Current);

        await _git.CheckoutAsync(null, _repo, "main");
        (await _git.StatusAsync(null, _repo)).Branch.Should().Be("main");
    }

    [Fact]
    public async Task CommitDetail_Файлы_И_Дифф_Файла()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "для деталей\n");
        await _git.StageAllAsync(null, _repo);
        var sha = await _git.CommitAsync(null, _repo, "детальный коммит\n\nтело описания");

        var detail = await _git.CommitDetailAsync(null, _repo, sha);
        detail.Should().NotBeNull();
        detail!.Subject.Should().Be("детальный коммит");
        detail.Body.Should().Contain("тело описания");
        detail.Files.Should().ContainSingle(f => f.Path == "a.txt" && f.Status == "M");

        var diff = await _git.CommitFileDiffAsync(null, _repo, sha, "a.txt");
        diff.Should().Contain("+для деталей");
    }

    [Fact]
    public async Task CommitDetail_Кириллическое_Имя_Файла_Читаемо()
    {
        // Регресс: без core.quotepath=false git отдаёт «\320\232…» вместо кириллицы в путях
        Directory.CreateDirectory(Path.Combine(_repo, "Комментарии"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "Комментарии", "Этот шаг.md"), "текст\n");
        await _git.StageAllAsync(null, _repo);
        var sha = await _git.CommitAsync(null, _repo, "кириллический файл");

        var detail = await _git.CommitDetailAsync(null, _repo, sha);
        detail!.Files.Should().ContainSingle(f => f.Path == "Комментарии/Этот шаг.md");
    }

    [Fact]
    public async Task Discard_Возвращает_Файл_К_HEAD()
    {
        // Сравнение с нормализацией переводов строк: git на Windows восстанавливает CRLF (autocrlf)
        static string Norm(string s) => s.Replace("\r\n", "\n");
        var before = Norm(await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt")));
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "мусор\n");
        await _git.DiscardAsync(null, _repo, "a.txt");
        Norm(await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt"))).Should().Be(before);
    }

    // Без origin «неопубликовано» = вся история ветки: иначе кнопка публикации не появилась
    // бы вовсе и подключить удалённый репозиторий из интерфейса было бы неоткуда
    [Fact]
    public async Task Unpushed_Без_Origin_Считает_Всю_Историю_Ветки()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "c.txt"), "c\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "второй коммит");

        var unpushed = await _git.UnpushedLogAsync(null, _repo);

        unpushed.Should().HaveCount(2);
        unpushed[0].Subject.Should().Be("второй коммит");
    }

    // Поднять bare-«сервер» и опубликовать в него main: origin живой, вся история — на сервере
    private async Task SetupPublishedOriginAsync()
    {
        var bare = Path.Combine(Path.GetTempPath(), "gitsvc_bare_" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(bare);
        await RawGitIn(Path.GetTempPath(), "init", "--bare", "-b", "main", bare);
        await RawGit("remote", "add", "origin", bare);
        await RawGit("push", "-u", "origin", "main");
    }

    // Новая ветка ещё не отслеживает ничего, и origin/<branch> для неё не существует —
    // «неопубликовано» это ровно её собственные коммиты, а не вся история от корня
    [Fact]
    public async Task Unpushed_Новая_Ветка_Без_Upstream_Считает_Только_Свои_Коммиты()
    {
        await SetupPublishedOriginAsync();
        await RawGit("checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(_repo, "f.txt"), "f\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "коммит в ветке");

        var unpushed = await _git.UnpushedLogAsync(null, _repo);

        // Ровно один коммит: ContainSingle(предикат) фильтрует коллекцию и прошёл бы
        // и на всей истории — регресс «весь HEAD» ловится только счётом
        unpushed.Should().HaveCount(1);
        unpushed[0].Subject.Should().Be("коммит в ветке");
    }

    // Detached HEAD имени ветки не даёт вовсе: все коммиты уже на origin — публиковать нечего
    [Fact]
    public async Task Unpushed_В_Detached_HEAD_Пуст_При_Живом_Origin()
    {
        await SetupPublishedOriginAsync();
        await RawGit("checkout", "--detach", "HEAD");

        (await _git.UnpushedLogAsync(null, _repo)).Should().BeEmpty();
    }

    // Пустой репозиторий: HEAD не резолвится, но список должен быть пустым, а не падать
    [Fact]
    public async Task Unpushed_В_Репозитории_Без_Коммитов_Пуст()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitsvc_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _extraDirs.Add(dir);
        await _git.InitAsync(null, dir);

        (await _git.UnpushedLogAsync(null, dir)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetRemoteUrl_Отдаёт_Null_Без_Origin_И_Адрес_После_SetRemote()
    {
        (await _git.GetRemoteUrlAsync(null, _repo)).Should().BeNull();

        await _git.SetRemoteAsync(null, _repo, "https://example.test/repo.git");

        (await _git.GetRemoteUrlAsync(null, _repo)).Should().Be("https://example.test/repo.git");
    }

    [Fact]
    public async Task Push_Без_Remote_Даёт_Понятную_Ошибку()
    {
        var act = () => _git.PushAsync(null, _repo);
        await act.Should().ThrowAsync<GitCommandException>();
    }

    // ---------- Публикация при расхождении с origin ----------

    // Разводит ветку с origin: поднимает bare-«сервер», уводит его вперёд чужим коммитом
    // через второй клон и добавляет свой локальный коммит. На выходе ahead>0 И behind>0 —
    // ровно та ситуация, где обычный push отклоняется, а pull --ff-only не проходит.
    // conflicting: обе стороны правят ОДНУ строку одного файла — тогда rebase не сойдётся
    private async Task SetupDivergedAsync(bool conflicting = false)
    {
        var tmp = Path.GetTempPath();
        var bare = Path.Combine(tmp, "gitsvc_bare_" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(tmp, "gitsvc_other_" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(bare);
        _extraDirs.Add(other);

        // -b main обязателен: без него HEAD голого репозитория смотрит на master, клон
        // встаёт на несуществующую ветку и чужой коммит уходит мимо main — расхождения
        // не возникает вовсе (на этом тесты и ловили арранж)
        await RawGitIn(tmp, "init", "--bare", "-b", "main", bare);
        await RawGit("remote", "add", "origin", bare);
        await RawGit("push", "-u", "origin", "main");

        // Чужой коммит: origin уходит вперёд
        await RawGitIn(tmp, "clone", bare, other);
        await RawGitIn(other, "config", "user.email", "other@test");
        await RawGitIn(other, "config", "user.name", "Другой");
        if (conflicting) await File.WriteAllTextAsync(Path.Combine(other, "a.txt"), "один\nверсия сервера\nтри\n");
        else await File.WriteAllTextAsync(Path.Combine(other, "server.txt"), "с сервера\n");
        await RawGitIn(other, "add", "-A");
        await RawGitIn(other, "commit", "-m", "коммит на сервере");
        await RawGitIn(other, "push");

        // Свой коммит поверх старого состояния — ветки разошлись
        if (conflicting) await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nмоя версия\nтри\n");
        else await File.WriteAllTextAsync(Path.Combine(_repo, "local.txt"), "локальный\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "локальный коммит");
        await _git.FetchAsync(null, _repo);   // чтобы behind посчитался
    }

    [Fact]
    public async Task Push_При_Расхождении_Даёт_GitDivergedException()
    {
        await SetupDivergedAsync();

        var act = () => _git.PushAsync(null, _repo);
        await act.Should().ThrowAsync<GitDivergedException>();
    }

    [Fact]
    public async Task Sync_Линеаризует_Расхождение_И_Публикует()
    {
        await SetupDivergedAsync();

        await _git.SyncAsync(null, _repo, "main", hasUpstream: true);

        var st = await _git.StatusAsync(null, _repo);
        st.Ahead.Should().Be(0);
        st.Behind.Should().Be(0);
        // Обе стороны на месте: чужой коммит подтянут, свой лёг поверх (rebase)
        File.Exists(Path.Combine(_repo, "server.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_repo, "local.txt")).Should().BeTrue();
    }

    // Конфликт: подтянуть автоматически нельзя — откатываемся в исходное состояние
    // и называем файлы, на которых споткнулись (UI показывает их и зовёт разобрать в чате)
    [Fact]
    public async Task Sync_При_Конфликте_Откатывает_И_Называет_Файлы()
    {
        await SetupDivergedAsync(conflicting: true);

        var act = () => _git.SyncAsync(null, _repo, "main", hasUpstream: true);

        var ex = await act.Should().ThrowAsync<GitConflictException>();
        ex.Which.Files.Should().Contain("a.txt");
        // Откат: расхождение осталось нетронутым, публикации не было
        var st = await _git.StatusAsync(null, _repo);
        st.Ahead.Should().Be(1);
        st.Behind.Should().Be(1);
    }

    // Незафиксированные правки не должны мешать публикации: --autostash убирает их
    // на время rebase и возвращает после
    [Fact]
    public async Task Sync_Переживает_Грязное_Рабочее_Дерево()
    {
        await SetupDivergedAsync();
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "правка на лету\n");

        await _git.SyncAsync(null, _repo, "main", hasUpstream: true);

        var st = await _git.StatusAsync(null, _repo);
        st.Ahead.Should().Be(0);
        st.Unstaged.Should().ContainSingle(f => f.Path == "a.txt");
    }

    // ---------- Игнор вложений чата ----------

    // Реальный кодовый проект приходит со своим .gitignore — дефолтный (с .cc-attachments/) ему
    // не пишется, поэтому в тестах заменяем сгенерированный InitAsync на пользовательский
    private async Task WithOwnGitignoreAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, ".gitignore"), "node_modules/\nbin/\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "свой .gitignore");
    }

    private async Task AddAttachmentAsync(string root)
    {
        var dir = Path.Combine(root, ".cc-attachments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "скриншот.png"), "фейковые байты");
    }

    [Fact]
    public async Task Вложения_Игнорируются_В_Проекте_Со_Своим_Gitignore()
    {
        await WithOwnGitignoreAsync();
        var ownGitignore = await File.ReadAllTextAsync(Path.Combine(_repo, ".gitignore"));

        AttachmentsGitExclude.Ensure(_repo);
        await AddAttachmentAsync(_repo);

        // Рабочее дерево чистое: вложения не видно в статусе
        var st = await _git.StatusAsync(null, _repo);
        st.Untracked.Should().BeEmpty();
        st.Unstaged.Should().BeEmpty();

        // `git add -A` вложение не берёт
        await _git.StageAllAsync(null, _repo);
        (await _git.StatusAsync(null, _repo)).Staged.Should().BeEmpty();

        // .gitignore пользователя не тронут
        (await File.ReadAllTextAsync(Path.Combine(_repo, ".gitignore"))).Should().Be(ownGitignore);
    }

    [Fact]
    public async Task Игнор_Вложений_Идемпотентен()
    {
        await WithOwnGitignoreAsync();

        AttachmentsGitExclude.Ensure(_repo);
        AttachmentsGitExclude.Ensure(_repo);
        AttachmentsGitExclude.Ensure(_repo);

        var exclude = await File.ReadAllTextAsync(Path.Combine(_repo, ".git", "info", "exclude"));
        exclude.Split('\n').Count(l => l.Trim() == ".cc-attachments/").Should().Be(1);
    }

    [Fact]
    public async Task Игнор_Вложений_Действует_В_Worktree()
    {
        await WithOwnGitignoreAsync();
        var wt = WtPath("attach");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/вложения");

        // Вложения кладутся в рабочую папку сессии — у worktree-чата это сам worktree
        AttachmentsGitExclude.Ensure(wt);
        await AddAttachmentAsync(wt);

        var st = await _git.StatusAsync(null, wt);
        st.IsWorktree.Should().BeTrue();
        st.Untracked.Should().BeEmpty();

        await _git.WorktreeRemoveAsync(null, _repo, wt);
    }

    // ---------- Worktree ----------

    private string WtPath(string name) => Path.Combine(Path.GetTempPath(), "gitsvc_wt_" + name + "_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Worktree_Add_List_Status_Remove()
    {
        var wt = WtPath("basic");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/тест-фича");

        Directory.Exists(wt).Should().BeTrue();
        File.Exists(Path.Combine(wt, ".git")).Should().BeTrue("в linked worktree .git — файл-ссылка");

        var list = await _git.WorktreeListAsync(null, _repo);
        list.Should().Contain(w => w.Branch == "wt/тест-фича");

        // Статус ИЗ worktree: своя ветка + признак IsWorktree
        var st = await _git.StatusAsync(null, wt);
        st.IsRepo.Should().BeTrue();
        st.IsWorktree.Should().BeTrue();
        st.Branch.Should().Be("wt/тест-фича");

        await _git.WorktreeRemoveAsync(null, _repo, wt);
        Directory.Exists(wt).Should().BeFalse();
        (await _git.WorktreeListAsync(null, _repo)).Should().NotContain(w => w.Branch == "wt/тест-фича");
        // Ветка переживает удаление дерева — коммиты не теряются
        (await _git.BranchesAsync(null, _repo)).Should().Contain(b => b.Name == "wt/тест-фича");
    }

    [Fact]
    public async Task Worktree_Remove_Гейтит_Грязное_Дерево_Force_Пробивает()
    {
        var wt = WtPath("dirty");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/dirty");
        await File.WriteAllTextAsync(Path.Combine(wt, "новый.txt"), "несохранённое\n");

        var act = () => _git.WorktreeRemoveAsync(null, _repo, wt);
        await act.Should().ThrowAsync<GitCommandException>("без force git отказывает при незакоммиченном");

        await _git.WorktreeRemoveAsync(null, _repo, wt, force: true);
        Directory.Exists(wt).Should().BeFalse();
    }

    [Fact]
    public async Task Worktree_Remove_Прунит_Снесённую_Руками_Папку()
    {
        var wt = WtPath("orphan");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/orphan");
        Directory.Delete(wt, recursive: true); // «руками» мимо git

        // Не кидает: папки нет — запись подчищается prune
        await _git.WorktreeRemoveAsync(null, _repo, wt);
        (await _git.WorktreeListAsync(null, _repo)).Should().NotContain(w => w.Branch == "wt/orphan");
    }

    [Fact]
    public async Task Worktree_Коммит_В_Дереве_Не_Трогает_Главную_Репу()
    {
        var wt = WtPath("commit");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/commit");
        await RawGitIn(wt, "config", "user.email", "test@test");
        await RawGitIn(wt, "config", "user.name", "Тест");

        await File.WriteAllTextAsync(Path.Combine(wt, "wt-файл.txt"), "изменение из worktree\n");
        await _git.StageAllAsync(null, wt);
        await _git.CommitAsync(null, wt, "коммит из worktree");

        // Главная репа чиста и на своей ветке, файла из worktree в ней нет
        var main = await _git.StatusAsync(null, _repo);
        main.Branch.Should().Be("main");
        main.Staged.Should().BeEmpty();
        main.Unstaged.Should().BeEmpty();
        main.Untracked.Should().BeEmpty();
        File.Exists(Path.Combine(_repo, "wt-файл.txt")).Should().BeFalse();

        await _git.WorktreeRemoveAsync(null, _repo, wt);
    }

    // Прямой git в произвольной папке (конфиг тестового worktree); env — для коммитов
    // с заданной датой (GIT_AUTHOR_DATE/GIT_COMMITTER_DATE)
    private static Task RawGitIn(string dir, params string[] args) => RawGitIn(dir, null, args);

    private static async Task RawGitIn(string dir, Dictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    // ---------- Ветка паспортов ccs/dossiers/v1: батчинг plumbing ----------

    // Прежняя пофайловая цепочка записи ветки (до батчинга): на каждый файл свой
    // hash-object -w --stdin + update-index --cacheinfo. Здесь — эталон для проверки
    // эквивалентности: пишет то же множество файлов во временный индекс и возвращает
    // дерево write-tree. Форма --cacheinfo — через пробел, как в старом коде.
    private async Task<string> LegacyWriteTreeAsync(string root, IReadOnlyList<GitSnapshotFile> files)
    {
        var idxEnv = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = ".git/index.refsnapshot-legacy" };
        var hostIdx = Path.Combine(root, ".git", "index.refsnapshot-legacy");
        try { File.Delete(hostIdx); } catch { }
        try
        {
            foreach (var f in files)
            {
                var blob = await _git.RunAsync(null, root, ["hash-object", "-w", "--stdin"], stdin: f.Content);
                blob.Ok.Should().BeTrue("эталонный hash-object не должен падать: {0}", blob.Stderr);
                var upd = await _git.RunAsync(null, root,
                    ["update-index", "--add", "--cacheinfo", "100644", blob.Stdout.Trim(), f.Path],
                    env: idxEnv);
                upd.Ok.Should().BeTrue("эталонный update-index не должен падать: {0}", upd.Stderr);
            }
            var tree = await _git.RunAsync(null, root, ["write-tree"], env: idxEnv);
            tree.Ok.Should().BeTrue("эталонный write-tree не должен падать: {0}", tree.Stderr);
            return tree.Stdout.Trim();
        }
        finally { try { File.Delete(hostIdx); } catch { } }
    }

    private static List<GitSnapshotFile> MkDossierFiles(int count)
    {
        var files = new List<GitSnapshotFile>(count);
        for (var i = 0; i < count; i++)
        {
            // Разнобой содержимого: кириллица, CRLF, кавычки и длинные строки — всё это
            // обязано хешироваться побайтово одинаково в обеих цепочках
            var body = i % 3 == 0
                ? $"# паспорт {i}\r\n\r\nзапись с CRLF и «кавычками»\r\n"
                : i % 3 == 1
                    ? $"# dossier {i}\n\ntext with \"quotes\" and 'apostrophes'\n"
                    : $"# запись {i}\n\n" + new string('д', 500) + "\n";
            files.Add(new GitSnapshotFile($"dossiers/2026/08/{i:0000}-dossier-zapis-{i}.md", body));
        }
        return files;
    }

    // 200 файлов > бюджета чанка argv (~12К символов) → update-index уходит несколькими
    // вызовами: проверяем, что батчинг (hash-object --stdin-paths одним процессом +
    // чанки update-index) даёт побайтово то же дерево, что пофайловая цепочка, и не
    // оставляет следов в рабочем дереве
    [Fact]
    public async Task WriteDossiersBranch_Батчинг_Даёт_То_Же_Дерево_Что_ПофайловыйПлюминг()
    {
        var files = MkDossierFiles(200);
        var legacyTree = await LegacyWriteTreeAsync(_repo, files);

        var result = await _git.WriteSnapshotAsync(null, _repo, DossierBranch.Ref, files, "экспорт: 200 паспортов", DossierBranch.Identity);

        result.Created.Should().BeTrue("первая запись ветки обязана создать коммит");
        var newTree = (await _git.RunAsync(null, _repo,
            ["rev-parse", $"{DossierBranch.Ref}^{{tree}}"])).Stdout.Trim();
        newTree.Should().Be(legacyTree,
            "батчинг обязан давать побайтово то же дерево, что прежняя пофайловая цепочка");

        var names = (await _git.RunAsync(null, _repo,
            ["ls-tree", "-r", "--name-only", DossierBranch.Ref])).Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        names.Should().HaveCount(200);
        Directory.Exists(Path.Combine(_repo, ".git", "refsnapshot-export-tmp")).Should()
            .BeFalse("временная папка батчинга удаляется после экспорта");
        (await _git.StatusAsync(null, _repo)).Untracked.Should().BeEmpty(
            "временная папка внутри git-dir не мусорит в рабочем дереве");
    }

    // Повторный экспорт того же набора новой цепочкой — коммита нет (дерево совпало с tip);
    // а изменение одного файла порождает ровно один новый коммит
    [Fact]
    public async Task WriteDossiersBranch_Батчинг_ИдемпотентенИИнкрементален()
    {
        var files = MkDossierFiles(60);
        var first = await _git.WriteSnapshotAsync(null, _repo, DossierBranch.Ref, files, "экспорт: 60", DossierBranch.Identity);

        first.Created.Should().BeTrue();
        var second = await _git.WriteSnapshotAsync(null, _repo, DossierBranch.Ref, files, "экспорт: 60 повторно", DossierBranch.Identity);
        second.Created.Should().BeFalse("дерево не изменилось — нового коммита быть не должно");
        second.CommitSha.Should().Be(first.CommitSha);

        files.Add(new GitSnapshotFile("dossiers/2026/08/0060-dossier-novyy.md", "# новый паспорт\n"));
        var third = await _git.WriteSnapshotAsync(null, _repo, DossierBranch.Ref, files, "экспорт: 61", DossierBranch.Identity);
        third.Created.Should().BeTrue();
        (await _git.RunAsync(null, _repo, ["rev-list", "--count", DossierBranch.Ref])).Stdout.Trim()
            .Should().Be("2", "в ветке коммит первого экспорта и один добавочный");
    }

    // ---------- RepoSnapshot / IsAncestor / CommitStat / ParentCount (волна 1 типизации) ----------

    [Fact]
    public async Task RepoSnapshot_ПустоеДерево_ПустыеПути_И_КороткийSha()
    {
        // В _repo уже есть начальный коммит из InitializeAsync
        var snap = await _git.RepoSnapshotAsync(null, _repo);
        snap.DirtyPaths.Should().BeEmpty();
        snap.ShortHeadSha.Should().NotBeNullOrEmpty().And.HaveLength(7);
        snap.Error.Should().BeNull("успешный git status — поле Error обязано быть пустым");
    }

    // Сценарий «git status упал»: выкатка обязана блокироваться гейтом GitFailed, а не
    // считать дерево чистым по факту провала. Имитируем битый .git переключением core.bare=true
    // — git отказывается выполнять status в bare-репозитории с «fatal: This operation must be
    // run in a work tree». Конструктор теста создаёт свежий _repo на каждый прогон, поэтому
    // config не утекает в другие тесты.
    [Fact]
    public async Task RepoSnapshot_GitStatusУпал_ErrorЗаполнен_ПутиНеДоверяем()
    {
        await RawGit("config", "core.bare", "true");

        var snap = await _git.RepoSnapshotAsync(null, _repo);

        snap.Error.Should().NotBeNullOrEmpty("падение git status обязано превращаться в Error");
        snap.DirtyPaths.Should().BeEmpty("пустой список при сбое status — это НЕ чистое дерево");
        // sha нерелевантен при сбое status; проверим только, что нет исключения и нет ложного «дерево чистое»
    }

    [Fact]
    public async Task RepoSnapshot_ГрязноеДерево_ВозвращаетПути()
    {
        // Поверх начального коммита: правка существующего + новый untracked
        await File.WriteAllTextAsync(Path.Combine(_repo, "новый.txt"), "правка\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "мусор\n");

        var snap = await _git.RepoSnapshotAsync(null, _repo);
        snap.DirtyPaths.Should().BeEquivalentTo(["новый.txt", "a.txt"]);
        snap.ShortHeadSha.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RepoSnapshot_НеРепозиторий_ПустойСнапшот()
    {
        var empty = Path.Combine(Path.GetTempPath(), "gitsvc_notrepo_" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(empty);
        Directory.CreateDirectory(empty);

        var snap = await _git.RepoSnapshotAsync(null, empty);
        snap.DirtyPaths.Should().BeEmpty();
        snap.ShortHeadSha.Should().BeNull();
    }

    [Fact]
    public void ParseDirty_РазбираетВсеФорматы_И_ПустойВвод()
    {
        // Переехал из DeployHost: раньше жил там, теперь парсер живёт у источника команды
        var files = GitService.ParseDirty(
            " M frontend/src/lib/design.ts\n?? docs/adr/ADR-010-deploy-from-chat.md\nR  a.txt -> b.txt\n");
        files.Should().BeEquivalentTo(
            ["frontend/src/lib/design.ts", "docs/adr/ADR-010-deploy-from-chat.md", "a.txt -> b.txt"]);

        GitService.ParseDirty("").Should().BeEmpty();
    }

    [Fact]
    public async Task IsAncestor_ПредокВозвращаетTrue_ЧужойВозвращаетFalse()
    {
        // Начальный коммит из InitializeAsync — первый; добавляем второй поверх него
        var first = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b2\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "второй");

        (await _git.IsAncestorAsync(null, _repo, first, "HEAD")).Should().BeTrue();
        (await _git.IsAncestorAsync(null, _repo, "0000000000000000000000000000000000000000", "HEAD"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CommitStat_ИзменённыйФайл_СодержитDiffStat()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "строка один\nстрока два\n");
        await _git.StageAllAsync(null, _repo);
        var sha = await _git.CommitAsync(null, _repo, "правка a.txt");

        var stat = await _git.CommitStatAsync(null, _repo, sha);
        stat.Should().Contain("a.txt");
    }

    [Fact]
    public async Task ParentCount_ОбычныйКоммитВозвращает1_Корневой0_Мерж2()
    {
        // Корневой: отдельный репо с ОДНИМ коммитом
        var fresh = Path.Combine(Path.GetTempPath(), "gitsvc_parentcount_" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(fresh);
        Directory.CreateDirectory(fresh);
        await RawGitIn(fresh, "init", "-b", "main");
        await RawGitIn(fresh, "config", "user.email", "t@t");
        await RawGitIn(fresh, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(fresh, "root.txt"), "корень\n");
        await _git.StageAllAsync(null, fresh);
        var rootSha = await _git.CommitAsync(null, fresh, "root");
        (await _git.ParentCountAsync(null, fresh, rootSha)).Should().Be(0);

        // Обычный поверх корневого
        await File.WriteAllTextAsync(Path.Combine(fresh, "second.txt"), "второй\n");
        await _git.StageAllAsync(null, fresh);
        await _git.CommitAsync(null, fresh, "второй");
        var second = (await _git.RunAsync(null, fresh, ["rev-parse", "HEAD"])).Stdout.Trim();
        (await _git.ParentCountAsync(null, fresh, second)).Should().Be(1);

        // Merge-коммит: запускаем сырой git merge в основном репо
        var headBefore = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();
        await RawGit("checkout", "-b", "feature/parentcount");
        await File.WriteAllTextAsync(Path.Combine(_repo, "feature.txt"), "из ветки\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "правка в feature");
        await _git.CheckoutAsync(null, _repo, "main");
        await RawGit("merge", "--no-ff", "feature/parentcount");
        var mergeSha = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();
        (await _git.ParentCountAsync(null, _repo, mergeSha)).Should().Be(2);
        headBefore.Should().NotBe(mergeSha, "merge-коммит отличается от предыдущего HEAD");
    }

    // ---------- DiffFileVsHeadAsync / LogNumstatRangeAsync (блокер ревью волны 3) ----------
    //
    // До этой правки оба метода ехали в прод без прямых тестов против настоящего git:
    // - GitServiceTests.cs не содержал вызовов ни того, ни другого;
    // - единственный HTTP-тест GetDiff падал на IsGitRepo() == false до DiffFileVsHeadAsync;
    // - DossierRecallTests.FakeRecall подменял ResolveHeadAsync/GitLogNumstatAsync на уровне
    //   protected virtual и реальных LocalTipAsync/LogNumstatRangeAsync не звал.
    // Любая будущая правка git-флагов или регресс .Ok-проверки проходил CI незамеченным —
    // эти тесты закрывают дыру.

    // Tracked-файл с правкой: `git diff HEAD -- path` обязан вернуть непустой unified diff.
    // Содержимое зависит от версии git (заголовки могут отличаться), контракт метода —
    // стабильно отдавать КУСОК диффа, а не null и не пустую строку.
    [Fact]
    public async Task DiffFileVsHead_МодифицированныйTracked_НепустойDiff()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "правка\n");

        var diff = await _git.DiffFileVsHeadAsync(null, _repo, "a.txt");

        diff.Should().NotBeNullOrEmpty();
        diff.Should().Contain("a.txt", "путь обязан присутствовать в заголовке unified diff");
        diff.Should().ContainAny("+правка", "-один", "-два", "-три",
            "хотя бы одна строка помечена как +/- (added/removed)");
    }

    // Фолбэк на `git diff --cached`: сценарий, где working copy == HEAD, но индекс != HEAD
    // (стейджили правку и вернули файл в исходное). `git diff HEAD` пуст → метод должен
    // попасть во вторую ветку и вернуть staged-diff. Без этого UI теряет diff у staged-only
    // файлов.
    [Fact]
    public async Task DiffFileVsHead_ТолькоStaged_ФолбэкНаCached()
    {
        // HEAD: b1 → правим → b2 → stage → правим обратно → b1.
        // Результат: working copy == HEAD, индекс != HEAD.
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b2\n");
        await _git.StageAsync(null, _repo, "b.txt");
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b1\n");

        // Sanity-check: head-diff пуст (working == HEAD), cached-diff не пуст
        var headCheck = await _git.RunAsync(null, _repo, ["diff", "HEAD", "--", "b.txt"]);
        headCheck.Ok.Should().BeTrue();
        headCheck.Stdout.Should().BeNullOrWhiteSpace(
            "working copy == HEAD — обычный diff обязан быть пуст");
        var cachedCheck = await _git.RunAsync(null, _repo, ["diff", "--cached", "--", "b.txt"]);
        cachedCheck.Ok.Should().BeTrue();
        cachedCheck.Stdout.Should().NotBeNullOrWhiteSpace(
            "индекс != HEAD — cached-diff обязан быть не пуст");

        var diff = await _git.DiffFileVsHeadAsync(null, _repo, "b.txt");

        diff.Should().NotBeNullOrEmpty(
            "фолбэк на `git diff --cached` обязан сработать, иначе UI потеряет diff staged-only файла");
        diff.Should().Contain("b.txt");
        diff.Should().Contain("b2", "diff должен показать staged-версию (b2), а не текущее working (b1)");
    }

    // Untracked: файл НЕ в HEAD и НЕ в индексе. Оба diff пусты → метод возвращает null.
    // В отличие от DiffFileAsync (там третий фолбэк на `diff --no-index`), контракт здесь
    // жёстче — null честно говорит «нет изменений в git-плоскости».
    [Fact]
    public async Task DiffFileVsHead_Untracked_ВозвращаетNull()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "новый.txt"), "не в git\n");

        (await _git.StatusAsync(null, _repo)).Untracked
            .Should().ContainSingle(f => f.Path == "новый.txt");

        var diff = await _git.DiffFileVsHeadAsync(null, _repo, "новый.txt");

        diff.Should().BeNull("контракт DiffFileVsHeadAsync для untracked — null");
    }

    // Бинарный файл: заменяем tracked-файл на байты, не проходящие текстовый фильтр.
    // Метод не должен падать; вернуть может либо текст с пометкой «Binary files ... differ»,
    // либо null — обе формы легитимны, главное — отсутствие исключения и мусорных байт.
    [Fact]
    public async Task DiffFileVsHead_БинарныйФайл_НеПадаетИНеМусорит()
    {
        // a.txt уже tracked из InitializeAsync. Переписываем его бинарными байтами.
        await File.WriteAllBytesAsync(Path.Combine(_repo, "a.txt"),
            new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0xAA, 0xBB });

        var diff = await _git.DiffFileVsHeadAsync(null, _repo, "a.txt");

        // Метод обязан либо отдать git-овский «Binary files ...» маркер, либо null.
        // Сырые нулевые байты в выводе — дефект (значит stdout не прошёл фильтр).
        if (diff is not null)
        {
            diff.Should().NotContain("\0",
                "сырые нулевые байты в diff означают, что git не распознал binary и отдал мусор");
            diff.Should().Contain("Binary files",
                "git для бинарей помечает diff как «Binary files ... differ»");
        }
    }

    // Обычный коммит с изменёнными файлами: numstat в формате `<sha>\n<+N>\t<-N>\t<path>`.
    // Метод пробрасывает сырой stdout — контракт на сохранение формата для вызывающего
    // парсера. Проверяем: sha коммита присутствует, для обоих файлов — пары чисел и путь.
    [Fact]
    public async Task LogNumstatRange_ОбычныйКоммит_ОтдаётShaИПарыЧисел()
    {
        var headBefore = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "строка1\nстрока2\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b2\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "правки a и b");

        var numstat = await _git.LogNumstatRangeAsync(null, _repo, headBefore, ["a.txt", "b.txt"]);

        numstat.Should().NotBeNullOrEmpty();
        var sha = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();
        numstat.Should().Contain(sha, "формат: %H на каждый коммит диапазона");
        numstat.Should().MatchRegex(@"\d+\s*\t\s*\d+\s*\ta\.txt",
            "для a.txt обязан быть numstat +N\\t-N\\ta.txt");
        numstat.Should().MatchRegex(@"\d+\s*\t\s*\d+\s*\tb\.txt",
            "для b.txt обязан быть numstat +N\\t-N\\tb.txt");
    }

    // Переименование: git mv + коммит. Без -M git покажет rename как delete + create
    // (две отдельные записи), с rename detection — одной строкой `{a => z}` либо
    // `a => z`. Наш метод пробрасывает stdout как есть, контракт — вызывающий парсер
    // получает упоминание обоих имён. Проверяем, что код не падает и оба имени в выводе.
    // Фильтр пустых pathspec, чтобы rename показался полностью (split delete+create тоже).
    [Fact]
    public async Task LogNumstatRange_Переименование_НеЛомаетВызывающийКод()
    {
        var headBefore = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        await RawGit("mv", "a.txt", "z.txt");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "переименовали a в z");

        // Пустой files — git log выводит все пути коммита (split или rename-формат).
        var numstat = await _git.LogNumstatRangeAsync(null, _repo, headBefore, []);

        numstat.Should().NotBeNullOrEmpty();
        numstat.Should().Contain("a.txt").And.Contain("z.txt",
            "rename обязан содержать оба имени — хоть split delete+create, хоть {a => z}");
    }

    // Бинарный файл в numstat: git не считает строки для бинарников и печатает
    // `-\t-\tpath`. Проверяем, что наш метод пробрасывает этот формат без потерь.
    [Fact]
    public async Task LogNumstatRange_БинарныйФайл_ДаётМинусМинусПуть()
    {
        var headBefore = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        await File.WriteAllBytesAsync(Path.Combine(_repo, "blob.bin"),
            new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xAA });
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "добавили бинарь");

        var numstat = await _git.LogNumstatRangeAsync(null, _repo, headBefore, ["blob.bin"]);

        numstat.Should().NotBeNullOrEmpty();
        numstat.Should().Contain("-\t-\tblob.bin",
            "git numstat для бинарного файла обязан вернуть прочерк-прочерк-путь");
    }
}
