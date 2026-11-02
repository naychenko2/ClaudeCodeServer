using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// NA-2 (Ф2): блок «Ночной разбор» в keeper/settings.json — уровень стора. Состояние строится
// записью файла с явным текстом (в т.ч. старым файлом до Ф2 — без блока), проверяется то, что
// вернёт Read. Блок nullable по решению плана: «блока нет» — не ошибка формата, а состояние,
// читаемое как умолчания с выключенной ночью; V при этом НЕ поднимается: при чужом V Read
// отдаёт умолчания целиком, и у всех владельцев пропали бы выключенные классы.
public class WikiSettingsStoreTests : IDisposable
{
    private const string User = "u1";
    private const string ClassKey = "session×append";

    private readonly string _dir;

    public WikiSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_settings_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: старый файл без блока читается целиком ────────────────────────────

    [Fact]
    public void СтарыйФайлV1БезНочногоБлокаЧитаетсяЦеликом_НочьПоУмолчаниюВыключена()
    {
        WriteFile("""
            {"v":1,"autoApplyEnabled":false,"classes":[{"class":"session×append","disabled":true,
             "reason":"класс выключен человеком","disabledAt":"2026-10-01T00:00:00+00:00","resumedAt":null}]}
            """);

        var (settings, invalid) = WikiSettingsStore.Read(Root());

        invalid.Should().BeFalse("нет блока — не ошибка формата: файл записан до Ф2");
        settings.AutoApplyEnabled.Should().BeFalse();
        settings.IsClassDisabled(ClassKey).Should().BeTrue("классы на месте");
        settings.Nightly.Should().BeNull();
        var night = settings.NightlyOrDefaults();
        night.Enabled.Should().BeFalse("ночь сама не приходит: тумблер по умолчанию выкл");
        night.WindowStartHour.Should().Be(3);
        night.WindowEndHour.Should().Be(6);
        night.MaxPackagesPerNight.Should().Be(20);
        night.MaxPackagesPerSection.Should().Be(6);
        night.MaxChatsPerSection.Should().Be(10);
        night.InputTokenBudget.Should().Be(700_000);
    }

    [Fact]
    public void НетФайла_УмолчанияСВыключеннойНочью()
    {
        var (settings, invalid) = WikiSettingsStore.Read(Root());

        invalid.Should().BeFalse();
        settings.AutoApplyEnabled.Should().BeTrue();
        settings.Nightly.Should().BeNull();
        settings.NightlyOrDefaults().Enabled.Should().BeFalse();
    }

    // ─── c2: кривой блок делает невалидным весь файл ───────────────────────────

    [Fact]
    public void НочнойБлокСЧасомВнеДиапазонаДелаетВесьФайлНевалидным_ЧитаетсяКакУмолчания()
    {
        WriteFile("""
            {"v":1,"autoApplyEnabled":false,"classes":[{"class":"session×append","disabled":true,
             "reason":"класс выключен человеком","disabledAt":"2026-10-01T00:00:00+00:00","resumedAt":null}],
             "nightly":{"enabled":true,"windowStartHour":24,"windowEndHour":6,
             "maxPackagesPerNight":20,"maxPackagesPerSection":6,"maxChatsPerSection":10,"inputTokenBudget":700000}}
            """);

        var (settings, invalid) = WikiSettingsStore.Read(Root());

        invalid.Should().BeTrue("windowStartHour=24: частичной починки нет, невалиден весь файл");
        settings.AutoApplyEnabled.Should().BeTrue("умолчания целиком");
        settings.Classes.Should().BeEmpty();
        settings.NightlyOrDefaults().Should().Be(WikiSettingsStore.DefaultNightly());
        File.ReadAllText(SettingsPath()).Should().Contain("\"windowStartHour\":24"); // чтение ничего не «чинит»
    }

    [Theory]
    [InlineData(5, 5, 20, 6, 10, 700000)]    // начало окна равно его концу: пустое half-open окно
    [InlineData(-1, 6, 20, 6, 10, 700000)]   // час отрицательный
    [InlineData(3, 24, 20, 6, 10, 700000)]   // час выше 23
    [InlineData(3, 6, 21, 6, 10, 700000)]    // пакетов за ночь выше лимита открытых
    [InlineData(3, 6, 20, 0, 10, 700000)]    // нулевой лимит пакетов на раздел
    [InlineData(3, 6, 20, 6, 0, 700000)]     // нулевой лимит чатов на раздел
    [InlineData(3, 6, 20, 6, 10, 0)]         // нулевой бюджет токенов
    [InlineData(3, 6, 20, 6, 10, 5000001)]   // бюджет выше потолка
    public void НочнойБлокСНевалиднымПолемДелаетФайлНевалидным(int start, int end, int perNight, int perSection, int chats, int tokens)
    {
        WriteFile(NightlyFile(start, end, perNight, perSection, chats, tokens));

        var (settings, invalid) = WikiSettingsStore.Read(Root());

        invalid.Should().BeTrue($"кривое поле ({start}, {end}, {perNight}, {perSection}, {chats}, {tokens})");
        settings.NightlyOrDefaults().Should().Be(WikiSettingsStore.DefaultNightly());
    }

    [Fact]
    public void ПустойНочнойБлокВФайлеНевалиден_КакКриваяЗаписьКласса()
    {
        // {} — окно 0..0 и нулевые лимиты: это не «блок по умолчанию», а кривой блок.
        WriteFile("""{"v":1,"autoApplyEnabled":true,"classes":[],"nightly":{}}""");

        var (_, invalid) = WikiSettingsStore.Read(Root());

        invalid.Should().BeTrue();
    }

    // ─── валидный блок: записан через Mutate, читается, выживает при чужой правке ───

    [Fact]
    public void ВалидныйНочнойБлокЗаписанныйЧерезMutateЧитаетсяОбратно_ИВыживаетПриСменеКласса()
    {
        var night = new WikiSettingsStore.NightlyConfig(true, 21, 2, 15, 4, 8, 100_000); // окно через полночь — допустимо
        WikiSettingsStore.Mutate(_dir, User, _ => WikiSettingsStore.Defaults() with { Nightly = night });

        var (settings, invalid) = WikiSettingsStore.Read(Root());
        invalid.Should().BeFalse();
        settings.Nightly.Should().Be(night);

        // Чужая правка через with (путь автовыключения класса) не съедает блок: with сохраняет поля.
        WikiSettingsStore.Mutate(_dir, User, cur => cur with
        {
            Classes = [new WikiSettingsStore.ClassState(ClassKey, true, "автовыключение", DateTimeOffset.UtcNow, null)],
        });

        var (after, invalid2) = WikiSettingsStore.Read(Root());
        invalid2.Should().BeFalse();
        after.Nightly.Should().Be(night);
        after.IsClassDisabled(ClassKey).Should().BeTrue();
    }

    // ─── построение состояния ───────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);
    private string SettingsPath() => Path.Combine(Root(), "keeper/settings.json");

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), json, new UTF8Encoding(false));
    }

    private static string NightlyFile(int start, int end, int perNight, int perSection, int chats, int tokens) =>
        JsonSerializer.Serialize(new
        {
            v = 1,
            autoApplyEnabled = true,
            classes = Array.Empty<object>(),
            nightly = new
            {
                enabled = true,
                windowStartHour = start,
                windowEndHour = end,
                maxPackagesPerNight = perNight,
                maxPackagesPerSection = perSection,
                maxChatsPerSection = chats,
                inputTokenBudget = tokens,
            },
        });
}
