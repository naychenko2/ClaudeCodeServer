using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2c-10 (хвост ревью R-S2c-8, карточка 1ac0075a п.1): Upload и ToMarkdown поставлены под тот же
/// замок пути (_pathLocks), что SaveContent/Rename/Delete, и под тот же контракт If-Match. Раньше
/// обе двери писали мимо замка (FileStream FileMode.Create / files.WriteFile) и без сверки хеша:
/// параллельная правка того же пути молча затиралась, 409 не срабатывал. Двери не менялись — обе
/// уже на WriteContent (S2c-8); здесь проверяется конкурентность и контракт, а не право.
///
/// Как доказана «запись под замком» без таймеров. Сигнал «операция внутри своей критической
/// секции» — создание временного файла атомарной записи («.имя.<guid>.tmp», TreeExcludes.cs:60),
/// тот же признак, что в гонке Rename↔SaveContent (S2c-8). В этот момент проверяется замок
/// этого пути попыткой взять его нулевым ожиданием (FilesController.LockFor — internal шов, как
/// MaxEtagTextBytes; приём — OnboardingController.cs:154): с замком он занят самой записью —
/// взять нельзя; без замка (мутация) — свободен, и тест краснеет. Гонка двух писателей при этом
/// тоже прогоняется: второй стартует по сигналу и обязан быть отшитым по версии (409), а не
/// затереть победителя. Мутация ловится состоянием замка, а не надеждой на пересечение по
/// таймерам: измерено, что окно 50 МБ-записи ~50 мс, а приход второго HTTP-запроса ~80 мс —
/// на одном пересечении мутация не краснеет.
///
/// Конвертер ToMarkdown в тесте — печать файла как есть через Markitdown:Command («cat»/«type»):
/// гонка не зависит от хостового markitdown (его нет в CI) и её результат детерминирован.
/// </summary>
[Trait("Category", "Host")]
public class FilesControllerUploadToMarkdownLockTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly string _tempDir;

    public FilesControllerUploadToMarkdownLockTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        // Конвертер — печать файла как есть (тот же контракт markitdown: stdout → markdown).
        // На Windows — встроенный type; на Linux — cat. Хостовый markitdown тесту не нужен.
        _factory.ExtraConfig["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat";
        _tempDir = Path.Combine(factory.TempDir, "s2c10");
        Directory.CreateDirectory(_tempDir);
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient EditorClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    /// <summary>Проект владельца с файлами-фикстурами на диске; второй пользователь — участник
    /// с ролью editor: сценарий Глеба «владелец правит через SaveContent, редактор пишет тем же
    /// путём через Upload/ToMarkdown».</summary>
    private async Task<(string ProjectId, string Dir)> ProjectWithEditorAsync(
        params (string Path, string Content)[] fixtures)
    {
        // Флаг нужен обоим: без своего флага владелец не может приглашать, участник не видит проект
        var owner = OwnerClient();
        (await owner.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();
        (await EditorClient().PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

        var dir = Path.Combine(_tempDir, "r_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var (p, c) in fixtures) File.WriteAllText(Path.Combine(dir, p), c);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "S2C10" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();

        return (projectId, dir);
    }

    private async Task<string> GetETagAsync(string projectId, string path)
    {
        var response = await OwnerClient().GetAsync($"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}");
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.Tag;
        etag.Should().NotBeNull("тег S2c-1 выдаётся для текстового файла");
        return etag!;
    }

    private static string Sha256HexOfUtf8(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Sha256HexOfStream(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static HttpRequestMessage UploadRequest(string projectId, string fileName, string content, string? ifMatch)
    {
        // Путь цели — имя поля формы (query ?path= — каталог, не файл): как у двери в S2c-8
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/files/upload");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var part = new StringContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content = new MultipartFormDataContent { { part, "file", fileName } };
        return req;
    }

    private static HttpRequestMessage ToMarkdownRequest(string projectId, string path, string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/files/document/to-markdown");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        req.Content = JsonContent.Create(new { path });
        return req;
    }

    // «Замок этого пути занят записью» — попытка взять его нулевым ожиданием (тот же приём,
    // что OnboardingController.cs:154): взял — значит никто его не держит. Разрешённый permit
    // сразу возвращается: иначе следующий писатель этого пути ждал бы сам тест.
    private static async Task<bool> PathLockHeldByTheWriter(string absPath)
    {
        var probe = FilesController.LockFor(absPath);
        var acquired = await probe.WaitAsync(0);
        if (acquired) probe.Release();
        return !acquired;
    }

    private static TaskCompletionSource WatchAtomicTempWrite(FileSystemWatcher watcher, string targetFileName)
    {
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, e) =>
        {
            if (e.Name is not { } name) return;
            if (name.StartsWith("." + targetFileName + ".", StringComparison.Ordinal) &&
                name.EndsWith(".tmp", StringComparison.Ordinal))
                inside.TrySetResult();
        };
        return inside;
    }

    private static Task<HttpResponseMessage> SaveContentTaskAsync(
        HttpClient owner, string projectId, string path, string etag, string content) =>
        Task.Run(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put,
                $"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}");
            req.Headers.TryAddWithoutValidation("If-Match", etag);
            req.Content = JsonContent.Create(new { content });
            return await owner.SendAsync(req);
        });

    // c1: Upload с чужим If-Match — 409 file_changed (тело как у SaveContent), файл на диске цел;
    // без If-Match — прежнее поведение (перезапись), обратная совместимость S2c-1
    [Fact]
    public async Task Загрузка_СЧужимIfMatch_409ИФайлЦел_БезIfMatchКакРаньше()
    {
        var (projectId, dir) = await ProjectWithEditorAsync(("up.txt", "базовое содержимое"));
        var file = Path.Combine(dir, "up.txt");

        var denied = await EditorClient().SendAsync(UploadRequest(projectId, "up.txt", "поверх",
            "\"0000000000000000000000000000000000000000000000000000000000000000\""));
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await denied.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        body.GetProperty("content").GetString().Should().Be("базовое содержимое", "тело 409 — актуальное состояние, как у SaveContent");
        File.ReadAllText(file).Should().Be("базовое содержимое", "отказ не трогает файл");

        var plain = await EditorClient().SendAsync(UploadRequest(projectId, "up.txt", "поверх", null));
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(file).Should().Be("поверх", "без If-Match — перезапись, как до S2c-10");
    }

    // c1: ToMarkdown с чужим If-Match — 409 и целевой .md цел; с верным тегом — пишет под замком
    [Fact]
    public async Task ToMarkdown_СЧужимIfMatch_409ИЦельЦела_СВернымТегомПишет()
    {
        var (projectId, dir) = await ProjectWithEditorAsync(
            ("doc.txt", "конвертированное содержимое"), ("doc.md", "базовое содержимое"));
        var target = Path.Combine(dir, "doc.md");
        var client = EditorClient();

        var conflict = await client.SendAsync(ToMarkdownRequest(projectId, "doc.txt",
            "\"0000000000000000000000000000000000000000000000000000000000000000\""));
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await conflict.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        File.ReadAllText(target).Should().Be("базовое содержимое", "отказ не пишет целевой .md");

        var etag = await GetETagAsync(projectId, "doc.md");
        var ok = await client.SendAsync(ToMarkdownRequest(projectId, "doc.txt", etag));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(target).Should().Be("конвертированное содержимое");
    }

    // c2: гонка SaveContent владельца ↔ Upload редактора на один путь, без Delay/Sleep.
    // Держатель замка — сама загрузка (20 МБ стриминга во временный файл): к сигналу она
    // доказанно внутри своей критической секции, и в этот момент читается состояние замка пути.
    // Мутация «Upload без замка» (убрать WaitAsync/Release в Upload) → красный на состоянии
    // замка: семафор свободен, хотя запись этого пути идёт прямо сейчас.
    [Fact]
    public async Task ГонкаSaveContentПротивЗагрузки_ЗагрузкаДержитЗамокПути_ВторойПисательОтшитПоВерсии()
    {
        const int BodyChars = 20_000_000;
        var (projectId, dir) = await ProjectWithEditorAsync(("up.txt", "базовое содержимое"));
        var abs = Path.Combine(dir, "up.txt");
        var etag = await GetETagAsync(projectId, "up.txt");
        var body = new string('U', BodyChars);
        var editor = EditorClient();
        var owner = OwnerClient();

        using var watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
        var uploadInsideCriticalSection = WatchAtomicTempWrite(watcher, "up.txt");

        var uploadTask = Task.Run(() => editor.SendAsync(UploadRequest(projectId, "up.txt", body, etag)));

        // Сигнал = временный файл записи: подмены ещё нет, загрузка внутри критической секции
        await uploadInsideCriticalSection.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await PathLockHeldByTheWriter(abs)).Should().BeTrue(
            "загрузка держит замок пути — та же _pathLocks, что у SaveContent (без замка замок свободен)");

        var saveTask = SaveContentTaskAsync(owner, projectId, "up.txt", etag, "правка владельца");
        var responses = await Task.WhenAll(uploadTask, saveTask);

        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        statuses.Count(s => s >= 200 && s < 300).Should().Be(1,
            "под одним замком пути проходит ровно одна запись: " + string.Join(",", statuses));
        statuses.Count(s => s == 409).Should().Be(1,
            "проигравший отшит по версии, а не затёр победителя молча");

        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var conflict = JsonSerializer.Deserialize<JsonElement>(await loser.Content.ReadAsStringAsync());
        conflict.GetProperty("code").GetString().Should().Be("file_changed");

        Sha256HexOfStream(abs).Should().Be(Sha256HexOfUtf8(body),
            "файл целый — запись победителя целиком, не смесь и не молча потерянные данные");
    }

    // c2: то же для ToMarkdown: SaveContent владельца ↔ ToMarkdown редактора на один целевой .md.
    // Мутация «ToMarkdown без замка» → красный по той же схеме (состояние замка при сигнале).
    [Fact]
    public async Task ГонкаSaveContentПротивToMarkdown_MarkdownДержитЗамокПути_ВторойПисательОтшитПоВерсии()
    {
        const int SourceChars = 20_000_000;
        var source = new string('C', SourceChars);
        var (projectId, dir) = await ProjectWithEditorAsync(
            ("doc.txt", source), ("doc.md", "базовое содержимое"));
        var abs = Path.Combine(dir, "doc.md");
        var etag = await GetETagAsync(projectId, "doc.md");
        var editor = EditorClient();
        var owner = OwnerClient();

        using var watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
        var mdInsideCriticalSection = WatchAtomicTempWrite(watcher, "doc.md");

        var mdTask = Task.Run(() => editor.SendAsync(ToMarkdownRequest(projectId, "doc.txt", etag)));

        await mdInsideCriticalSection.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await PathLockHeldByTheWriter(abs)).Should().BeTrue(
            "запись конверта держит замок пути — та же _pathLocks, что у SaveContent (без замка замок свободен)");

        var saveTask = SaveContentTaskAsync(owner, projectId, "doc.md", etag, "правка владельца");
        var responses = await Task.WhenAll(mdTask, saveTask);

        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        statuses.Count(s => s >= 200 && s < 300).Should().Be(1,
            "под одним замком пути проходит ровно одна запись: " + string.Join(",", statuses));
        statuses.Count(s => s == 409).Should().Be(1,
            "проигравший отшит по версии, а не затёр победителя молча");

        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var conflict = JsonSerializer.Deserialize<JsonElement>(await loser.Content.ReadAsStringAsync());
        conflict.GetProperty("code").GetString().Should().Be("file_changed");

        Sha256HexOfStream(abs).Should().Be(Sha256HexOfUtf8(source),
            "целевой .md целый — запись победителя целиком, не смесь и не молча потерянные данные");
    }
}
