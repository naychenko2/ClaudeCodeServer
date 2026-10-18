using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Ядро установки (ADR-031 §2.3, шаг 1.4, C1/C2/M5/M6) — unit-тесты PluginInstaller
// (без контроллера): атомарность (staging + move), TOCTOU-замена каталога, no-op повтора,
// метка источника. Пакет кладётся в каталог черновика (draft:<slug>) — единственный
// читаемый источник в MVP.
public class PluginInstallerTests : IDisposable
{
    private const string Owner = "owner-a";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginInstaller _sut;

    public PluginInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_inst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _sut = new PluginInstaller(_store, new PluginPackageValidator());
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

    // Пакет в каталоге черновика: ccs-plugin.json (id/version) + файлы skills.
    // Возвращает метку источника (draft:<slug>).
    private string CreateDraft(string id, string version, params string[] extraFiles)
    {
        var slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        foreach (var f in extraFiles)
        {
            var p = Path.Combine(root, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "content of " + f + "\n");
        }

        var manifest = new { id, name = "Pkg", version, contributes = new { skills = new[] { "skills/a" } }, permissions = new { requested = new[] { "notes:read" } } };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // --- M5: атомарность — копирование во временный .staging-{guid}, затем move ---

    [Fact]
    public void Install_Атомарность_КопируетВStaging_ИПеремещает()
    {
        var source = CreateDraft("local-a/pkg", "1.0.0");
        var result = _sut.Install(Owner, source);

        result.Status.Should().Be(InstallStatus.Ok);
        var targetDir = _store.PackageDir(Owner, "local-a/pkg", "1.0.0");
        Directory.Exists(targetDir).Should().BeTrue("пакет перемещён в packages/{id}/{version}");
        // Временный .staging-{guid} в корне packages/ убран после Directory.Move.
        var packagesRoot = Path.GetDirectoryName(targetDir)!;
        Directory.EnumerateDirectories(packagesRoot, ".staging-*").Should().BeEmpty(
            "временный каталог .staging-{guid} не остаётся после установки");
    }

    // --- M5/TOCTOU: атомарная замена — старый каталог не «дописывается», а заменяется целиком ---

    [Fact]
    public void Install_Атомарность_СтарыйКаталогЗаменёнЦеликом()
    {
        // Предсуществующий каталог пакета (остаток прошлой установки) с «чужим» файлом.
        var targetDir = _store.PackageDir(Owner, "local-a/pkg", "1.0.0");
        Directory.CreateDirectory(Path.Combine(targetDir, "panel"));
        File.WriteAllText(Path.Combine(targetDir, "panel", "old.txt"), "old\n");

        // Свежая установка (нет записи в installed.json) пакетом, где panel/old.txt НЕТ.
        // Атомарная замена (staging + Directory.Move) — old.txt исчезает; прямое
        // «дописывание» поверх старого каталога (staging убран) — old.txt «выживает».
        var source = CreateDraft("local-a/pkg", "1.0.0");
        var result = _sut.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Ok);

        File.Exists(Path.Combine(targetDir, "skills", "a", "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "panel", "old.txt")).Should().BeFalse(
            "атомарная замена: файл, отсутствующий в новом пакете, не «выживает» из старого");
    }

    // --- M6: повторная установка (совпал sha) — no-op, Granted/Secrets не сбрасываются ---

    [Fact]
    public void Install_Повтор_СовпалSha_NoOp_СохраняетGrantedИСекреты()
    {
        var source = CreateDraft("local-a/pkg", "1.0.0");
        _sut.Install(Owner, source).Status.Should().Be(InstallStatus.Ok);

        // Выдать права и секрет (имитация grant) — они живут в записи.
        _store.Update(Owner, records =>
        {
            var r = records.Single(x => x.Id == "local-a/pkg");
            r.Granted = ["notes:read"];
            r.Secrets = [new PluginInstallSecret { Name = "LINEAR_TOKEN", Set = true }];
            return records;
        });

        // Повторная установка того же пакета (sha не изменился) — no-op.
        var second = _sut.Install(Owner, source);
        second.Status.Should().Be(InstallStatus.NoOp, "повторная установка (совпал sha) — no-op");

        var record = _store.LoadInstalled(Owner).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted не сброшен повторной установкой");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "секреты не сброшены повторной установкой");
    }

    // --- C1: метка источника — белый список ---

    [Fact]
    public void Install_ПроизвольныйПуть_ОтдаётFail()
    {
        var result = _sut.Install(Owner, "/home/user/pkg");
        result.Status.Should().Be(InstallStatus.Fail);
        result.Error!.Should().Contain("белого списка");
    }

    [Fact]
    public void Install_КаталогНеНайден_ОтдаётFail()
    {
        // Метка draft:ghost — каталога черновика нет.
        var result = _sut.Install(Owner, "draft:ghost");
        result.Status.Should().Be(InstallStatus.Fail);
        result.Error!.Should().Contain("не найден");
    }

    // --- minor 2: draft:«.» — slug-грамматика (как у PluginId) отбивает «.» → 400 с
    // понятной причиной (раньше «.» проходила и резолвилась в сам каталог _drafts) ---

    [Fact]
    public void Install_MetkaDraftTochka_ОтдаётFail()
    {
        // «draft:.» — параметр «.» не по грамматике slug [a-z0-9-]+ → 400 (Fail),
        // а не 500 от SafePath.Join (Path.GetFullPath нормализует «.» в сам каталог).
        var result = _sut.Install(Owner, "draft:.");
        result.Status.Should().Be(InstallStatus.Fail);
        result.Error!.Should().Contain("Недопустимый параметр метки источника");
    }

    // --- TOCTOU: запись (id) и пакет (файлы, sha256) не разходятся: манифест читается из
    // staging-копии (того, что перемещается в target), не из источника.
    //
    // Детерминированная гонка: фон-поток меняет id в ccs-plugin.json источника на OtherId.
    //   FIX  (copy → read из staging): staging — снапшот; запись и пакет берут id из одного
    //         снапшота → совпадают (и запись, и пакет имеют тот же id).
    //   МУТАЦИЯ (read из source → copy): read и copy — по разные стороны изменения → запись
    //         (id из read) и пакет (id из copy) разъехались → id в записи ≠ id в пакете.
    // Т.е. мутация даёт расхождение id между записью и перемещённым пакетом → тест ловит. ---

    [Fact]
    public void TOCTOU_МанифестИзStaging_ЗаписьСовпадаетСПакетом()
    {
        const string OrigId = "local-a/pkg";
        const string OtherId = "local-a/other";
        var source = CreateDraft(OrigId, "1.0.0");
        var manifestPath = Path.Combine(_store.DraftDir(Owner, "pkg"), "ccs-plugin.json");

        // Фон-поток: в плотном цикле чередует id в манифесте источника (OrigId ↔ OtherId)
        // атомарной записью (tmp + rename), чтобы изменение гарантированно попало в окно между
        // read и copy (гонка TOCTOU): read и copy увидят разные значения.
        // Два предсериализованных варианта манифеста (id=OrigId / id=OtherId): цикл
        // просто чередует их запись (write + rename), без чтения — максимально быстро.
        string MakeManifestJson(string id) => JsonSerializer.Serialize(new
        {
            id, name = "Pkg", version = "1.0.0",
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested = new[] { "notes:read" } },
        }, new JsonSerializerOptions { WriteIndented = true });
        var jsonOrig = MakeManifestJson(OrigId);
        var jsonOther = MakeManifestJson(OtherId);

        // .tmp-файл — в отдельном каталоге (не в source), чтобы CopyDirectoryNoSymlinks
        // не увидел его при перечислении source (гонка: rename ещё не завершён).
        var tmpDir = Path.Combine(Path.GetTempPath(), "ccs_toctou_tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var stop = new ManualResetEvent(initialState: false);
        var writer = new Thread(() =>
        {
            var toOther = true;
            while (true)
            {
                // Плотный цикл: чередовать id как можно чаще (write + rename), чтобы
                // изменение гарантированно попало в окно между copy и read (гонка TOCTOU).
                // stop.WaitOne(0) — неблокирующая проверка (возвращает сразу), не замедляет цикл.
                if (stop.WaitOne(0)) return;
                try
                {
                    var tmp = Path.Combine(tmpDir, "ccs-plugin.json");
                    File.WriteAllText(tmp, toOther ? jsonOther : jsonOrig);
                    File.Move(tmp, manifestPath, overwrite: true); // замена (не атомарно между каталогами — ок для теста)
                }
                catch { return; }
                toOther = !toOther;
            }
        }) { IsBackground = true };
        writer.Start();

        try
        {
            var result = _sut.Install(Owner, source);
            result.Status.Should().Be(InstallStatus.Ok);
        }
        finally
        {
            stop.Set();
            writer.Join(2000);
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }

        // Запись (id из манифеста) и пакет (файлы, sha256) не разойдутся.
        var record = _store.LoadInstalled(Owner).Single();
        var targetDir = _store.PackageDir(Owner, record.Id, record.Version);
        var (targetManifest, _) = PluginInstaller.ReadManifest(targetDir);
        targetManifest.Should().NotBeNull("манифест перемещён вместе с пакетом");
        targetManifest!.Id.Should().Be(record.Id,
            "id из перемещённого пакета = id в записи (TOCTOU: оба из одного снапшота)");
    }

    [Fact]
    public void Install_SourceПисетсяКакМетка()
    {
        var source = CreateDraft("local-a/pkg", "1.0.0");
        _sut.Install(Owner, source).Status.Should().Be(InstallStatus.Ok);

        var record = _store.LoadInstalled(Owner).Single(r => r.Id == "local-a/pkg");
        record.Source.Should().Be("draft:pkg", "source в installed.json — метка, не путь");
    }

    // --- C2: granted = [] при установке, requested — из манифеста ---

    [Fact]
    public void Install_GrantedПусто_RequestedИзМанифеста()
    {
        var source = CreateDraft("local-a/pkg", "1.0.0");
        _sut.Install(Owner, source).Status.Should().Be(InstallStatus.Ok);

        var record = _store.LoadInstalled(Owner).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().BeEmpty("granted при установке пуст (C2)");
        record.Requested.Should().Equal("notes:read");
        record.Enabled.Should().BeTrue();
    }

    // --- M5: symlink в пакете — CopyDirectoryNoSymlinks сохраняет ссылку, валидация КОПИИ
    // (staging) видит no-symlink → установка не проходит (старый CopyDirectory следовал в
    // target и копировал его содержимое как обычный файл). ---

    [Fact]
    public void Install_СсылкаВПакете_ВалидацияКопии_ОтбиваетNoSymlink()
    {
        var slug = "pkg";
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        // Реальный файл вне пакета + symlink-файл на него внутри пакета (panel/** — allow-list).
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-" + Path.GetRandomFileName() + ".txt");
        File.WriteAllText(outside, "outside content");
        File.CreateSymbolicLink(Path.Combine(root, "panel", "link.txt"), outside);
        var manifest = new
        {
            id = "local-a/pkg", name = "Pkg", version = "1.0.0",
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested = new[] { "notes:read" } },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var result = _sut.Install(Owner, "draft:" + slug);
        result.Status.Should().Be(InstallStatus.Fail, "symlink в копии → no-symlink");
        result.Problems.Should().Contain(p => p.Code == "no-symlink");
    }

    // --- non-blocker: symlink-манифест (plugin.json → чужой файл) — 400 без утечки
    // содержимого чужого файла. Манифест читается ПОСЛЕ валидатора: no-symlink отбивает
    // symlink-манифест до ReadManifest, и JsonException с куском чужого файла (старый
    // порядок: read → copy) не уходит в ответ. ---

    [Fact]
    public void SymlinkManifest_Утечка_ЧужогоСодержимого()
    {
        const string Secret = "SECRET-FOREIGN-CONTENT-XYZ-12345";
        var slug = "pkg";
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");

        // Чужой файл (вне пакета) с характерным содержимым + symlink plugin.json → на него.
        var foreign = Path.Combine(Path.GetTempPath(), "ccs_foreign_" + Path.GetRandomFileName() + ".txt");
        File.WriteAllText(foreign, Secret);
        File.CreateSymbolicLink(Path.Combine(root, "plugin.json"), foreign);

        try
        {
            var result = _sut.Install(Owner, "draft:" + slug);
            // no-symlink отбивает symlink-манифест (FailValidation), не Fail от ReadManifest.
            result.Status.Should().Be(InstallStatus.Fail, "symlink-манифест → no-symlink");
            result.Problems.Should().Contain(p => p.Code == "no-symlink");

            // Утечка: содержимое чужого файла не в тексте ошибки и не в проблемах.
            var leaked = new List<string>();
            if (result.Error is not null) leaked.Add(result.Error);
            foreach (var p in result.Problems ?? [])
                leaked.Add(p.Message);
            leaked.Should().NotContain(s => s.Contains(Secret),
                "содержимое чужого файла (symlink-манифест) не уходит в ответ 400");
        }
        finally
        {
            try { File.Delete(foreign); } catch { }
        }
    }

    // --- manifest.Validate() при установке: неизвестное право → 400 (Fail) ---

    [Fact]
    public void Install_НеизвестноеПравоВМанифесте_ОтдаётFail()
    {
        var slug = "pkg";
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        var manifest = new
        {
            id = "local-a/pkg", name = "Pkg", version = "1.0.0",
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested = new[] { "unknown:perm" } },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var result = _sut.Install(Owner, "draft:" + slug);
        result.Status.Should().Be(InstallStatus.Fail, "manifest.Validate(): неизвестное право");
        result.Problems.Should().Contain(p => p.Code == "unknown-permission");
    }

    // --- manifest.Validate() при установке: net:bad/host (слэш вне класса хоста) → 400 ---

    [Fact]
    public void Install_NetBadHostВМанифесте_ОтдаётFail()
    {
        var slug = "pkg";
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        var manifest = new
        {
            id = "local-a/pkg", name = "Pkg", version = "1.0.0",
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested = new[] { "net:bad/host" } },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var result = _sut.Install(Owner, "draft:" + slug);
        result.Status.Should().Be(InstallStatus.Fail, "manifest.Validate(): net:bad/host — битый хост");
        result.Problems.Should().Contain(p => p.Code == "unknown-permission");
    }
}
