using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Services.ChatUserState;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.ChatUserState;

/// <summary>
/// Стор оверлея личного состояния чата (S1-6, разрез §«Личное состояние чата»):
/// data/chat-user-state/{userId}.json, ключ — sessionId.
///
/// Что фиксируют тесты (критерии S1-6):
/// 1. двое пользователей — отметки одного не видны другому (файл на пользователя);
/// 2. нет файла / нет записи — Get отдаёт null («не задано»), значение с Session решает
///    вызывающий; битый JSON не роняет стор и не стирает данные молча;
/// 3. операции правят по одному полю, не затирая остальные;
/// 4. корень — из резолвера путей данных (DataPath), не хардкод; файл стора лежит в data/
///    и по правилам бэкапа (BackupPaths.ShouldInclude) едет в архив.
/// Платформонезависимо: Path.GetTempPath()+Path.Combine, без задержек.
/// </summary>
public sealed class ChatUserStateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ChatUserStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-chat-user-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private string Root => Path.Combine(_tempDir, ChatUserStateStore.DirName);

    private static IConfiguration ConfigWith(string dataPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = dataPath,
        }).Build();

    private ChatUserStateStore Store() => new(Root, TimeProvider.System);

    private string FileFor(string userId) => Path.Combine(Root, userId + ".json");

    [Fact]
    public void НетФайлаИлиНетЗаписи_ВозвращаетНеЗадано()
    {
        var store = Store();

        // Нет файла пользователя вовсе.
        store.Get("user-a", "chat-1").Should().BeNull("нет файла — состояние не задано, значение с Session решает вызывающий");

        // Файл есть, но записи для этого чата нет.
        store.MarkRead("user-a", "chat-1");
        store.Get("user-a", "chat-2").Should().BeNull("запись другого чата не распространяется на этот");
    }

    [Fact]
    public void ДвоеПользователей_ОтметкиОдногоНеВидныДругому()
    {
        var store = Store();
        var at = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        store.MarkRead("user-a", "chat-1", at);
        // Отметка прочтения участника A не видна участнику B.
        store.Get("user-b", "chat-1").Should().BeNull("прочитанность — личное состояние, не общее на чат");

        store.SetPinned("user-b", "chat-1", true);

        // И наоборот: закреплённое B не подкрашивается A, и личное A не перезаписано чужой операцией.
        var a = store.Get("user-a", "chat-1");
        a.Should().NotBeNull();
        a!.LastReadAt.Should().Be(at);
        a.IsPinned.Should().BeNull("закрепление — состояние пользователя B, у A оно не задано");
        a.NotificationsMuted.Should().BeNull();

        var b = store.Get("user-b", "chat-1");
        b.Should().NotBeNull();
        b!.IsPinned.Should().BeTrue();
        b.LastReadAt.Should().BeNull("прочитанность A не видна B");
        b.NotificationsMuted.Should().BeNull();
    }

    [Fact]
    public void ОперацииПравятПоОдноПоле_НеЗатираяДругие()
    {
        var store = Store();
        var at = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        var s1 = store.MarkRead("user-a", "chat-1", at);
        var s2 = store.SetPinned("user-a", "chat-1", true);
        var s3 = store.SetMuted("user-a", "chat-1", true);

        s1.LastReadAt.Should().Be(at);
        s2.LastReadAt.Should().Be(at);
        s2.IsPinned.Should().BeTrue();
        s3.Should().BeEquivalentTo(new UserChatState(at, true, true));

        // Снятие закрепления трогает только его.
        var s4 = store.SetPinned("user-a", "chat-1", false);
        s4.IsPinned.Should().BeFalse();
        s4.LastReadAt.Should().Be(at);
        s4.NotificationsMuted.Should().BeTrue();

        // И то же состояние читается с диска после перезагрузки стора (тот же корень, новый экземпляр).
        Store().Get("user-a", "chat-1").Should().BeEquivalentTo(s4);
    }

    [Fact]
    public void МаркРидБезОтметкиСтавитТекущееВремя()
    {
        var store = Store();
        var before = DateTime.UtcNow;

        var s = store.MarkRead("user-a", "chat-1");

        s.LastReadAt.Should().NotBeNull();
        s.LastReadAt.Should().BeOnOrAfter(before.AddSeconds(-5));
        s.LastReadAt.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void БитыйФайл_НеРоняетИНеСтираетДанныеМолча()
    {
        // Полностью нечитаемый JSON — как ручная правка файла или обрыв записи.
        const string broken = "{ \"chat-1\": {\"lastReadAt\": ";
        Directory.CreateDirectory(Root);
        File.WriteAllText(FileFor("user-a"), broken);

        var store = Store();
        var act = () => store.Get("user-a", "chat-1");

        act.Should().NotThrow("битый стор не роняет вызывающего — состояние просто не задано");
        act().Should().BeNull();

        // Данные не стёрты молча: повреждённый файл сохранён рядом снимком .corrupt-*.bak
        // (тот же путь восстановления, что у остальных JSON-сторов через JsonFileStore).
        var snapshots = Directory.GetFiles(Root, "user-a.json.corrupt-*.bak");
        snapshots.Should().NotBeEmpty("повреждённый исходник обязан остаться для разбора");
        File.ReadAllText(snapshots[0]).Should().Be(broken);
    }

    [Fact]
    public void ОднаБитаяЗапись_НеГлушитОстальные()
    {
        // Синтаксически целый файл с одной битой записью: поэлементный подъём
        // пропускает только её, копия исходника кладётся рядом (.partial-*.bak).
        const string json = """
            { "chat-1": { "lastReadAt": "2026-10-29T12:00:00Z" }, "chat-2": { "isPinned": "не-bool" } }
            """;
        Directory.CreateDirectory(Root);
        File.WriteAllText(FileFor("user-a"), json);

        var store = Store();

        var ok = store.Get("user-a", "chat-1");
        ok.Should().NotBeNull("уцелевшие записи поднимаются");
        ok!.LastReadAt.Should().Be(new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));

        store.Get("user-a", "chat-2").Should().BeNull("битая запись — не задана, но не тянет за собой весь стор");

        // Файл остался на месте, и рядом — копия полного исходника: первый Save не затёр бы
        // его усечённым словарем без следа от пропущенной записи.
        File.ReadAllText(FileFor("user-a")).Should().Be(json);
        Directory.GetFiles(Root, "user-a.json.partial-*.bak").Should().NotBeEmpty();
    }

    [Fact]
    public void КореньБерётсяИзРезолвераДанных_АНеХардкодом()
    {
        var dataPath = Path.Combine(_tempDir, "projects.json");

        var root = ChatUserStateStore.RootFromConfig(ConfigWith(dataPath));

        root.Should().Be(Path.Combine(_tempDir, "chat-user-state"),
            "каталог стора — под data из DataPath, как у остальных сторов (тот же резолвер, что у ChatContextStore)");

        // Файл стора действительно ложится в этот корень.
        var store = new ChatUserStateStore(root, TimeProvider.System);
        store.MarkRead("user-a", "chat-1");
        File.Exists(Path.Combine(root, "user-a.json")).Should().BeTrue();
    }

    [Fact]
    public void ФайлСторВData_ПопадаетВБэкап()
    {
        // data = _tempDir (DataPath = {data}/projects.json), файл стора — {data}/chat-user-state/user-a.json.
        var dataPath = Path.Combine(_tempDir, "projects.json");
        var root = ChatUserStateStore.RootFromConfig(ConfigWith(dataPath));
        var store = new ChatUserStateStore(root, TimeProvider.System);

        store.MarkRead("user-a", "chat-1");

        var relative = Path.GetRelativePath(_tempDir, Path.Combine(root, "user-a.json"))
            .Replace('\\', '/');
        relative.Should().Be("chat-user-state/user-a.json");

        // Правила состава архива: для этого корня нет строки-исключения → едет в архив.
        BackupPaths.ShouldInclude(relative).Should().BeTrue(
            "оверлей личного состояния — настройка, её потеря на restore перечернула бы выбор пользователя");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a/../b")]
    public void НедопустимыйИдентификаторПользователя_Отказ(string userId)
    {
        var act = () => Store().Get(userId, "chat-1");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void НедопустимыйИдентификаторСессии_Отказ(string sessionId)
    {
        var act = () => Store().Get("user-a", sessionId);
        act.Should().Throw<ArgumentException>();
    }
}
