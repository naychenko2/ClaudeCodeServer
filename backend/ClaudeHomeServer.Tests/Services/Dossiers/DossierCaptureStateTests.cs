using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// DossierCaptureState (ADR-004 §2, major-правка Глеба №3): единица наблюдения — РАБОЧЕЕ ДЕРЕВО,
// а не проект. state.json ключуется {owner}:{project}:{hash(EffectiveRoot)}: worktree-чат
// коммитит в свою ветку, и HEAD корня такого коммита не увидит — без per-дерева ключа паспорт
// worktree-коммита не снялся бы, а чужое дерево затёрло бы «последнее виденное HEAD» корня.
public class DossierCaptureStateTests : IDisposable
{
    private readonly string _temp;
    private readonly DossierCaptureState _state;

    public DossierCaptureStateTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_state_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
        }).Build();
        _state = new DossierCaptureState(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void RootKey_РазныеДеревья_РазныеКлючи()
    {
        var root = Path.Combine(_temp, "main");
        var worktree = Path.Combine(_temp, "worktree");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(worktree);

        var k1 = DossierCaptureState.RootKey("owner", "proj", root);
        var k2 = DossierCaptureState.RootKey("owner", "proj", worktree);

        k1.Should().NotBe(k2, "worktree-коммит наблюдается по своему ключу, не затирая корень");
    }

    [Fact]
    public void RootKey_ОдноДерево_ОдинКлюч_Детерминизм()
    {
        var root = Path.Combine(_temp, "main");
        Directory.CreateDirectory(root);

        var k1 = DossierCaptureState.RootKey("owner", "proj", root);
        var k2 = DossierCaptureState.RootKey("owner", "proj", root);

        k1.Should().Be(k2);
        k1.Should().Contain("owner:proj:");
    }

    [Fact]
    public void RootKey_РазныеВладельцы_РазныеКлючи()
    {
        var root = Path.Combine(_temp, "main");
        Directory.CreateDirectory(root);

        DossierCaptureState.RootKey("ownerA", "proj", root)
            .Should().NotBe(DossierCaptureState.RootKey("ownerB", "proj", root));
    }

    [Fact]
    public void Get_Set_Кругловорот()
    {
        var root = Path.Combine(_temp, "main");
        Directory.CreateDirectory(root);
        var key = DossierCaptureState.RootKey("owner", "proj", root);

        _state.Get(key).Should().BeNull();

        _state.Set(key, "abc123");
        _state.Get(key).Should().Be("abc123");

        _state.Set(key, "def456");
        _state.Get(key).Should().Be("def456");
    }

    // Идемпотентность после отката state.json из бэкапа: store восстанавливается вместе со
    // state, и повторный захват уже виденного коммита не должен дать дубль (§4, §7). State
    // переживает пересоздание (persist на диске) — новый инстанс читает ту же запись.
    [Fact]
    public void ПереживаетПересозданиеИнстанса_КакБэкапРестор()
    {
        var root = Path.Combine(_temp, "main");
        Directory.CreateDirectory(root);
        var key = DossierCaptureState.RootKey("owner", "proj", root);

        _state.Set(key, "seen-head");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
        }).Build();
        var restored = new DossierCaptureState(config);

        restored.Get(key).Should().Be("seen-head",
            "восстановленный из бэкапа state.json знает последний виденный HEAD — повторный захват не даст дубль");
    }

    // --- Compare-and-set курсора импорта (гонка курсора, разбор консилиума 23.08) ---

    [Fact]
    public void SetIfUnchanged_ЗначениеНеМенялось_Записывает()
    {
        var key = DossierCaptureState.ImportKey("owner", "proj");

        // Первый импорт: ключа ещё нет (expected null)
        _state.SetIfUnchanged(key, expected: null, "tip1").Should().BeTrue(
            "отсутствие значения — тоже «не менялось», первый курсор обязан записаться");
        _state.Get(key).Should().Be("tip1");

        _state.SetIfUnchanged(key, expected: "tip1", "tip2").Should().BeTrue();
        _state.Get(key).Should().Be("tip2");
    }

    [Fact]
    public void SetIfUnchanged_ЗначениеУспелоИзмениться_ОтказываетИНеЗатирает()
    {
        var key = DossierCaptureState.ImportKey("owner", "proj");
        _state.Set(key, "tip-старый");

        // За «долгий импорт» автовыгрузка успела пометить свой tip в том же ключе
        _state.Set(key, "tip-наш");

        _state.SetIfUnchanged(key, expected: "tip-старый", "tip-старый-прочитанный").Should().BeFalse(
            "значение изменилось с момента чтения — слепая запись затёрла бы MarkOwnTip");
        _state.Get(key).Should().Be("tip-наш", "пометка автовыгрузки не затёрта");
    }
}
