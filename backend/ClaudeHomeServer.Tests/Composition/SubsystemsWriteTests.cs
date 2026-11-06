using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

/// <summary>
/// S2c-1: PUT /api/admin/subsystems/{key} — запись тумблера панели «Компоненты» в
/// data/subsystems.json. Тесты уровня ХОСТА: реальная маршрутизация MVC и реальная
/// регистрация подсистем на старте хоста (состав snapshot, а не только форма ответа).
///
/// Хост — один на класс (храповик HostPerClassRatchetGuardTests); свои хосты поднимаются только
/// там, где их требует предмет теста: «битый файл» пишет состояние своего каталога, «рестарт»
/// читает каталог классного хоста новым процессом — по образцу SubsystemUsageAttributionTests.
///
/// Путь файла: тест берёт его из хост-конфигурации через SubsystemOverridesFile.PathFor —
/// тот же резолв, что у контроллера, поэтому совпадение по построению, а не по соглашению.
///
/// ═══ Почему DataPath «рестартового» хоста идёт через UseSetting, а не ExtraConfig ═══
///
/// Файл тумблеров читается в Program.cs ДО builder.Build() (мой AddJsonFile стоит перед
/// AddSubsystems, который читает гейт из конфигурации). ConfigureAppConfiguration-колбэки
/// фабрики — встроенный InMemory и ExtraConfig — отложены и до этой точки не доходят:
/// та же грабля, что разобрана в шапке NotesDisabledTests. UseSetting разворачивается в
/// аргументы командной строки точки входа и успевает в конфигурацию до AddSubsystems.
/// </summary>
[Trait("Category", "Host")]
public class SubsystemsWriteTests(SubsystemsWriteTests.Hosts hosts) : IClassFixture<SubsystemsWriteTests.Hosts>
{
    /// <summary>
    /// Классный хост + фабрика дополнительных хостов. Создание фабрики — только внутри этого
    /// типа (требование HostPerClassRatchetGuardTests): тесты зовут методы, не new.
    /// </summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        // Override DataPath для «рестартового» хоста; применяется в ConfigureWebHost через
        // UseSetting (командная строка точки входа) — единственный путь, доезжающий до
        // чтения конфигурации в Program.cs до Build (см. шапку класса).
        private string? _dataPathOverride;

        /// <summary>Хост со своим каталогом данных — тестам, чей предмет не должен
        /// пересекаться с состоянием классного хоста.</summary>
        public Hosts Fresh() => new();

        /// <summary>«Рестарт»: новый хост, читающий каталог данных этого хоста.</summary>
        public Hosts RestartOnSameData()
        {
            var next = new Hosts();
            next._dataPathOverride = Path.Combine(TempDir, "projects.json");
            return next;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_dataPathOverride is not null)
                builder.UseSetting("DataPath", _dataPathOverride);
        }
    }

    private readonly Hosts _hosts = hosts;

    private static string FilePathOf(TestWebApplicationFactory factory) =>
        SubsystemOverridesFile.PathFor(factory.Services.GetRequiredService<IConfiguration>());

    // ─── Запись и ответ ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_ВыключаетTts_ОтветЧестный_ФайлЗаписан()
    {
        var client = _hosts.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        row.GetProperty("key").GetString().Should().Be("tts");
        row.GetProperty("enabled").GetBoolean().Should().BeFalse(
            "ответ отражает только что записанное значение, а не конфиг старта процесса");
        row.GetProperty("active").GetBoolean().Should().BeTrue(
            "текущий процесс всё ещё зарегистрировал tts: гейт читается один раз при регистрации (ADR-014)");
        row.GetProperty("restartRequired").GetBoolean().Should().BeTrue(
            "enabled=false при active=true — изменение подействует только после перезапуска");

        // Файл записан по-настоящему и в той форме, которую читает гейт.
        var file = SubsystemOverridesFile.ReadEnabled(FilePathOf(_hosts));
        file.Should().ContainKey("tts");
        file["tts"].Should().BeFalse();
    }

    [Fact]
    public async Task Put_ВключаетОбратно_ОтветЧестный()
    {
        var client = _hosts.CreateAuthenticatedClient();

        var off = await client.PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK);

        var on = await client.PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = true });
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await on.Content.ReadFromJsonAsync<JsonElement>();
        row.GetProperty("enabled").GetBoolean().Should().BeTrue();
        row.GetProperty("active").GetBoolean().Should().BeTrue();
        row.GetProperty("restartRequired").GetBoolean().Should().BeFalse(
            "значение совпало с состоянием процесса — рестарт больше не нужен");

        SubsystemOverridesFile.ReadEnabled(FilePathOf(_hosts))["tts"].Should().BeTrue();
    }

    // ─── Отказы ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_НеАдмин_403_ФайлаНеКасается()
    {
        var path = FilePathOf(_hosts);
        var before = SubsystemOverridesFile.ReadEnabled(path);
        var client = _hosts.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "[Authorize(Roles=\"admin\")] на контроллере: пользователь без роли admin не пишет конфигурацию подсистем");
        SubsystemOverridesFile.ReadEnabled(path).Should().BeEquivalentTo(before,
            "отказ по роли — до всякой записи");
    }

    [Fact]
    public async Task Put_НеизвестныйКлюч_404()
    {
        var path = FilePathOf(_hosts);
        var before = SubsystemOverridesFile.ReadEnabled(path);
        var client = _hosts.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/api/admin/subsystems/no-such-vertical", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "ключа нет в снимке состава — писать не во что");
        SubsystemOverridesFile.ReadEnabled(path).Should().BeEquivalentTo(before,
            "запись происходит только после проверки ключа");
    }

    // Строка ядра (префикс core:, RecordCore — гейта нет вовсе) и вертикали, объявленные
    // ядром в классе (CanDisable=false, метка «ядро/сквозная» в SubsystemDisableMatrixTests).
    [Theory]
    [InlineData("core:sessions")]
    [InlineData("git")]
    [InlineData("tasks")]
    public async Task Put_ЯдроИНеотключаемое_400CoreSubsystem(string key)
    {
        var client = _hosts.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync($"/api/admin/subsystems/{key}", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("core_subsystem");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace(
            "панель показывает причину в строке — пустое сообщение бессмысленно для человека");

        SubsystemOverridesFile.ReadEnabled(FilePathOf(_hosts)).Should().NotContainKey(key);
    }

    [Fact]
    public async Task Put_БезПоляEnabled_400НеМолчаВыключает()
    {
        var client = _hosts.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/api/admin/subsystems/tts", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "отсутствие поля не трактуется как «выключить»: это ошибка клиента, а не решение о подсистеме");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("enabled_required");
    }

    // ─── Битый файл ───────────────────────────────────────────────────────────────

    // Свой хост: предмет теста — состояние файла, и оставлять битый файл в каталоге
    // классного хоста нельзя — следующие тесты ловили бы 503 по чужому состоянию.
    [Fact]
    public async Task Put_БитыйФайл_503СПричиной_ФайлНеТронут()
    {
        using var own = _hosts.Fresh();
        var path = FilePathOf(own);
        File.WriteAllText(path, "{ это не json");
        var before = File.ReadAllBytes(path);

        var response = await own.CreateAuthenticatedClient()
            .PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "битый файл в data/ — положение дел клиента, а не падение сервера: 503 с причиной");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("write_failed");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace(
            "её показывает строка панели: человек должен отличить «файл битый» от «сервер упал»");

        File.ReadAllBytes(path).Should().Equal(before,
            "чтение оборвалось до записи — файл остаётся ровно таким, каким был");
    }

    // Валидный JSON не той схемы: JsonException здесь нет, парсер проходит, форма — нет.
    // Без проверки ValueKind в FindProperty EnumerateObject() бросает
    // InvalidOperationException, который не ловит ни Parse, ни контроллер → 500 без code.
    [Theory]
    [InlineData("{\"Subsystems\":{\"tts\":5}}")] // значение записи — не объект
    [InlineData("{\"Subsystems\":5}")]           // секция — не объект
    [InlineData("[]")]                          // корень — массив
    [InlineData("5")]                           // корень — число
    public async Task Put_ВалидныйJSONНеТойСхемы_503СПричиной_ФайлНеТронут(string content)
    {
        using var own = _hosts.Fresh();
        var path = FilePathOf(own);
        File.WriteAllText(path, content);
        var before = File.ReadAllBytes(path);

        var response = await own.CreateAuthenticatedClient()
            .PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "ручка никогда не отдаёт 500 на содержимое файла: неверная форма — 503 с причиной");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("write_failed");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace(
            "в ней должно быть видно, какой именно фрагмент не объект");

        File.ReadAllBytes(path).Should().Equal(before,
            "ошибка формы — до записи, файл остаётся ровно таким, каким был");
    }

    // ─── Гонка двух записей ───────────────────────────────────────────────────────

    [Fact]
    public async Task Put_TwoKeysInParallel_BothPersisted()
    {
        var path = FilePathOf(_hosts);

        // Файл засеян большим документом: чтение и разбор занимают миллисекунды, и два
        // конкурентных запроса надёжно пересекаются в критической секции. Без замка пути
        // этот тест устойчиво теряет одно из значений; с замком — зелёный детерминированно.
        const int seedCount = 3000;
        var seed = new StringBuilder("{\"Subsystems\":{");
        for (var i = 0; i < seedCount; i++)
            seed.Append(i > 0 ? "," : "").Append("\"seed").Append(i).Append("\":{\"Enabled\":true}");
        seed.Append("}}");
        File.WriteAllText(path, seed.ToString());

        var client = _hosts.CreateAuthenticatedClient();
        // Два реальных отключаемых ключа (несуществующий ключ отбил бы 404 до записи) —
        // на отдельных задачах, чтобы критическая секция реально выполнялась параллельно.
        var first = Task.Run(() => client.PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false }));
        var second = Task.Run(() => client.PutAsJsonAsync("/api/admin/subsystems/video", new { enabled = false }));
        var responses = await Task.WhenAll(first, second);
        foreach (var response in responses)
            response.StatusCode.Should().Be(HttpStatusCode.OK);

        var file = SubsystemOverridesFile.ReadEnabled(path);
        file.Should().ContainKey("tts", "первое значение не должно быть потеряно вторым");
        file["tts"].Should().BeFalse();
        file.Should().ContainKey("video", "второе значение не должно быть потеряно первым");
        file["video"].Should().BeFalse();
        file.Count.Should().Be(seedCount + 2, "обе записи легли поверх прежнего содержимого, а не вместо него");
    }

    // ─── Перезапуск: файл доезжает до нового процесса ─────────────────────────────

    [Fact]
    public async Task Put_ПослеЗаписи_НовыйХостНаТомЖеКаталоге_НеАктивируетПодсистему()
    {
        var response = await _hosts.CreateAuthenticatedClient()
            .PutAsJsonAsync("/api/admin/subsystems/tts", new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(FilePathOf(_hosts)).Should().BeTrue();

        // «Рестарт»: новый хост на том же каталоге данных.
        using var second = _hosts.RestartOnSameData();
        var rows = await second.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        var tts = rows.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "tts");

        tts.GetProperty("active").GetBoolean().Should().BeFalse(
            "гейт на регистрации прочитал Subsystems:tts:Enabled=false из data/subsystems.json");
        tts.GetProperty("enabled").GetBoolean().Should().BeFalse();
        tts.GetProperty("restartRequired").GetBoolean().Should().BeFalse(
            "конфиг и состояние процесса совпали — рестарт больше не нужен");
    }
}
