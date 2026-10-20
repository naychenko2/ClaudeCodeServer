using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// QA-фикс L4 — ИНВАРИАНТ: для любого черновика, если ValidateFull (проверка, которую видит
// человек) не нашла проблем, то accept (PluginInstaller.Install) НЕ МОЖЕТ упасть по причинам
// манифеста/пакета (id, version, имена, пути). Всё, что приведёт к отказу в accept, обязано
// быть видно в ValidateFull ДО нажатия «Принять…».
//
// Держится единой точкой PluginIdentity.Resolve: ValidateFull и Install разрешают id/version
// ОДИН и тот же методом. Мутация: убрать проверку version (или вывод id) из Resolve → тест красный.
public class PluginAcceptInvariantTests : IDisposable
{
    private const string Owner = "owner-a";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginPackageValidator _validator;
    private readonly PluginInstaller _installer;

    public PluginAcceptInvariantTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_inv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _validator = new PluginPackageValidator();
        _installer = new PluginInstaller(_store, _validator);
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

    // Черновик с заданным ccs-plugin.json (сырой JSON) + нативным манифестом (name = slug).
    // slug — часть id после '/' (или сам id, если '/' нет); нативный name = slug.
    private string CreateDraft(string slug, string ccsJson)
    {
        var root = _store.DraftDir(Owner, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"), ccsJson);
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug }, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // ── ИНВАРИАНТ: ValidateFull без проблем ⇒ accept успешен ──────────────────────────────
    // Таблица 9 манифестов. Для каждого: если ValidateFull не дал проблем — accept обязан
    // пройти (Ok/NoOp). Если ValidateFull дал проблемы — implication vacuous (accept может упасть).
    // Это и есть критерий: человек не увидит «Проверка пройдена» для пакета, который не принять.

    [Theory]
    // 1) нормальный: id = выведенный сервером (local-{ownerShort}/pkg) + version + вклад → ValidateFull чисто → accept Ok.
    [InlineData("pkg", """{"id":"local-952568751510/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 2) без id: id выводится (local-{ownerShort}/pkg) → ValidateFull чисто → accept Ok (выведенный id).
    [InlineData("pkg", """{"name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 3) без version: missing-version → ValidateFull с проблемами (accept может упасть — vacuous).
    [InlineData("pkg", """{"id":"local-952568751510/pkg","name":"pkg","contributes":{"skills":["skills/a"]}}""")]
    // 4) пустые строки id+version: id выводится, version пуст → missing-version (проблемы).
    [InlineData("pkg", """{"id":"","name":"pkg","version":"","contributes":{"skills":["skills/a"]}}""")]
    // 5) id с «../»: «..» не в [a-z0-9-] → bad-id (проблемы).
    [InlineData("pkg", """{"id":"../pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 6) id чужого издателя (other-pub/pkg ≠ выведенный local-{ownerShort}/pkg) → bad-id (QA-фикс N:
    //    id выводит сервер; чужой издатель = подмена) → ValidateFull с проблемами (vacuous).
    [InlineData("pkg", """{"id":"other-pub/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 7) длинный выведенный id (slug 60 символов ≤ 64): id = local-{ownerShort}/{slug} → ValidateFull
    //    чисто → accept Ok (инвариант держится и для длинных, но валидных значений).
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", """{"id":"local-952568751510/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 8) юникод в id (кириллица в slug, не в [a-z0-9-]) → bad-id (проблемы).
    [InlineData("pkg", """{"id":"local-a/пакет","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 9) юникод в version (pre-release с кириллицей, не в [A-Za-z0-9.-]) → bad-version (проблемы).
    [InlineData("pkg", """{"id":"local-952568751510/pkg","name":"pkg","version":"1.0.0-пр","contributes":{"skills":["skills/a"]}}""")]
    public void Инвариант_ValidateFullБезПроблем_ТогдаAcceptУспешен(string slug, string ccsJson)
    {
        var source = CreateDraft(slug, ccsJson);
        var draftDir = _store.DraftDir(Owner, slug);

        // Проверка, которую видит человек (Preview/карточка).
        var validation = _validator.ValidateFull(draftDir, Owner, slug);

        if (validation.IsValid)
        {
            // ValidateFull чисто → accept обязан пройти (Ok = первая установка, NoOp = повтор).
            var result = _installer.Install(Owner, source);
            (result.Status == InstallStatus.Ok || result.Status == InstallStatus.NoOp).Should().BeTrue(
                $"ValidateFull без проблем, но accept упал: {result.Error} " +
                $"(проблемы: {string.Join(", ", (result.Problems ?? []).Select(p => p.Code))})");
        }
    }

    // ── Точечные: конкретные коды проблем ─────────────────────────────────────────────────

    // Без id → id выведен (local-{ownerShort}/slug), ValidateFull чисто, accept Ok с выведенным id.
    [Fact]
    public void БезId_IdВыведен_AcceptOkСВыведеннымId()
    {
        const string slug = "qaagent";
        // name = slug каталога (qaagent): файловая проверка name==slug чистая (QA-фикс O минор 1).
        var source = CreateDraft(slug, """{"name":"qaagent","version":"0.1.0","contributes":{"skills":["skills/a"]}}""");
        var draftDir = _store.DraftDir(Owner, slug);

        var validation = _validator.ValidateFull(draftDir, Owner, slug);
        validation.IsValid.Should().BeTrue($"без id → выводится, проблем нет: {string.Join("; ", validation.Problems.Select(p => p.Code))}");

        var result = _installer.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Ok);
        // Выведенный id: local-{ownerShort}/{slug} — publisher от владельца, slug из черновика.
        var expectedId = PluginIdentity.DeriveId(Owner, slug);
        result.Record!.Id.Should().Be(expectedId, "id выведен сервером (агент издателя не выбирал)");
        _store.LoadInstalled(Owner).Single(r => r.Id == expectedId).Version.Should().Be("0.1.0");
    }

    // Без version → missing-version в ValidateFull; accept тем же кодом (FailValidation + missing-version).
    [Fact]
    public void БезVersion_MissingVersion_ВПроверкеИВAccept()
    {
        const string slug = "pkg";
        var source = CreateDraft(slug, """{"id":"local-952568751510/pkg","name":"pkg","contributes":{"skills":["skills/a"]}}""");
        var draftDir = _store.DraftDir(Owner, slug);

        _validator.ValidateFull(draftDir, Owner, slug).Problems
            .Should().Contain(p => p.Code == "missing-version", "version обязателен — проверка видит это ДО accept");

        var result = _installer.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Fail);
        result.Problems.Should().Contain(p => p.Code == "missing-version", "accept тем же кодом, что проверка");
    }

    // Битый id (точка в publisher) → bad-id в ValidateFull; accept тем же кодом.
    [Fact]
    public void БитыйId_BadId_ВПроверкеИВAccept()
    {
        const string slug = "pkg";
        var source = CreateDraft(slug, """{"id":"local.a/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""");
        var draftDir = _store.DraftDir(Owner, slug);

        _validator.ValidateFull(draftDir, Owner, slug).Problems
            .Should().Contain(p => p.Code == "bad-id", "id «local.a/pkg» (точка) — не по формату");

        var result = _installer.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Fail);
        result.Problems.Should().Contain(p => p.Code == "bad-id");
    }

    // QA-фикс N (critical): id выводит ТОЛЬКО сервер. Заданный в манифесте id обязан совпасть
    // с выведенным (DeriveId(ownerId, slug)), иначе bad-id. Агент выбирает только slug.

    // id чужого издателя (other-pub/pkg ≠ выведенный local-{ownerShort}/pkg) → bad-id
    // в проверке (которую видит человек) и в accept одним кодом.
    [Fact]
    public void IdЧужогоИздателя_BadId_ВПроверкеИВAccept()
    {
        const string slug = "pkg";
        var source = CreateDraft(slug, """{"id":"other-pub/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""");
        var draftDir = _store.DraftDir(Owner, slug);

        _validator.ValidateFull(draftDir, Owner, slug).Problems
            .Should().Contain(p => p.Code == "bad-id", "id чужого издателя ≠ выведенный сервером");

        var result = _installer.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Fail);
        result.Problems.Should().Contain(p => p.Code == "bad-id", "accept тем же кодом, что проверка");
    }

    // id уже установленного плагина, но черновик с ДРУГИМ slug: выведенный id другого slug
    // ≠ id установленного → bad-id (агент не может «дослать» id чужого пакета).
    [Fact]
    public void IdУстановленного_ЧерновикДругогоSlug_BadId()
    {
        // Установлен local-{ownerShort}/pkg (slug "pkg").
        var installedId = PluginIdentity.DeriveId(Owner, "pkg");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = installedId, Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        // Черновик с slug "other", но id в манифесте = id установленного плагина.
        // name в обоих манифестах = "other" (slug КАТАЛОГА, QA-фикс O минор 1), чтобы файловая
        // проверка name==slug не заслоняла bad-id (manifest-проверка идёт только при чистых файлах).
        const string slug = "other";
        var json = "{\"id\":\"" + installedId + "\",\"name\":\"other\",\"version\":\"2.0.0\",\"contributes\":{\"skills\":[\"skills/a\"]}}";
        var source = CreateDraft(slug, json);
        var draftDir = _store.DraftDir(Owner, slug);
        File.WriteAllText(Path.Combine(draftDir, ".claude-plugin", "plugin.json"),
            "{\"name\":\"other\",\"version\":\"2.0.0\"}");

        _validator.ValidateFull(draftDir, Owner, slug).Problems
            .Should().Contain(p => p.Code == "bad-id",
                "id установленного плагина ≠ выведенный для slug «other»");

        var result = _installer.Install(Owner, source);
        result.Status.Should().Be(InstallStatus.Fail);
        result.Problems.Should().Contain(p => p.Code == "bad-id");
    }

    // Сценарий из ревью (подмена): установлен local-{ownerShort}/pkg с выданным правом,
    // черновик другого slug записал id установленного плагина (v2) → accept ОТКЛОНЯЕТ
    // (bad-id), и granted НЕ унаследован: запись установленного плагина не тронута,
    // новой записи (с другим slug) нет.
    [Fact]
    public void Подмена_Установленного_GrantedНеУнаследован()
    {
        // 1) Установить плагин (slug "pkg") и выдать право (как человек через grant).
        var installedId = PluginIdentity.DeriveId(Owner, "pkg");
        var json1 = "{\"id\":\"" + installedId + "\",\"name\":\"pkg\",\"version\":\"1.0.0\",\"contributes\":{\"skills\":[\"skills/a\"]},\"permissions\":{\"requested\":[\"notes:read\"]}}";
        var source1 = CreateDraft("pkg", json1);
        _installer.Install(Owner, source1).Status.Should().Be(InstallStatus.Ok);
        _store.Update(Owner, records =>
        {
            records.Single(r => r.Id == installedId).Granted = ["notes:read"];
            return records;
        });

        // 2) Черновик другого slug (other) с id УСТАНОВЛЕННОГО плагина, версия 2.0.0.
        // name = "other" (slug КАТАЛОГА, QA-фикс O минор 1) в обоих манифестах: файловая
        // проверка name==slug чистая, bad-id (manifest-уровень) не заслонён.
        var json2 = "{\"id\":\"" + installedId + "\",\"name\":\"other\",\"version\":\"2.0.0\",\"contributes\":{\"skills\":[\"skills/a\"]}}";
        var source2 = CreateDraft("other", json2);
        var draftDir2 = _store.DraftDir(Owner, "other");
        File.WriteAllText(Path.Combine(draftDir2, ".claude-plugin", "plugin.json"),
            "{\"name\":\"other\",\"version\":\"2.0.0\"}");
        var result = _installer.Install(Owner, source2);

        // 3) Отказ (bad-id): granted не унаследован.
        result.Status.Should().Be(InstallStatus.Fail, "подмена id отбита bad-id");
        result.Problems.Should().Contain(p => p.Code == "bad-id");

        // Запись установленного плагина не тронута: та же версия, то же выданное право.
        var installed = _store.LoadInstalled(Owner).Single(r => r.Id == installedId);
        installed.Version.Should().Be("1.0.0", "версия установленного плагина не «обновилась» чужим черновиком");
        installed.Granted.Should().Equal(["notes:read"], "granted не унаследован/не сброшен");
        // Новой записи (выведенный id другого slug) не появилось.
        _store.LoadInstalled(Owner).Should().NotContain(r => r.Id == PluginIdentity.DeriveId(Owner, "other"),
            "черновик другого slug не создал запись — granted остался у своего плагина");
    }

    // Мутация (критерий): убрать вывод id (DeriveId) из Resolve → «без id» даёт null id →
    // Install падает, хотя ValidateFull (с выводом) был бы чист. Здесь фиксируем: вывод id
    // реально происходит (DeriveId детерминирован, ownerShort = 12 hex, QA-фикс N).
    [Fact]
    public void ВыводId_Детерминирован_OwnerShort12Hex()
    {
        var id1 = PluginIdentity.DeriveId("owner-a", "pkg");
        var id2 = PluginIdentity.DeriveId("owner-a", "pkg");
        id1.Should().Be(id2, "вывод id детерминирован (один владелец+slug → один id)");
        // Формат local-{12 hex}/{slug}: publisher = local-<12 hex-символов> (QA-фикс N: 12, не 8).
        id1.Should().MatchRegex(@"^local-[0-9a-f]{12}/pkg$");
        // Другой владелец → другой publisher (ownerShort зависит от ownerId).
        PluginIdentity.DeriveId("owner-b", "pkg").Should().NotBe(id1);
    }
}
