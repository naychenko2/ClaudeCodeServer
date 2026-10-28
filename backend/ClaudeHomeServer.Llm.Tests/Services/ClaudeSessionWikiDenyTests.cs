using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// ADR-035 §2 (S1c): встроенный deny CLI-агенту по абсолютному {data}/wiki/** для
// Edit/Write/NotebookEdit — до project-правил и до авто-allow исполнителя задачи
// (единственный писатель вики — Хранитель). Проверяется решение DecidePermissionAsync,
// не живой CLI: в acceptEdits правку до сервера не доносит (её отсекает статический
// deny настроек запуска — см. ClaudeRuntimeSettingsTests), сюда приходят обычный режим,
// «всегда разрешать», исполнитель задачи, acceptEdits-персона (её автономный allow) и пути
// через «..».
// Каждый отказ-кейс отвечает и на сломанном коде: если правка всё же ушла на карточку,
// тест отвечает на неё и краснеет на признаке «карточки быть не должно» — не висит
// до 60-минутного таймаута ожидания.
public class ClaudeSessionWikiDenyTests
{
    private static readonly MethodInfo Decide =
        typeof(ClaudeSession).GetMethod("DecidePermissionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-wiki-deny-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (ClaudeSession Session, List<ServerMessage> Sent, string Root) NewSession(
        string? dataDir, string? root = null, bool taskExecution = false, List<string>? autoAllow = null,
        string? personaId = null, ClaudeMode? mode = null)
    {
        root ??= NewDir("root");
        var sent = new List<ServerMessage>();
        var info = new Session { TaskExecution = taskExecution, AutoAllowTools = autoAllow ?? [] };
        // Mode по умолчанию — AcceptEdits (Session.cs); без явной персоны эта ветка не
        // срабатывает (она требует PersonaId), поэтому прежние кейсы остаются на карточке.
        if (personaId is not null) info.PersonaId = personaId;
        if (mode is not null) info.Mode = mode.Value;
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            BuiltInSystemPrompt: "",
            PermissionRules: null,
            TasksMcp: null,
            DataDir: dataDir);
        return (new ClaudeSession(info, context), sent, root);
    }

    private static Task<string> DecideAsync(ClaudeSession session, string requestId, string toolName,
        string pathKey, string path)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [pathKey] = path }));
        return (Task<string>)Decide.Invoke(session, [requestId, toolName, doc.RootElement.Clone(), new object()])!;
    }

    // Ждём карточку короткий срок: появилась — отвечаем на неё, чтобы тест не висел
    // до 60-минутного таймаута (на сломанном коде отказ не встроенный, и карточка есть).
    private static async Task<bool> ResolveIfCardAppeared(ClaudeSession session, List<ServerMessage> sent, string requestId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (sent)
            {
                if (sent.OfType<PermissionRequestMessage>().Any())
                {
                    session.RespondPermission(requestId, "deny");
                    return true;
                }
            }
            await Task.Delay(10);
        }
        return false;
    }

    // Все три инструмента записи файлов и все режимы, где раньше был allow: обычный чат,
    // «всегда разрешать», исполнитель задачи. Отказ встроенный — без карточки.
    [Theory]
    [InlineData(false, false, "Edit", "file_path")]
    [InlineData(false, false, "Write", "file_path")]
    [InlineData(false, false, "NotebookEdit", "notebook_path")]
    [InlineData(false, true, "Write", "file_path")]  // «всегда разрешать» нажато — вику не открывает
    [InlineData(true, false, "Edit", "file_path")]    // исполнитель: ветка авто-allow не доезжает до вики
    [InlineData(true, false, "Write", "file_path")]
    [InlineData(true, false, "NotebookEdit", "notebook_path")]
    public async Task ЗаписьВФайлВики_ОтказБезКарточки(bool taskExecution, bool autoAllow, string toolName, string pathKey)
    {
        var data = NewDir("data");
        var (session, sent, _) = NewSession(data, taskExecution: taskExecution, autoAllow: autoAllow ? ["Write", "Edit", "NotebookEdit"] : null);
        await using var _ = session;

        var target = Path.Combine(data, "wiki", "owner-1", "pages", "Хроника.md");
        var pending = DecideAsync(session, "req-1", toolName, pathKey, target);
        await ResolveIfCardAppeared(session, sent, "req-1");

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("deny");
        lock (sent) sent.Should().BeEmpty("встроенный deny не порождает карточку разрешения");
    }

    // Чат с персоной в AcceptEdits — автономный: правку файла он на карточку не выносит
    // вовсе (решение 22.10), и без wiki-deny Edit ушёл бы «автономно» — allow мимо отказа.
    // Ветка вики стоит ПЕРЕД этой веткой (ревью S1c, Н4): отказ остаётся и здесь, без карточки.
    [Fact]
    public async Task ПерсонаВАcceptEdits_ЗаписьВФайлВики_ОтказБезКарточки()
    {
        var data = NewDir("data");
        var (session, sent, _) = NewSession(data, personaId: "p1", mode: ClaudeMode.AcceptEdits);
        await using var _ = session;

        var target = Path.Combine(data, "wiki", "owner-1", "pages", "Хроника.md");
        var pending = DecideAsync(session, "req-1", "Edit", "file_path", target);
        await ResolveIfCardAppeared(session, sent, "req-1");

        // На перенесённом ниже deny авто-allow вернёт "allow" немедленно, карточки не будет
        // вовсе — краснеет ровно этот первый признак, а не ожидание карточки.
        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("deny");
        lock (sent) sent.Should().BeEmpty("автономный allow acceptEdits-персоны не обходит отказ");
    }

    // Сам корень вики и путь через «..» — тоже вики (тот же канонический предикат, что в Notes).
    [Fact]
    public async Task ЗаписьВКореньВикиИлиЧерезТочки_ТожеОтказ()
    {
        var data = NewDir("data");
        var (session, sent, _) = NewSession(data);
        await using var _ = session;

        var toRoot = DecideAsync(session, "req-1", "Write", "file_path", Path.Combine(data, "wiki"));
        var throughDots = DecideAsync(session, "req-2", "Write", "file_path",
            Path.Combine(data, "wiki", "owner-1", "pages", "..", "Хроника.md"));
        await ResolveIfCardAppeared(session, sent, "req-1");
        await ResolveIfCardAppeared(session, sent, "req-2");

        (await toRoot.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("deny");
        (await throughDots.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("deny");
        lock (sent) sent.Should().BeEmpty();
    }

    // Относительный путь модель присылает относительно cwd процесса CLI — корня сессии,
    // а не cwd серверного процесса: корень проекта может быть предком data/ (ADR-035 §2),
    // и относительная запись в вику обязана опознаться как вики.
    [Fact]
    public async Task ОтносительныйПутьВВикиПодКорнемПроекта_ОпознаётсяИОтказывается()
    {
        var root = NewDir("root");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var (session, sent, _) = NewSession(data, root);
        await using var _ = session;

        var pending = DecideAsync(session, "req-1", "Edit", "file_path",
            Path.Combine("data", "wiki", "owner-1", "pages", "x.md"));
        await ResolveIfCardAppeared(session, sent, "req-1");

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("deny");
        lock (sent) sent.Should().BeEmpty();
    }

    // Контроль обратного: заметка проекта и файл под data/, но не в wiki, — прежнее решение
    // карточкой (механика не сломана: без карточки оставался бы любой Write).
    [Theory]
    [InlineData("project")]
    [InlineData("data-notes")]
    public async Task ФайлВнеВики_ПрежнееРешениеКарточкой(string where)
    {
        var data = NewDir("data");
        var (session, sent, root) = NewSession(data);
        await using var _ = session;
        var target = where == "project"
            ? Path.Combine(root, "notes", "x.md")
            : Path.Combine(data, "notes", "x.md");

        var pending = DecideAsync(session, "req-2", "Edit", "file_path", target);
        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        lock (sent) sent.OfType<PermissionRequestMessage>().Should().ContainSingle()
            .Which.ToolName.Should().Be("Edit");

        session.RespondPermission("req-2", "allow");
        (await pending).Should().Be("allow");
    }

    // data-корень не передан (тесты/старый контракт) — deny нет, поведение прежнее.
    [Fact]
    public async Task БезDataDir_РешениеНеМеняется()
    {
        var (session, sent, _) = NewSession(dataDir: null);
        await using var _ = session;

        var pending = DecideAsync(session, "req-3", "Write", "file_path", "/some/data/wiki/owner-1/pages/x.md");
        await WaitForAsync(() => { lock (sent) return sent.OfType<PermissionRequestMessage>().Any(); });
        session.RespondPermission("req-3", "deny");
        (await pending).Should().Be("deny");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "карточка разрешения так и не пришла");
            await Task.Delay(10);
        }
    }
}
