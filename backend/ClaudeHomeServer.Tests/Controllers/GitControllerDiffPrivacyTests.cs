using System.Diagnostics;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// F2-P1 (вердикт финального ревью 892deb00, п.1): читающая ручка git/diff не должна отдавать
/// содержимое чужого private. Для неотслеживаемого файла DiffFileAsync фолбэчит на
/// `git diff --no-index` (GitService.cs:296-300) без путевого гейта, а путь
/// .cc-attachments/private/{author}/… несёт и id автора, и guid вложения: ToPrivate сохраняет
/// guid (PrivateAttachments.cs:43-50), значит он известен каждому, кто видел вложение, пока чат
/// был общим. Гейт — общий канонический предикат (PrivateAttachments.IsForeignResolved), тот же,
/// что держит соседнюю ручку files/diff (FilesController.cs:90): по ФАКТИЧЕСКОМУ корню операции,
/// после резолва дерева, до чтения. Чужесть судится по сегменту closed-поддерева в каноне на
/// любой глубине (DEC-ROOTS 5c8a4705), поэтому и ссылка на корень чужого проекта не раскрывает
/// чужого содержимого — тот же случай, что закрыт для обхода FileService в 92e5873b.
///
/// Легальные пути остаются: своё private, общая раскладка {guid}/…, новый файл проекта, правка
/// трекаемого файла и легальная ссылка наружу на не-private (она не чужое private по обеим линиям).
/// Актор неизвестен (нет sub) — любое private чужое (fail-closed, как IsForeign по пустому id).
///
/// Без хоста: контроллер собирается напрямую с живым GitService на настоящем git CLI (образец —
/// GetOwnedVisibleTests:456-479), вызывающий — ClaimsPrincipal с claim «sub» (тот же источник
/// UserId, что в проде, GitController.cs:22). Предикат судит id автора против id актора и состав
/// участников не читает, поэтому общий проект с участниками фикстуре не нужен — достаточно двух id.
/// </summary>
public sealed class GitControllerDiffPrivacyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectRoot;
    private readonly string _otherRoot;
    private readonly string _projectId;
    private readonly string _actorId;   // вызывающий: автор своего private
    private readonly string _authorId;  // автор чужого private
    private readonly GitController _controller;

    private readonly Guid _foreignGuid = Guid.NewGuid();
    private readonly Guid _ownGuid = Guid.NewGuid();
    private readonly Guid _commonGuid = Guid.NewGuid();
    private readonly Guid _otherForeignGuid = Guid.NewGuid();

    private string ForeignRel => $"{TreeExcludes.AttachmentsDir}/private/{_authorId}/{_foreignGuid:N}/secret.txt";
    private string OwnRel => $"{TreeExcludes.AttachmentsDir}/private/{_actorId}/{_ownGuid:N}/mine.txt";
    private string CommonRel => $"{TreeExcludes.AttachmentsDir}/{_commonGuid:N}/common.txt";
    // Чужое private ЧЕРЕЗ ссылку «other» на корень чужого проекта: первым сегментом имени ссылки
    // текстовая линия чужести не видит — её обязан закрыть канон разыменованного пути
    private string ForeignThroughLink => $"other/{TreeExcludes.AttachmentsDir}/private/{_authorId}/{_otherForeignGuid:N}/other-secret.txt";
    private const string ThroughLinkPlain = "other/docs/plain.txt";
    private const string PlainRel = "docs/plain.txt";
    private const string NewRel = "new.txt";
    private const string SubClaim = "sub"; // тот же claim, из которого контроллер читает UserId (GitController.cs:22)

    public GitControllerDiffPrivacyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git_diff_priv_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_tempDir, "project");
        _otherRoot = Path.Combine(_tempDir, "other-project");
        Directory.CreateDirectory(_projectRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var users = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _actorId = users.GetFirst()!.Id;
        _authorId = users.Add("git-diff-author", "pwd", "user").Id;

        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        _projectId = projects.Create("Проект git-diff", _projectRoot, _actorId, users.GetFirst()!.Username).Id;

        WriteFixtureFiles();

        _controller = new GitController(
            new GitService(TestLauncherFactory.Instance), null!, null!, projects,
            null!, null!, null!, null!, null!);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Репозиторий + раскладка вложений на диске. commited — только docs/plain.txt: всё остальное
    // остаётся неотслеживаемым, ровно тот путь DiffFileAsync, где содержимое и утекает
    private void WriteFixtureFiles()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "docs"));
        RunGit("init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_projectRoot, PlainRel), "основа\n");
        RunGit("-c", "user.name=тест", "-c", "user.email=test@test", "add", PlainRel);
        RunGit("-c", "user.name=тест", "-c", "user.email=test@test", "commit", "-q", "-m", "начальный");
        File.WriteAllText(Path.Combine(_projectRoot, PlainRel), "основа\nправка\n"); // unstaged M
        File.WriteAllText(Path.Combine(_projectRoot, NewRel), "НОВЫЙ\n");            // untracked

        Dir(ForeignRel); File.WriteAllText(Abs(ForeignRel), "ЧУЖОЕ\n");
        Dir(OwnRel); File.WriteAllText(Abs(OwnRel), "СВОЁ\n");
        Dir(CommonRel); File.WriteAllText(Abs(CommonRel), "ОБЩЕЕ\n");

        // «Чужой проект» в общей папке теста + ссылка на его корень из нашего корня
        Directory.CreateDirectory(Path.Combine(_otherRoot, "docs"));
        var otherForeign = Path.Combine(_otherRoot, TreeExcludes.AttachmentsDir, "private", _authorId, _otherForeignGuid.ToString("N"));
        Directory.CreateDirectory(otherForeign);
        File.WriteAllText(Path.Combine(otherForeign, "other-secret.txt"), "ДРУГОЕ\n");
        File.WriteAllText(Path.Combine(_otherRoot, "docs", "plain.txt"), "ЛЕГАЛЬНОЕ\n");
        try { Directory.CreateSymbolicLink(Path.Combine(_projectRoot, "other"), _otherRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Abs(string rel) => Path.Combine(_projectRoot, rel);
    private void Dir(string rel) => Directory.CreateDirectory(Path.GetDirectoryName(Abs(rel))!);

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _projectRoot,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000)) throw new InvalidOperationException("git не завершился: " + string.Join(' ', args));
        if (proc.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }

    // Вызов ручки от лица актора (null — без sub: актор неизвестен). Гейт читает UserId из этого же
    // principal, что и прод.
    private Task<IActionResult> DiffAsync(string path, string? actorId, bool staged = false)
    {
        var http = new DefaultHttpContext();
        var claims = new List<Claim>();
        if (actorId is not null) claims.Add(new Claim(SubClaim, actorId));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims));
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        return _controller.Diff(_projectId, path, staged);
    }

    private static string OkDiff(IActionResult r) =>
        r.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DiffResponse>().Subject.Diff!;

    [Fact]
    public async Task Diff_ЧужоеPrivate_Отказ_СвоёPrivateОбщееИОбычныеФайлыОтдаютDiff()
    {
        (await DiffAsync(ForeignRel, _actorId)).Should().BeOfType<NotFoundResult>(
            "чужое private для участника не существует: содержимое не отдаётся, существования не раскрываем");
        // Та же отказанность на staged-ветке: путь чужой независимо от того, какой diff просят
        (await DiffAsync(ForeignRel, _actorId, staged: true)).Should().BeOfType<NotFoundResult>();

        // Легальное не тронуто: своё private, общая раскладка {guid}/…, новый файл, правка трекаемого
        OkDiff(await DiffAsync(OwnRel, _actorId)).Should().Contain("СВОЁ");
        OkDiff(await DiffAsync(CommonRel, _actorId)).Should().Contain("ОБЩЕЕ");
        OkDiff(await DiffAsync(NewRel, _actorId)).Should().Contain("НОВЫЙ");
        OkDiff(await DiffAsync(PlainRel, _actorId)).Should().Contain("+правка");

        // Автор своё видит и в чужом проекте (id автора = актор): чужесть судится по id, не по проекту
        var otherAuthorsOwn = $"other/{TreeExcludes.AttachmentsDir}/private/{_authorId}/{_otherForeignGuid:N}/other-secret.txt";
        OkDiff(await DiffAsync(otherAuthorsOwn, _authorId)).Should().Contain("ДРУГОЕ");
    }

    /// <summary>
    /// Тот же случай, что закрыт в 92e5873b для обхода FileService: ссылка в корне на каталог,
    /// СОДЕРЖАЩИЙ контейнер .cc-attachments (корень чужого проекта). Имя ссылки — первый сегмент,
    /// текстовая линия чужести его не видит; закрывает канон разыменованного пути (первая линия
    /// IsForeignResolved). Легальная ссылка наружу на не-private остаётся: она не чужое private
    /// ни по одной линии (guard «СсылкаНаружу…» в PrivateAttachmentsResolvedTests).
    /// </summary>
    /// <Named мутации: «гейт опущен» (ForeignPrivateDenied в GitController.Diff) краснит этот тест:
    /// через ссылку возвращается diff с содержимым чужого private.</Named>
    [Fact]
    public async Task Diff_СсылкаНаКореньЧужогоПроекта_ЧужоеНеРаскрывается_ЛегальнаяСсылкаНаружуОтдана()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий (тот же приём, что в FileServicePrivateAttachmentsLinkTests)
        Directory.Exists(Path.Combine(_projectRoot, "other")).Should().BeTrue("арранж: ссылка на корень чужого проекта создана");

        (await DiffAsync(ForeignThroughLink, _actorId)).Should().BeOfType<NotFoundResult>(
            "ссылка разыменовывается в канон с сегментом чужого private — содержимое не отдаётся");
        // Тот же отказ по своему имени чужого private в этом же дереве
        (await DiffAsync(ForeignRel, _actorId)).Should().BeOfType<NotFoundResult>();

        OkDiff(await DiffAsync(ThroughLinkPlain, _actorId)).Should().Contain("ЛЕГАЛЬНОЕ",
            "обычное содержимое чужого проекта — легальная ссылка наружу, она открыта и раньше, и теперь");
        OkDiff(await DiffAsync(OwnRel, _actorId)).Should().Contain("СВОЁ");
    }

    [Fact]
    public async Task Diff_АкторНеизвестен_ЛюбоеPrivateОтказано_ОбщаяРаскладкаИОбычныеФайлыОтданы()
    {
        (await DiffAsync(OwnRel, null)).Should().BeOfType<NotFoundResult>(
            "без актора «своего» нет: любое private чужое (fail-closed, как IsForeign по пустому id)");
        (await DiffAsync(ForeignRel, null)).Should().BeOfType<NotFoundResult>();
        OkDiff(await DiffAsync(CommonRel, null)).Should().Contain("ОБЩЕЕ",
            "общая раскладка {guid}/… — не closed-поддерево: ей этот гейт не ставился");
        OkDiff(await DiffAsync(NewRel, null)).Should().Contain("НОВЫЙ");
    }
}
