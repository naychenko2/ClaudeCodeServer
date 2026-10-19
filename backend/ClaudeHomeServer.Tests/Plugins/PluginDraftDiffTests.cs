using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Сравнение черновика с ПОСЛЕДНЕЙ установленной версией того же id (ADR-031, шаг 4.3b):
//   - нет установленной версии → «новый плагин»: статус new, список файлов черновика;
//   - иначе — файлы added/removed/modified; текстовый изменённый → построчный diff
//     (потолок 200 строк на файл, 1000 суммарно → truncated); бинарный/больше 256 КБ →
//     «изменён, N байт» без содержимого; манифест — права/секреты (добавлены/убраны/сменился
//     признак «обязательный»); в ответе только относительные пути, без хостовых.
//
// Гейты (те же, что у 4.3a): сервисный JWT → 403, чужой/несуществующий → 404.
public class PluginDraftDiffTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Stranger = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginDraftDiff _diff;

    public PluginDraftDiffTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_draftdiff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _diff = new PluginDraftDiff(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // ── Хелперы ────────────────────────────────────────────────────────────────

    private static string SlugOf(string id) => id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

    // Манифест (ccs-plugin.json + нативный .claude-plugin/plugin.json) в каталоге пакета.
    private static void WriteManifest(string root, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets)
    {
        var slug = SlugOf(id);
        var manifest = new
        {
            id,
            name = slug,
            version,
            contributes = new
            {
                skills = new[] { "skills/a" },
                requires = secrets is null
                    ? null
                    : new { secrets = secrets.Select(s => new { name = s.Item1, required = s.Item2 }) },
            },
            permissions = new { requested },
        };
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteTextFile(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void WriteBinaryFile(string root, string rel, byte[] content)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    // Черновик: _drafts/{slug} с манифестом + файлами (rel → текст).
    private void CreateDraft(string owner, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets, Dictionary<string, string> files)
    {
        var root = _store.DraftDir(owner, SlugOf(id));
        WriteManifest(root, id, version, requested, secrets);
        foreach (var (rel, content) in files)
            WriteTextFile(root, rel, content);
    }

    // Установленный пакет: packages/{id}/{version} с манифестом + файлами + запись installed.json.
    private void CreateInstalled(string owner, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets, Dictionary<string, string> files)
    {
        var root = _store.PackageDir(owner, id, version);
        WriteManifest(root, id, version, requested, secrets);
        foreach (var (rel, content) in files)
            WriteTextFile(root, rel, content);
        _store.Update(owner, recs =>
        {
            recs.RemoveAll(r => r.Id == id);
            recs.Add(new PluginInstall
            {
                Id = id,
                Version = version,
                Sha256 = "sha-" + version,
                Requested = requested,
                Granted = [],
                Enabled = true,
                Source = "draft:" + SlugOf(id),
                Secrets = (secrets ?? []).Select(s => new PluginInstallSecret { Name = s.Name, Set = true }).ToList(),
            });
            return recs;
        });
    }

    // Контроллер (для гейтов 403/404) — тот же паттерн, что в PluginDraftsControllerTests.
    private PluginDraftsController NewController(string userId, bool serviceToken = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = true, PluginsBuildYourself = true };
        var ctl = new PluginDraftsController(config, gate, _store,
            new PluginDraftService(_store, new PluginPackageValidator()),
            new PluginInstaller(_store, new PluginPackageValidator()),
            _diff);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, serviceToken) },
        };
        return ctl;
    }

    private static ClaimsPrincipal Principal(string userId, bool serviceToken)
    {
        var claims = new List<Claim> { new("sub", userId) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        return new(new ClaimsIdentity(claims, "test"));
    }

    // ── Новый плагин: нет установленной версии ──────────────────────────────────

    [Fact]
    public void Diff_НетУстановленной_НовыйПлагин_СписокФайлов()
    {
        CreateDraft(Owner, "local-a/pkg", "1.0.0", ["notes:read"], null,
            new() { ["skills/a/SKILL.md"] = "# A\n", ["commands/b.md"] = "# B\n" });

        var res = _diff.Diff(Owner, "pkg");

        res.Status.Should().Be("new");
        res.InstalledVersion.Should().BeNull("установленной версии нет");
        res.Manifest.Should().BeNull("сравнивать не с чем");
        res.Removed.Should().BeEmpty();
        res.Modified.Should().BeEmpty();
        // Файлы черновика — в Added (всё «новое» относительно пустого пакета).
        res.Added.Select(f => f.Path).Should().Contain("skills/a/SKILL.md");
        res.Added.Select(f => f.Path).Should().Contain("commands/b.md");
    }

    // ── Файлы: добавлен / удалён / изменён ──────────────────────────────────────

    [Fact]
    public void Diff_Файлы_ДобавленУдалёнИзменён()
    {
        // Установленный v1.0.0: A, B, C.
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", ["notes:read"], null,
            new() { ["A.txt"] = "a1\na2\n", ["B.txt"] = "b\n", ["C.txt"] = "c\n" });
        // Черновик v2.0.0: A изменён, C тот же, D новый; B удалён.
        CreateDraft(Owner, "local-a/pkg", "2.0.0", ["notes:read"], null,
            new() { ["A.txt"] = "a1\na2\na3\n", ["C.txt"] = "c\n", ["D.txt"] = "d\n" });

        var res = _diff.Diff(Owner, "pkg");

        res.Status.Should().Be("updated");
        res.InstalledVersion.Should().Be("1.0.0");

        res.Added.Select(f => f.Path).Should().Contain("D.txt", "новый файл — added");
        res.Removed.Select(f => f.Path).Should().Contain("B.txt", "удалённый файл — removed");

        var modA = res.Modified.Single(f => f.Path == "A.txt");
        modA.Binary.Should().BeFalse();
        modA.Lines.Should().NotBeNull("текстовый изменённый — с построчным diff");
        modA.Lines!.Should().Contain(l => l.Kind == "add" && l.Text == "a3", "добавленная строка в diff");

        // C.txt не изменился → ни в одном списке.
        res.Added.Select(f => f.Path).Should().NotContain("C.txt");
        res.Removed.Select(f => f.Path).Should().NotContain("C.txt");
        res.Modified.Select(f => f.Path).Should().NotContain("C.txt");
    }

    // ── Потолок строк: 200 на файл ──────────────────────────────────────────────

    [Fact]
    public void Diff_ПотолокСтрокНаФайл_200_Truncated()
    {
        // Старый: 10 строк; новый: 250 других строк → diff = 10 del + 250 add = 260 > 200.
        var oldContent = string.Join("", Enumerable.Range(1, 10).Select(i => $"old-{i}\n"));
        var newContent = string.Join("", Enumerable.Range(1, 250).Select(i => $"new-{i}\n"));
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", [], null, new() { ["A.txt"] = oldContent });
        CreateDraft(Owner, "local-a/pkg", "2.0.0", [], null, new() { ["A.txt"] = newContent });

        var res = _diff.Diff(Owner, "pkg");

        var modA = res.Modified.Single(f => f.Path == "A.txt");
        modA.Truncated.Should().BeTrue("diff больше 200 строк → усечён");
        modA.Lines!.Count.Should().Be(PluginDraftDiff.MaxDiffLinesPerFile, "потолок 200 строк на файл");
    }

    // ── Потолок строк: 1000 суммарно ─────────────────────────────────────────────

    [Fact]
    public void Diff_ПотолокСтрокСуммарно_1000_Truncated()
    {
        // 6 файлов, каждый с diff ровно 200 строк (100 del + 100 add, все разные;
        // потолок 200 на файл НЕ превышен): суммарно 6×200 = 1200 > 1000 → общий truncated.
        var oldFiles = new Dictionary<string, string>();
        var newFiles = new Dictionary<string, string>();
        for (int i = 0; i < 6; i++)
        {
            var oldContent = string.Join("", Enumerable.Range(1, 100).Select(n => $"f{i}-old-{n}\n"));
            var newContent = string.Join("", Enumerable.Range(1, 100).Select(n => $"f{i}-new-{n}\n"));
            oldFiles[$"F{i}.txt"] = oldContent;
            newFiles[$"F{i}.txt"] = newContent;
        }
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", [], null, oldFiles);
        CreateDraft(Owner, "local-a/pkg", "2.0.0", [], null, newFiles);

        var res = _diff.Diff(Owner, "pkg");

        res.Truncated.Should().BeTrue("суммарный diff > 1000 строк → truncated");
        // Сумма строк diff по всем файлам ≤ 1000.
        int total = res.Modified.Sum(f => f.Lines?.Count ?? 0);
        total.Should().BeLessOrEqualTo(PluginDraftDiff.MaxDiffLinesTotal, "суммарный потолок 1000");
    }

    // ── Бинарный файл: «изменён, N байт», без содержимого ───────────────────────

    [Fact]
    public void Diff_БинарныйФайл_БезСодержимого()
    {
        // Старый: текстовый; новый: бинарный (NUL-байт) → «изменён, N байт», Lines=null.
        var binary = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00 };
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", [], null, new() { ["img.bin"] = "old text\n" });
        var draftRoot = _store.DraftDir(Owner, "pkg");
        WriteManifest(draftRoot, "local-a/pkg", "2.0.0", [], null);
        WriteTextFile(draftRoot, "img.bin", "old text\n");
        WriteBinaryFile(draftRoot, "img.bin", binary);

        var res = _diff.Diff(Owner, "pkg");

        var modImg = res.Modified.Single(f => f.Path == "img.bin");
        modImg.Binary.Should().BeTrue("бинарный файл — без содержимого");
        modImg.Lines.Should().BeNull("бинарный — только «изменён, N байт»");
        modImg.Size.Should().Be(binary.Length, "размер в байтах");
    }

    // ── Крупный файл (> 256 КБ): «изменён, N байт», без содержимого ─────────────

    [Fact]
    public void Diff_КрупныйФайл_БезСодержимого()
    {
        // Текстовый, но больше 256 КБ → без содержимого (как бинарный).
        var bigOld = new string('a', 256 * 1024 + 10);
        var bigNew = new string('b', 256 * 1024 + 20);
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", [], null, new() { ["big.txt"] = bigOld });
        CreateDraft(Owner, "local-a/pkg", "2.0.0", [], null, new() { ["big.txt"] = bigNew });

        var res = _diff.Diff(Owner, "pkg");

        var modBig = res.Modified.Single(f => f.Path == "big.txt");
        modBig.Binary.Should().BeTrue("крупный файл — без содержимого");
        modBig.Lines.Should().BeNull();
        modBig.Size.Should().Be(bigNew.Length, "размер в байтах");
    }

    // ── Манифест: права и секреты ───────────────────────────────────────────────

    [Fact]
    public void Diff_Манифест_ПраваДобавленыУбраны()
    {
        // Установленный: requested = [notes:read, tasks:read].
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", ["notes:read", "tasks:read"], null,
            new() { ["A.txt"] = "x\n" });
        // Черновик: requested = [notes:read, net:api.linear.app] → +net, −tasks.
        CreateDraft(Owner, "local-a/pkg", "2.0.0", ["notes:read", "net:api.linear.app"], null,
            new() { ["A.txt"] = "x\n" });

        var res = _diff.Diff(Owner, "pkg");

        res.Manifest.Should().NotBeNull();
        res.Manifest!.PermissionsAdded.Should().Contain("net:api.linear.app", "новое запрошенное право");
        res.Manifest.PermissionsRemoved.Should().Contain("tasks:read", "убранное запрошенное право");
        res.Manifest.PermissionsAdded.Should().NotContain("notes:read", "неизменное право не в added");
    }

    [Fact]
    public void Diff_Манифест_СекретыДобавленУбранСменилсяRequired()
    {
        // Установленный: secrets = [LINEAR_TOKEN(required=true), OLD_TOKEN(required=true)].
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", ["secret:LINEAR_TOKEN", "secret:OLD_TOKEN"],
            secrets: [("LINEAR_TOKEN", true), ("OLD_TOKEN", true)],
            files: new() { ["A.txt"] = "x\n" });
        // Черновик: secrets = [LINEAR_TOKEN(required=false), NEW_TOKEN(required=true)]
        // → OLD_TOKEN убран, NEW_TOKEN добавлен, LINEAR_TOKEN сменил required (true→false).
        CreateDraft(Owner, "local-a/pkg", "2.0.0", ["secret:LINEAR_TOKEN", "secret:NEW_TOKEN"],
            secrets: [("LINEAR_TOKEN", false), ("NEW_TOKEN", true)],
            files: new() { ["A.txt"] = "x\n" });

        var res = _diff.Diff(Owner, "pkg");

        res.Manifest!.SecretsAdded.Should().Contain(s => s.Name == "NEW_TOKEN", "новый секрет");
        res.Manifest.SecretsRemoved.Should().Contain(s => s.Name == "OLD_TOKEN", "убранный секрет");
        res.Manifest.SecretsRequiredChanged.Should().Contain(s => s.Name == "LINEAR_TOKEN", "сменился признак «обязательный»");
        // LINEAR_TOKEN не «добавлен» (он в обоих).
        res.Manifest.SecretsAdded.Should().NotContain(s => s.Name == "LINEAR_TOKEN");
    }

    // ── В ответе нет хостовых (абсолютных) путей ────────────────────────────────

    [Fact]
    public void Diff_ВОтветеНетАбсолютныхПутей()
    {
        CreateInstalled(Owner, "local-a/pkg", "1.0.0", [], null, new() { ["A.txt"] = "a\n" });
        CreateDraft(Owner, "local-a/pkg", "2.0.0", [], null, new() { ["A.txt"] = "a\nb\n", ["B.txt"] = "b\n" });

        var res = _diff.Diff(Owner, "pkg");
        var json = JsonSerializer.Serialize(res);

        // _dir — абсолютный путь временного каталога; он не должен попасть в ответ.
        json.Should().NotContain(_dir, "в ответе только относительные пути внутри пакета");
        // Все пути файлов — относительные (без ведущего разделителя и без ':').
        var allPaths = res.Added.Concat(res.Removed).Concat(res.Modified).Select(f => f.Path);
        allPaths.Should().OnlyContain(p => !Path.IsPathRooted(p) && !p.Contains(':'),
            "каждый путь — относительный, без диска/корня");
    }

    // ── Гейты: 403 сервисному, 404 чужому ───────────────────────────────────────

    [Fact]
    public void Diff_СервисныйJwt_Отдаёт403()
    {
        CreateDraft(Owner, "local-a/pkg", "1.0.0", ["notes:read"], null, new() { ["A.txt"] = "a\n" });
        var ctl = NewController(Owner, serviceToken: true);
        var res = ctl.Diff("pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Diff_ЧужойВладелец_Отдаёт404()
    {
        CreateDraft(Owner, "local-a/pkg", "1.0.0", ["notes:read"], null, new() { ["A.txt"] = "a\n" });
        var ctl = NewController(Stranger);
        var res = ctl.Diff("pkg");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Diff_НесуществующийЧерновик_Отдаёт404()
    {
        var ctl = NewController(Owner);
        var res = ctl.Diff("ghost");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Тестовая заглушка флага (тот же паттерн, что в PluginDraftsControllerTests) ──

    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool PluginsBuildYourself;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp ? PluginsMvp
            : key == FeatureFlagKeys.PluginsBuildYourself ? PluginsBuildYourself
            : false;
    }
}
