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
    // 1) нормальный: валидный id + version + вклад → ValidateFull чисто → accept Ok.
    [InlineData("pkg", """{"id":"local-a/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 2) без id: id выводится (local-{ownerShort}/pkg) → ValidateFull чисто → accept Ok (выведенный id).
    [InlineData("pkg", """{"name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 3) без version: missing-version → ValidateFull с проблемами (accept может упасть — vacuous).
    [InlineData("pkg", """{"id":"local-a/pkg","name":"pkg","contributes":{"skills":["skills/a"]}}""")]
    // 4) пустые строки id+version: id выводится, version пуст → missing-version (проблемы).
    [InlineData("pkg", """{"id":"","name":"pkg","version":"","contributes":{"skills":["skills/a"]}}""")]
    // 5) id с «../»: «..» не в [a-z0-9-] → bad-id (проблемы).
    [InlineData("pkg", """{"id":"../pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 6) id чужого издателя (валидный формат other-pub/pkg): ValidateFull чисто → accept Ok.
    [InlineData("pkg", """{"id":"other-pub/pkg","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 7) слишком длинный id (валидный формат, slug 80 символов > 64): PluginId не режет длину
    //    → ValidateFull чисто → accept Ok (инвариант держится и для длинных, но валидных значений).
    [InlineData("pkg", """{"id":"local-a/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 8) юникод в id (кириллица в slug, не в [a-z0-9-]) → bad-id (проблемы).
    [InlineData("pkg", """{"id":"local-a/пакет","name":"pkg","version":"1.0.0","contributes":{"skills":["skills/a"]}}""")]
    // 9) юникод в version (pre-release с кириллицей, не в [A-Za-z0-9.-]) → bad-version (проблемы).
    [InlineData("pkg", """{"id":"local-a/pkg","name":"pkg","version":"1.0.0-пр","contributes":{"skills":["skills/a"]}}""")]
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
        var source = CreateDraft(slug, """{"name":"QA виджет","version":"0.1.0","contributes":{"skills":["skills/a"]}}""");
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
        var source = CreateDraft(slug, """{"id":"local-a/pkg","name":"pkg","contributes":{"skills":["skills/a"]}}""");
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

    // Мутация (критерий): убрать вывод id (DeriveId) из Resolve → «без id» даёт null id →
    // Install падает, хотя ValidateFull (с выводом) был бы чист. Здесь фиксируем: вывод id
    // реально происходит (DeriveId детерминирован, ownerShort = 8 hex).
    [Fact]
    public void ВыводId_Детерминирован_OwnerShort8Hex()
    {
        var id1 = PluginIdentity.DeriveId("owner-a", "pkg");
        var id2 = PluginIdentity.DeriveId("owner-a", "pkg");
        id1.Should().Be(id2, "вывод id детерминирован (один владелец+slug → один id)");
        // Формат local-{8 hex}/{slug}: publisher = local-<8 hex-символов>.
        id1.Should().MatchRegex(@"^local-[0-9a-f]{8}/pkg$");
        // Другой владелец → другой publisher (ownerShort зависит от ownerId).
        PluginIdentity.DeriveId("owner-b", "pkg").Should().NotBe(id1);
    }
}
