using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2c-8: двери записи содержимого файлов переведены поимённо на WriteContent — редактору общего
/// проекта (разрез f899602f §6/§7). Первая таблица — семь переведённых дверей: владелец и редактор
/// пишут (любой 2xx), файл лежит под корнем проекта арендатора (ключ хранилищ — директория проекта,
/// а не вызывающего); читатель — 403 с машинным кодом project_scope (существование проекта ему и так
/// известно), посторонний — 404. Вторая таблица — перечень дверей, оставшихся на Write:
/// Revert, SaveFromUrl, office-config/discard/force-save, удаление папки: редактору и читателю 403,
/// владельцу гейт открыт. ToMarkdown для владельца и редактора — PassesGate: результат двери зависит
/// от хостового markitdown (опциональный инструмент, ADR-005), а не от права; здесь проверяется право.
/// </summary>
[Trait("Category", "Host")]
public class FilesControllerWriteContentTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»; -1 (PassesGate) — «не 403»: гейт права пройден,
    // ручка вправе ответить по делу (400 «нечего откатывать», 502 «нет markitdown»)
    private const int Ok2xx = 200;
    private const int PassesGate = -1;

    private readonly TestWebApplicationFactory _factory;
    private readonly string _tempDir;

    public FilesControllerWriteContentTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _tempDir = Path.Combine(factory.TempDir, "wc");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Дверь: метод, суффикс пути, тело (null — без тела), multipart, фикстуры на диске
    /// до вызова, проверка диска после вызова (для строки редактора) и коды по ролям.</summary>
    private sealed record Door(
        string Name, string Method, string PathSuffix, object? Body, bool Multipart,
        string? FixtureFile, string? FixtureDir, Action<string>? VerifyOnDisk,
        int ForOwner, int ForEditor, int ForReader = 403, int ForOutsider = 404)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    // Переведённые двери (S2c-8): ровно эти семь, снимок держит WriteContentDoorsGuardTests
    private static readonly Door[] WriteContentDoors =
    [
        new("запись содержимого (SaveContent)", "PUT", "/content?path=f.txt",
            new { content = "правка редактора" }, false, null, null,
            dir => File.ReadAllText(Path.Combine(dir, "f.txt")).Should().Be("правка редактора"),
            Ok2xx, Ok2xx),
        new("создание файла (CreateFile)", "POST", "/create",
            new { path = "new.txt", content = "создан" }, false, null, null,
            dir => File.ReadAllText(Path.Combine(dir, "new.txt")).Should().Be("создан"),
            Ok2xx, Ok2xx),
        new("создание папки (CreateDir)", "POST", "/mkdir",
            new { path = "newdir" }, false, null, null,
            dir => Directory.Exists(Path.Combine(dir, "newdir")).Should().BeTrue(),
            Ok2xx, Ok2xx),
        new("переименование (Rename)", "POST", "/rename",
            new { oldPath = "old.txt", newPath = "moved.txt" }, false, "old.txt", null,
            dir =>
            {
                File.ReadAllText(Path.Combine(dir, "moved.txt")).Should().Be("базовое содержимое");
                File.Exists(Path.Combine(dir, "old.txt")).Should().BeFalse();
            },
            Ok2xx, Ok2xx),
        new("загрузка файла (Upload)", "POST", "/upload", null, true, null, null,
            dir => File.ReadAllText(Path.Combine(dir, "up.txt")).Should().Be("загружено"),
            Ok2xx, Ok2xx),
        new("удаление файла (Delete)", "DELETE", "/?path=gone.txt", null, false, "gone.txt", null,
            dir => File.Exists(Path.Combine(dir, "gone.txt")).Should().BeFalse(),
            Ok2xx, Ok2xx),
        new("конвертация в Markdown (ToMarkdown)", "POST", "/document/to-markdown",
            new { path = "doc.txt" }, false, "doc.txt", null, null,
            PassesGate, PassesGate),
    ];

    // Двери, оставшиеся на Write (перечень явный — с2 принятой части): участнику закрыты
    private static readonly Door[] WriteDoors =
    [
        new("откат (Revert)", "POST", "/revert", new { path = "r.txt" }, false, "r.txt", null, null,
            PassesGate, 403),
        new("сохранение из URL (SaveFromUrl)", "POST", "/save-from-url",
            new { url = "https://example.invalid/x.txt", path = "s.txt" }, false, null, null, null,
            PassesGate, 403),
        new("конфиг OnlyOffice (OfficeConfig)", "GET", "/office-config?path=o.docx", null, false, "o.docx", null, null,
            PassesGate, 403),
        new("отмена в OnlyOffice (OfficeDiscard)", "POST", "/office-discard?path=o.docx", null, false, "o.docx", null, null,
            PassesGate, 403),
        new("принудительное сохранение (OfficeForceSave)", "POST", "/office-force-save?path=o.docx", null, false, "o.docx", null, null,
            PassesGate, 403),
        new("удаление папки (Delete, папка)", "DELETE", "/?path=sub", null, false, null, "sub",
            dir => Directory.Exists(Path.Combine(dir, "sub")).Should().BeTrue("отказ права не трогает папку"),
            Ok2xx, 403),
    ];

    public static IEnumerable<object[]> WriteContentMatrix() =>
        from d in WriteContentDoors
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { d.Name, role };

    public static IEnumerable<object[]> WriteMatrix() =>
        from d in WriteDoors
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { d.Name, role };

    [Theory]
    [MemberData(nameof(WriteContentMatrix))]
    public async Task ДвериWriteContent_РедакторПишетПодКорнемАрендатора_Читатель403(string doorName, string role) =>
        await RunDoorAsync(WriteContentDoors.Single(d => d.Name == doorName), role);

    [Theory]
    [MemberData(nameof(WriteMatrix))]
    public async Task ДвериWrite_ОстаютсяЗакрытымиДляУчастника(string doorName, string role) =>
        await RunDoorAsync(WriteDoors.Single(d => d.Name == doorName), role);

    private async Task RunDoorAsync(Door door, string role)
    {
        var (projectId, dir) = await ProjectWithMemberAsync(role, door.FixtureFile, door.FixtureDir);
        var (status, body) = await SendAsync(ClientOf(role), door, projectId);

        var expected = door.For(role);
        if (expected == Ok2xx)
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {door.Name} → {status}");
        else if (expected == PassesGate)
            status.Should().NotBe(403, $"{role}: {door.Name} — владельцу гейт открыт");
        else
        {
            status.Should().Be(expected, $"{role}: {door.Name}");
            if (expected == 403)
                body.Should().Contain("project_scope", $"{role}: {door.Name} — отказ гейта права, а не прочего");
        }

        // c1: файл редактора лежит под корнем проекта арендатора — той самой директорией,
        // которую создал владелец; своего «проекта участника» в хранилищах нет
        if (role == Editor && expected == Ok2xx) door.VerifyOnDisk?.Invoke(dir);
    }

    // Ветка, которой нет в таблицах: один и тот же DELETE по признаку пути.
    // Владелец удаляет папку как раньше; редактору файл-дверь не даёт права на папку (403),
    // и отказ не трогает её содержимое
    [Fact]
    public async Task УдалениеПапки_ВладельцуКакРаньше_Редактору403_ИПапкаЦела()
    {
        var (projectId, dir) = await ProjectWithMemberAsync(Editor, null, "sub");
        var sub = Path.Combine(dir, "sub");
        File.WriteAllText(Path.Combine(sub, "inner.txt"), "внутри");

        var denied = await MemberClient().DeleteAsync($"/api/projects/{projectId}/files?path=sub");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("project_scope");
        Directory.Exists(sub).Should().BeTrue("отказ не трогает папку");
        File.ReadAllText(Path.Combine(sub, "inner.txt")).Should().Be("внутри");

        var ok = await OwnerClient().DeleteAsync($"/api/projects/{projectId}/files?path=sub");
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(sub).Should().BeFalse("владелец удаляет папку как раньше");
    }

    // c3: гонка Rename↔SaveContent на один путь под одним замком (_pathLocks), без таймеров.
    // Пересечение фаз — структурное, по образцу NotesConcurrentCreateTests: сигнал что
    // SaveContent ВНУТРИ своей критической секции — создание временного файла WriteAtomic
    // (тот же признак, которым наблюдатель глушит эти записи: «.» + имя + guid + «.tmp»,
    // TreeExcludes.cs:60). Created-событие означает «сверка If-Match уже пройдена, подмены
    // ещё нет»: окно от сигнала до подмены — остаток записи 50 МБ, сотни мс. Rename стартует
    // строго по сигналу: с замком он встаёт в очередь за ним и отшивается по версии (409);
    // без замка (мутация) успевает проверить ещё целую старую версию и перенести её —
    // два 2xx и два файла на диске, красный. Ровно одна операция проходит и данные целы —
    // только когда обе двери держат один замок пути.
    [Fact]
    public async Task ГонкаRenameПротивSaveContent_ПодОднимЗамком_РовноОдинПобеждает_ДанныеНеПотеряны()
    {
        var (projectId, dir) = await ProjectWithMemberAsync(Editor, "race.txt", null);
        var oldPath = Path.Combine(dir, "race.txt");
        var movedPath = Path.Combine(dir, "moved.txt");
        var etag = await GetETagAsync(projectId, "race.txt");
        var newContent = new string('x', 50_000_000);

        using var watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
        var saveInsideCriticalSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, e) =>
        {
            if (e.Name is not { } name) return;
            if (name.StartsWith(".race.txt.", StringComparison.Ordinal) &&
                name.EndsWith(".tmp", StringComparison.Ordinal))
                saveInsideCriticalSection.TrySetResult();
        };

        var client = ClientOf(Editor);
        var saveTask = Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put,
                $"/api/projects/{projectId}/files/content?path=race.txt");
            req.Headers.TryAddWithoutValidation("If-Match", etag);
            req.Content = JsonContent.Create(new { content = newContent });
            return await client.SendAsync(req);
        });

        // Вторая операция стартует по событию, а не по времени: к сигналу SaveContent
        // уже держит замок и ещё не подменил файл
        await saveInsideCriticalSection.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var renameTask = Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/files/rename");
            req.Headers.TryAddWithoutValidation("If-Match", etag);
            req.Content = JsonContent.Create(new { oldPath = "race.txt", newPath = "moved.txt" });
            return await client.SendAsync(req);
        });

        var responses = await Task.WhenAll(saveTask, renameTask);

        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        statuses.Count(s => s >= 200 && s < 300).Should().Be(1,
            "под одним замком пути проходит ровно одна операция: " + string.Join(",", statuses));
        statuses.Count(s => s is 409 or 404).Should().Be(1,
            "проигравший отшит по версии или отсутствию, а не молча затёр победителя");

        var survivors = new[] { oldPath, movedPath }.Where(File.Exists).ToList();
        survivors.Count.Should().Be(1, "после гонки на диске ровно один файл — версия победителя");
        if (survivors[0] == movedPath)
            File.ReadAllText(movedPath).Should().Be("базовое содержимое", "победил rename — старое содержимое на новом пути");
        else
            File.ReadAllText(oldPath).Should().Be(newContent, "победил SaveContent — новый текст на старом пути");
    }

    // If-Match на Rename без гонки: назван не тот версии — 409, файл не переименован;
    // без If-Match — прежнее поведение (обратная совместимость, как у SaveContent в S2c-1)
    [Fact]
    public async Task Rename_СЧужимIfMatch_409ИФайлНаМесте_БезIfMatchКакРаньше()
    {
        var (projectId, dir) = await ProjectWithMemberAsync(Editor, "a.txt", null);
        var client = ClientOf(Editor);

        var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/files/rename");
        stale.Headers.TryAddWithoutValidation("If-Match", "\"0000000000000000000000000000000000000000000000000000000000000000\"");
        stale.Content = JsonContent.Create(new { oldPath = "a.txt", newPath = "b.txt" });
        var denied = await client.SendAsync(stale);
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.Exists(Path.Combine(dir, "a.txt")).Should().BeTrue("отказ не переименовывает файл");
        File.Exists(Path.Combine(dir, "b.txt")).Should().BeFalse();

        var plain = await client.PostAsJsonAsync($"/api/projects/{projectId}/files/rename",
            new { oldPath = "a.txt", newPath = "b.txt" });
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(dir, "b.txt")).Should().Be("базовое содержимое");
    }

    // If-Match на Delete файла: не та версия — 409 и файл цел; та версия — 204
    [Fact]
    public async Task Delete_СЧужимIfMatch_409ИФайлЦел_СВерным_204()
    {
        var (projectId, dir) = await ProjectWithMemberAsync(Editor, "d.txt", null);
        var client = ClientOf(Editor);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{projectId}/files?path=d.txt");
        del.Headers.TryAddWithoutValidation("If-Match", "\"0000000000000000000000000000000000000000000000000000000000000000\"");
        var denied = await client.SendAsync(del);
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.ReadAllText(Path.Combine(dir, "d.txt")).Should().Be("базовое содержимое", "отказ не удаляет файл");

        var etag = await GetETagAsync(projectId, "d.txt");
        var ok = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{projectId}/files?path=d.txt");
        ok.Headers.TryAddWithoutValidation("If-Match", etag);
        (await client.SendAsync(ok)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(dir, "d.txt")).Should().BeFalse();
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient MemberClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient ClientOf(string role) => role == Owner ? OwnerClient() : MemberClient();

    /// <summary>Проект владельца (с файлом/папкой-фикстурой на диске); второй пользователь —
    /// участник роли role и со своим флагом общих проектов; для owner/outsider участника нет.</summary>
    private async Task<(string ProjectId, string Dir)> ProjectWithMemberAsync(
        string role, string? fixtureFile, string? fixtureDir)
    {
        // Флаг нужен обоим: без своего флага владелец не может приглашать (ProjectMembershipService
        // AddAsync: FlagOn(caller) и FlagOn(target)), без своего флага участник не видит проект
        var owner = OwnerClient();
        (await owner.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();
        (await MemberClient().PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

        var dir = Path.Combine(_tempDir, "wc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        if (fixtureFile is not null) File.WriteAllText(Path.Combine(dir, fixtureFile), "базовое содержимое");
        if (fixtureDir is not null) Directory.CreateDirectory(Path.Combine(dir, fixtureDir));

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "WC" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        if (role is Editor or Reader)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        return (projectId, dir);
    }

    private static async Task<(int Status, string Body)> SendAsync(HttpClient client, Door door, string projectId)
    {
        var request = new HttpRequestMessage(new HttpMethod(door.Method),
            $"/api/projects/{projectId}/files{door.PathSuffix}");
        if (door.Multipart)
        {
            var file = new StringContent("загружено");
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var form = new MultipartFormDataContent { { file, "file", "up.txt" } };
            request.Content = form;
        }
        else if (door.Body is not null) request.Content = JsonContent.Create(door.Body);

        var response = await client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> GetETagAsync(string projectId, string path)
    {
        var response = await ClientOf(Editor).GetAsync($"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}");
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.Tag;
        etag.Should().NotBeNull("тег S2c-1 должен выдаваться для текстового файла");
        return etag!;
    }
}
