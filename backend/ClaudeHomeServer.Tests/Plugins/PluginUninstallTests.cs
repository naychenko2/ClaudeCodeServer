using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Каскадное удаление установки плагина (ADR-031 §2.3, §4, шаг 1.4): один вызов
// PluginUninstall.Uninstall убирает пакет, запись installed.json, черновик, выданные
// права и секреты, и снимает «внешние» ссылки через шов IPluginInstallCascade.
// Тест проверяет КАЖДЫЙ пункт каскада (критерий готовности).
public class PluginUninstallTests : IDisposable
{
    private const string Owner = "owner-a";
    // id — выведенный (DeriveId(Owner, slug)): сверка черновика при uninstall идёт по
    // выведенному id (QA-фикс O минор 3), поэтому тестовый id = выведенный, а не фейковый.
    private static readonly string Id = PluginIdentity.DeriveId(Owner, "pkg");
    private const string Version = "1.0.0";
    private const string Slug = "pkg"; // хвост id после '/'

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly StubCascade _cascade;
    private readonly PluginUninstall _sut;

    public PluginUninstallTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_uninst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _secrets = new PluginSecretsStore(BuildConfig(_dir));
        _cascade = new StubCascade();
        _sut = new PluginUninstall(_store, _secrets, _cascade);
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

    // Разложить всё, что установка занимает: пакет, черновик, запись с granted + секретами.
    private void Setup()
    {
        // 1) Пакет: packages/{id}/{version}/
        var pkg = _store.PackageDir(Owner, Id, Version);
        Directory.CreateDirectory(Path.Combine(pkg, "skills", "a"));
        File.WriteAllText(Path.Combine(pkg, "skills", "a", "SKILL.md"), "# A\n");

        // 2) Черновик: _drafts/{slug}/
        var draft = _store.DraftDir(Owner, Slug);
        Directory.CreateDirectory(draft);
        File.WriteAllText(Path.Combine(draft, "SKILL.md"), "# draft\n");

        // 3) Запись installed.json: granted + секреты.
        _store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = Id,
                Version = Version,
                Sha256 = "abc123",
                Requested = ["notes:read", "net:api.linear.app"],
                Granted = ["notes:read"],
                Enabled = true,
                Source = "draft",
                Secrets = [new PluginInstallSecret { Name = "LINEAR_TOKEN", Set = true }],
            },
        ]);
    }

    // --- Каскад: каждый пункт ---

    [Fact]
    public void Uninstall_УдаляетКаталогПакета()
    {
        Setup();
        var pkg = _store.PackageDir(Owner, Id, Version);
        Directory.Exists(pkg).Should().BeTrue();

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        Directory.Exists(pkg).Should().BeFalse("пакет packages/{id}/{version}/ удалён");
    }

    [Fact]
    public void Uninstall_УдаляетЗаписьInstalledJson()
    {
        Setup();

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        _store.LoadInstalled(Owner).Should().BeEmpty("запись из installed.json удалена");
    }

    [Fact]
    public void Uninstall_УдаляетЧерновик()
    {
        Setup();
        var draft = _store.DraftDir(Owner, Slug);
        Directory.Exists(draft).Should().BeTrue();

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        Directory.Exists(draft).Should().BeFalse("черновик _drafts/{slug}/ удалён");
    }

    [Fact]
    public void Uninstall_СнимаетВыданныеПраваИСекреты()
    {
        Setup();
        // granted и секреты живут в записи: удаление записи убирает и то, и другое.
        var before = _store.LoadInstalled(Owner).Single();
        before.Granted.Should().Contain("notes:read");
        before.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN");

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        var after = _store.LoadInstalled(Owner);
        after.Should().BeEmpty("granted и секреты ушли вместе с записью");
    }

    // --- Шаг B2: удаление установки с выданным storage — хвостов в записях прав нет ---
    // Права живут только в записи installed.json (отдельного хранилища прав в сторе нет —
    // проверено по коду). Мутация (не удалять запись в PluginUninstall): after непустой →
    // тест красный.

    [Fact]
    public void Uninstall_ВыданныйStorage_УходитСЗаписьюБезХвостов()
    {
        _store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = Id,
                Version = Version,
                Sha256 = "abc123",
                Requested = ["net:api.linear.app", "storage"],
                Granted = ["net:api.linear.app", "storage"],
                Enabled = true,
                Source = "draft",
            },
        ]);

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        var after = _store.LoadInstalled(Owner);
        after.Should().BeEmpty("запись с granted storage удалена целиком, хвостов нет");
    }

    [Fact]
    public void Uninstall_СнимаетВнешниеСсылкиЧерезШов()
    {
        Setup();

        _sut.Uninstall(Owner, Id).Should().BeTrue();

        _cascade.Calls.Should().ContainSingle().Which.Should().Be((Owner, Id),
            "шов IPluginInstallCascade вызван с владельцем и id (привязки персон, слоты)");
    }

    // --- Idempotency / not-found ---

    [Fact]
    public void Uninstall_БезЗаписи_ВозвращаетFalse_ИНеТронут()
    {
        // Пустой владелец: записи нет → false, шов не вызван, каталоги не тронуты.
        var pkg = _store.PackageDir(Owner, Id, Version);
        Directory.CreateDirectory(pkg);

        _sut.Uninstall(Owner, Id).Should().BeFalse();
        _cascade.Calls.Should().BeEmpty();
        Directory.Exists(pkg).Should().BeTrue("без записи пакет не трогаем");
    }

    [Fact]
    public void Uninstall_Повторный_ByIdempotent()
    {
        Setup();
        _sut.Uninstall(Owner, Id).Should().BeTrue();
        // Повтор: записи уже нет → false, без ошибок.
        _sut.Uninstall(Owner, Id).Should().BeFalse();
    }

    // --- Publisher check (minor): черновик _drafts/{slug} общий для всех publisher'ов,
    //     поэтому удаляется ТОЛЬКО если ВЫВЕДЕННЫЙ id черновика (DeriveId(ownerId, slug),
    //     QA-фикс O минор 3) совпадает с uninstall-имым id. ---

    [Fact]
    public void Uninstall_ЧерновикЧужогоPublisher_НеУдаляется()
    {
        // Запись local-b/pkg (чужой publisher: b ≠ ownerShort(Owner)); черновик _drafts/pkg.
        // Выведенный id черновика (DeriveId(Owner, pkg)) ≠ local-b/pkg → черновик не тронут.
        var draft = _store.DraftDir(Owner, "pkg");
        Directory.CreateDirectory(draft);
        File.WriteAllText(Path.Combine(draft, "ccs-plugin.json"),
            """{ "id": "local-a/pkg", "name": "P", "version": "1.0.0" }""");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = "local-b/pkg", Version = "1.0.0", Sha256 = "b", Enabled = true, Source = "draft" },
        ]);

        _sut.Uninstall(Owner, "local-b/pkg").Should().BeTrue();

        Directory.Exists(draft).Should().BeTrue("черновик чужого publisher (выведенный id ≠ uninstall id) не тронут");
    }

    [Fact]
    public void Uninstall_ЧерновикСвоегоPublisher_Удаляется()
    {
        // Запись с ВЫВЕДЕННЫМ id владельца (DeriveId(Owner, pkg)); черновик _drafts/pkg.
        // Выведенный id черновика = uninstall id → черновик удалён.
        var id = PluginIdentity.DeriveId(Owner, "pkg");
        var draft = _store.DraftDir(Owner, "pkg");
        Directory.CreateDirectory(draft);
        File.WriteAllText(Path.Combine(draft, "ccs-plugin.json"),
            """{ "name": "pkg", "version": "1.0.0" }""");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = id, Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        _sut.Uninstall(Owner, id).Should().BeTrue();

        Directory.Exists(draft).Should().BeFalse("черновик своего publisher (выведенный id = uninstall id) удалён");
    }

    // QA-фикс O (минор 3): черновик БЕЗ id в манифесте (новый шаблон, QA-фикс N) — сверка
    // по выведенному id, не по сырому manifest.Id (он null → черновик не удалялся).
    // Мутация: вернуть сверку по manifest.Id → тест красный.
    [Fact]
    public void Uninstall_ЧерновикБезIdВМанифесте_СвойPublisher_Удаляется()
    {
        var id = PluginIdentity.DeriveId(Owner, "pkg");
        var draft = _store.DraftDir(Owner, "pkg");
        Directory.CreateDirectory(draft);
        // Новый шаблон: ccs-plugin.json без id (name = slug, version).
        File.WriteAllText(Path.Combine(draft, "ccs-plugin.json"),
            """{ "name": "pkg", "version": "1.0.0" }""");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = id, Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        _sut.Uninstall(Owner, id).Should().BeTrue();

        Directory.Exists(draft).Should().BeFalse("черновик без id в манифесте удалён по выведенному id");
    }

    // --- Каскад (minor): шов бросил → запись installed.json ещё на месте (откат, «коммит» после шова). ---

    [Fact]
    public void Uninstall_ШовБросил_ЗаписьНеУдалена()
    {
        Setup();
        var sut = new PluginUninstall(_store, _secrets, new ThrowingCascade());

        Action act = () => sut.Uninstall(Owner, Id);
        act.Should().Throw<InvalidOperationException>("шов IPluginInstallCascade бросил");

        // «Коммит» (удаление записи) — ПОСЛЕ шова: шов упал → запись осталась (идемпотентный повтор).
        _store.LoadInstalled(Owner).Should().Contain(r => r.Id == Id, "запись не удалена: шов упал до коммита");
    }

    // Итерация 2 (R-Финал-2, MAJOR): каскад uninstall даёт ровно ОДНО plugins_changed.
    // SaveInstalled и RemovePlugin — с fireEvent: false (иначе по одному событию), единое
    // событие — в шаге 8. Мутация: вернуть fireEvent: true в SaveInstalled/RemovePlugin →
    // 2-3 события → тест красный. Секрет нужен: RemovePlugin на плагине без секретов
    // не мутирует (changed=false) и даже с fireEvent: true не испустил бы.
    [Fact]
    public void Uninstall_Каскад_РовноОдноСобытие()
    {
        var broadcaster = new RecordingBroadcaster();
        var store = new PluginStore(BuildConfig(_dir), broadcaster);
        var secrets = new PluginSecretsStore(BuildConfig(_dir), broadcaster);
        var sut = new PluginUninstall(store, secrets, _cascade, null, broadcaster);

        // Установка + секрет: RemovePlugin реально мутирует (changed) → при fireEvent: true испустил бы.
        store.SaveInstalled(Owner,
        [
            new PluginInstall
            {
                Id = Id, Version = Version, Sha256 = "abc", Enabled = true, Source = "draft",
                Secrets = [new PluginInstallSecret { Name = "LINEAR_TOKEN", Set = true }],
            },
        ]);
        secrets.Set(Owner, Id, "LINEAR_TOKEN", "tok-123");
        broadcaster.Reset();

        sut.Uninstall(Owner, Id).Should().BeTrue();

        // Ровно ОДНО plugins_changed за весь каскад (шаг 8), не два (SaveInstalled + RemovePlugin).
        broadcaster.OwnerMessages.Should().ContainSingle()
            .Which.Should().BeOfType<PluginsChangedMessage>();
    }

    // ISessionBroadcaster: собирает ToOwner-сообщения; Reset — «считать с нуля» перед каскадом.
    private sealed class RecordingBroadcaster : ISessionBroadcaster
    {
        public List<ServerMessage> OwnerMessages { get; } = [];
        public void Reset() => OwnerMessages.Clear();
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { OwnerMessages.Add(message); return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    // IPluginInstallCascade: фиксирует вызовы.
    private sealed class StubCascade : IPluginInstallCascade
    {
        public List<(string Owner, string Id)> Calls { get; } = [];
        public void OnUninstalled(string ownerId, string pluginId) => Calls.Add((ownerId, pluginId));
    }

    // IPluginInstallCascade: бросает (сбой каскада → откат записи).
    private sealed class ThrowingCascade : IPluginInstallCascade
    {
        public void OnUninstalled(string ownerId, string pluginId) => throw new InvalidOperationException("шов упал");
    }
}
