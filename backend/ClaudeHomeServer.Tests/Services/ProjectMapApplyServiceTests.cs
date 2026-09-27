using System.Text;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Контракт записи Р10а — самая опасная половина уборки карты: всё остальное читает, эта
// пишет в CLAUDE.md. Каждый кейс закрывает конкретный способ потерять чужой текст.
// Пути от Path.GetTempPath() + Path.Combine: набор гоняется и на Linux в CI.
public class ProjectMapApplyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectMapScanner _scanner = new();
    private readonly ProjectMapApplyService _apply;

    public ProjectMapApplyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccs-map-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _apply = new ProjectMapApplyService(_scanner, NullLogger<ProjectMapApplyService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string MapPath => Path.Combine(_root, "CLAUDE.md");

    // Байты, а не текст: BOM и окончания строк проверяются только на них
    private byte[] MapBytes() => File.ReadAllBytes(MapPath);

    private void WriteBytes(string path, byte[] bytes)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private void Write(string path, string content) =>
        WriteBytes(path, Encoding.UTF8.GetBytes(content));

    // Типовая карта: мёртвая ссылка на переехавший файл, кандидат в проекте ровно один
    private void WriteMapWithDeadLink(string map) => Write("CLAUDE.md", map);

    private void WriteCandidate() => Write("backend/Core/Models/FeatureFlag.cs", "// код");

    private static string DeadLinkId(ProjectMapScanner scanner, string root)
    {
        var report = scanner.Scan(root);
        return report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;
    }

    // ─── что применяется ────────────────────────────────────────────────────

    // Живой путь целиком: скан дал патч, применение починило ссылку, отчёт вернулся по
    // ИТОГОВОМУ тексту — фронту не нужен второй запрос ради перерисовки шапки
    [Fact]
    public async Task МёртваяСсылкаСЕдинственнымКандидатом_Чинится()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var suggestion = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink);
        suggestion.Apply.Should().NotBeNull();
        suggestion.Apply!.After.Should().Be("[флаги](backend/Core/Models/FeatureFlag.cs)");

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [suggestion.Id]);

        result.Stale.Should().BeFalse();
        result.Applied.Should().Equal(suggestion.Id);
        result.Failed.Should().BeEmpty();
        File.ReadAllText(MapPath).Should().Be("# Карта\n\n[флаги](backend/Core/Models/FeatureFlag.cs)\n");
        // newSha соответствует записанному файлу: он взят из скана итогового текста
        result.NewSha.Should().Be(_scanner.Scan(_root).BaseSha);
        result.Scan!.DeadLinkCount.Should().Be(0);
    }

    // Якорь встречается в тексте один раз и один раз внутри забора — это ОДИН кандидат,
    // и замена состоится именно в тексте, а пример внутри забора обязан уцелеть.
    // Проверяются оба вида заборов: ``` и ~~~
    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    public async Task ЯкорьВТекстеИВЗаборе_ЗаменяетсяТолькоВТексте(string fence)
    {
        WriteCandidate();
        WriteMapWithDeadLink(
            $"# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n\n{fence}\n" +
            $"[флаги](backend/Models/FeatureFlag.cs)\n{fence}\n");
        var report = _scanner.Scan(_root);
        var suggestion = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink);
        suggestion.Apply.Should().NotBeNull("вхождение внутри забора не считается вторым кандидатом");

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [suggestion.Id]);

        result.Applied.Should().Equal(suggestion.Id);
        var text = File.ReadAllText(MapPath);
        text.Should().Contain("[флаги](backend/Core/Models/FeatureFlag.cs)");
        // Пример внутри забора не тронут: замена по подстроке иначе залезла бы в него
        text.Should().Contain($"{fence}\n[флаги](backend/Models/FeatureFlag.cs)\n{fence}");
    }

    // Якорь есть ТОЛЬКО внутри забора: сканер такой ссылки не видит вовсе, значит кнопки
    // у человека нет, а присланный по формуле id не находится в свежем скане. Файл цел
    [Fact]
    public async Task ЯкорьТолькоВнутриЗабора_ПравкаНеПрименяется_ФайлЦел()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n```\n[флаги](backend/Models/FeatureFlag.cs)\n```\n");
        var report = _scanner.Scan(_root);
        report.Suggestions.Should().NotContain(s => s.Kind == MapSuggestions.KindDeadLink);
        var before = MapBytes();
        var id = MapSuggestions.Id(MapSuggestions.KindDeadLink, "[флаги](backend/Models/FeatureFlag.cs)");

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Applied.Should().BeEmpty();
        result.Failed.Single().Reason.Should().Be(MapApplyReasons.UnknownId);
        MapBytes().Should().Equal(before);
    }

    // Два вхождения якоря вне заборов: куда чинить — решение человека, и предложение
    // рождается БЕЗ кнопки. Файл не тронут (сверка по байтам до и после)
    [Fact]
    public async Task ДваВхожденияЯкоряВнеЗаборов_ПатчаНет_ФайлНеТронут()
    {
        WriteCandidate();
        WriteMapWithDeadLink(
            "# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n\nи ещё раз: [флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var suggestion = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink);
        suggestion.Apply.Should().BeNull("неуникальный якорь не расширяется контекстом, а обнуляет патч");
        var before = MapBytes();

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [suggestion.Id]);

        result.Applied.Should().BeEmpty();
        result.Failed.Single().Reason.Should().Be(MapApplyReasons.NotApplicable);
        MapBytes().Should().Equal(before);
    }

    // Одна правка не нашла своего предложения, вторая нашла: честный отчёт вместо мнимой
    // атомарности — применённое применено, непрошедшее названо по имени
    [Fact]
    public async Task ОднаПравкаНеПрошла_ОстальныеПрименяются()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink).Id;

        var result = await _apply.ApplyAsync(_root, report.BaseSha, ["deadbeefdeadbeef", id]);

        result.Applied.Should().Equal(id);
        result.Failed.Single().Should().Be(new MapApplyFailure("deadbeefdeadbeef", MapApplyReasons.UnknownId));
        File.ReadAllText(MapPath).Should().Contain("backend/Core/Models/FeatureFlag.cs");
        result.NewSha.Should().Be(_scanner.Scan(_root).BaseSha);
    }

    // Путь встречается в ссылке трижды: в подписи, в адресе и в якоре. Заменить обязано
    // ровно адрес — поиск «последнего вхождения пути» переписал бы якорь, а «первого» —
    // подпись, и человек увидел бы в git-диффе испорченную ссылку вместо починенной
    [Fact]
    public async Task ПутьВПодписиИВЯкоре_ЗаменяетсяТолькоАдрес()
    {
        Write("backend/Core/Models/FeatureFlag.cs", "// код");
        WriteMapWithDeadLink(
            "# Карта\n\n[backend/Models/FeatureFlag.cs](backend/Models/FeatureFlag.cs#backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var suggestion = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink);

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [suggestion.Id]);

        result.Applied.Should().HaveCount(1);
        File.ReadAllText(MapPath).Should().Be(
            "# Карта\n\n[backend/Models/FeatureFlag.cs](backend/Core/Models/FeatureFlag.cs#backend/Models/FeatureFlag.cs)\n");
    }

    // ─── BOM и окончания строк ──────────────────────────────────────────────

    // Построчный разбор со склейкой через '\n' превратил бы CRLF-файл в LF одной правкой:
    // дифф на весь файл вместо одной строки, и карту после этого не отревьюить
    [Fact]
    public async Task ФайлСCRLF_ОстаётсяСCRLF()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\r\n\r\n[флаги](backend/Models/FeatureFlag.cs)\r\n\r\n## Раздел\r\n");
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink).Id;

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Applied.Should().HaveCount(1);
        var text = Encoding.UTF8.GetString(MapBytes());
        text.Should().Be("# Карта\r\n\r\n[флаги](backend/Core/Models/FeatureFlag.cs)\r\n\r\n## Раздел\r\n");
        // Ни одного одинокого LF: проверка на самих байтах, а не на ReadAllText
        text.Replace("\r\n", "").Should().NotContain("\n");
    }

    // ReadAllText BOM съедает молча, WriteAllText по умолчанию его не пишет: без явной
    // передачи признака файл с BOM потерял бы его на первой же правке
    [Fact]
    public async Task ФайлСBOM_ОстаётсяСBOM()
    {
        WriteCandidate();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        WriteBytes("CLAUDE.md", [.. bom,
            .. Encoding.UTF8.GetBytes("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n")]);
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink).Id;

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Applied.Should().HaveCount(1);
        MapBytes().Take(3).Should().Equal(bom);
        File.ReadAllText(MapPath).Should().Be("# Карта\n\n[флаги](backend/Core/Models/FeatureFlag.cs)\n");
    }

    // Файла без BOM новый BOM не приобретает: кодировка записи — ровно по факту исходного
    [Fact]
    public async Task ФайлБезBOM_BOMНеПоявляется()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.Single(s => s.Kind == MapSuggestions.KindDeadLink).Id;

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        // Ассерт на применение обязателен: иначе тест зеленел бы и на apply, который
        // перестал что-либо писать вовсе — файл остался бы исходным, без BOM
        result.Applied.Should().HaveCount(1);
        MapBytes()[0].Should().NotBe(0xEF);
    }

    // Карта не в UTF-8 (CP1251 — обычное дело у русского проекта на Windows): декодер по
    // умолчанию молча меняет каждый невалидный байт на U+FFFD, сканер портит текст ровно
    // так же — хеши совпадают, сверка проходит, и на месте карты остаются одни ромбики.
    // Отказ дороже молчаливой перекодировки
    [Fact]
    public async Task КартаВЧужойКодировке_НеПерезаписывается()
    {
        WriteCandidate();
        // «Карта» в CP1251 записана байтами напрямую: кодовой страницы 1251 в .NET Core
        // нет без отдельного провайдера, а тянуть пакет ради одного теста незачем.
        // 0xCA 0xE0 — валидная пара для CP1251 и заведомо битая последовательность UTF-8
        WriteBytes("CLAUDE.md", [.. "# "u8.ToArray(), 0xCA, 0xE0, 0xF0, 0xF2, 0xE0,
            .. Encoding.UTF8.GetBytes("\n\n[флаги](backend/Models/FeatureFlag.cs)\n")]);
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;
        var before = MapBytes();

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Stale.Should().BeTrue();
        MapBytes().Should().Equal(before);
    }

    // Разбор байтов проверяется напрямую. Метка UTF-16 отбивается тем же строгим
    // декодером: FF FE в UTF-8 невалидны сами по себе, отдельного правила для неё нет
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x23, 0x00 }, false)]   // UTF-16 LE
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x23 }, false)]   // UTF-16 BE
    [InlineData(new byte[] { 0x23, 0x20, 0xCA, 0xE0 }, false)]   // CP1251: «# Ка»
    [InlineData(new byte[] { 0x23, 0x20, 0xD0, 0x9A }, true)]    // UTF-8: «# К»
    public void TryDecode_ОтбиваетВсёНеUtf8(byte[] bytes, bool expected)
    {
        ProjectMapApplyService.TryDecode(bytes, out _, out _).Should().Be(expected);
    }

    [Fact]
    public void TryDecode_УзнаётBOM()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Карта")];

        ProjectMapApplyService.TryDecode(withBom, out var text, out var hasBom).Should().BeTrue();

        hasBom.Should().BeTrue();
        text.Should().Be("# Карта");
        ProjectMapApplyService.TryDecode(Encoding.UTF8.GetBytes("# Карта"), out _, out var plain)
            .Should().BeTrue();
        plain.Should().BeFalse();
    }

    // Карта С @-импортом, который никто не трогал: правка обязана ПРИМЕНИТЬСЯ. Без этого
    // кейса набор не краснеет на самой дорогой поломке — расхождении двух путей раскрытия
    // (Expand от пути против Expand от текста): apply отвечал бы 409 на любую карту с
    // импортом, а человек видел бы штатную плашку и считал фичу рабочей
    [Fact]
    public async Task КартаСИмпортом_КоторыйНеМеняли_ПравкаПрименяется()
    {
        WriteCandidate();
        Write("rules/git.md", "# Правила\n");
        WriteMapWithDeadLink("# Карта\n\n@rules/git.md\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;

        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Stale.Should().BeFalse();
        result.Applied.Should().Equal(id);
        File.ReadAllText(MapPath).Should().Contain("[флаги](backend/Core/Models/FeatureFlag.cs)");
    }

    // ─── сверка отпечатка ───────────────────────────────────────────────────

    [Fact]
    public async Task УстаревшийBaseSha_НеПишетНичего()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var id = DeadLinkId(_scanner, _root);
        var before = MapBytes();

        var result = await _apply.ApplyAsync(_root, "устарел", [id]);

        result.Stale.Should().BeTrue();
        result.Applied.Should().BeEmpty();
        // Актуальный отпечаток отдаётся сразу: фронту не нужен второй запрос ради «Проверить заново»
        result.NewSha.Should().NotBeNullOrEmpty();
        MapBytes().Should().Equal(before);
    }

    // Правка ИМПОРТИРОВАННОГО файла не меняет в CLAUDE.md ни байта, но меняет раскрытый
    // состав — то есть то, что реально едет в контекст. Хеш по одному файлу пропустил бы
    // изменившуюся карту ровно в том сценарии, ради которого раскрытие и заведено
    [Fact]
    public async Task ПравкаИмпортированногоФайла_ДелаетBaseShaУстаревшим()
    {
        WriteCandidate();
        Write("rules/git.md", "# Правила\n");
        WriteMapWithDeadLink("# Карта\n\n@rules/git.md\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var id = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;
        var before = MapBytes();

        Write("rules/git.md", "# Правила\n\nдописали строку\n");
        var result = await _apply.ApplyAsync(_root, report.BaseSha, [id]);

        result.Stale.Should().BeTrue();
        MapBytes().Should().Equal(before);
    }

    // Карты нет вовсе (удалили или переименовали, пока отчёт был открыт) — тот же отказ
    // по отпечатку, а не 404 и не необработанное исключение
    [Fact]
    public async Task КартыНет_ОтвечаетУстаревшимОтпечатком()
    {
        var result = await _apply.ApplyAsync(_root, "какой-нибудь", ["deadbeefdeadbeef"]);

        result.Stale.Should().BeTrue();
        result.Applied.Should().BeEmpty();
    }

    // Применять нечего — файл не переписывается вовсе: лишняя перезапись меняет время
    // файла и будит синки на ровном месте
    [Fact]
    public async Task ПустойСписокПравок_ФайлНеПерезаписывается()
    {
        WriteCandidate();
        WriteMapWithDeadLink("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n");
        var report = _scanner.Scan(_root);
        var writtenAt = File.GetLastWriteTimeUtc(MapPath);

        var result = await _apply.ApplyAsync(_root, report.BaseSha, []);

        result.Stale.Should().BeFalse();
        result.Applied.Should().BeEmpty();
        File.GetLastWriteTimeUtc(MapPath).Should().Be(writtenAt);
    }

    // ─── защитные ветки: гонка между сканом и записью ───────────────────────
    //
    // Через публичный путь они недостижимы (при едином снимке неуникальный якорь
    // обнуляет патч ещё на скане), но существуют ради правки, пришедшей в TOCTOU-окно.

    private static MapSuggestion PatchedSuggestion(string anchor, string after) => new()
    {
        Id = MapSuggestions.Id(MapSuggestions.KindDeadLink, anchor),
        Kind = MapSuggestions.KindDeadLink,
        Severity = MapSuggestions.SeverityHigh,
        Fact = "факт",
        Anchor = new MapSuggestionAnchor(1, null),
        Apply = new MapApplyPatch(anchor, after, anchor),
    };

    [Fact]
    public void ГонкаДобавилаВторойЯкорь_ambiguousAnchor_ТекстНеМеняется()
    {
        var suggestion = PatchedSuggestion("[a](старый.md)", "[a](новый.md)");
        var text = "[a](старый.md)\n\nдописали: [a](старый.md)\n";

        var (applied, failed, result) = ProjectMapApplyService.ApplyTo(text, [suggestion], [suggestion.Id]);

        applied.Should().BeEmpty();
        failed.Single().Reason.Should().Be(MapApplyReasons.AmbiguousAnchor);
        result.Should().Be(text);
    }

    [Fact]
    public void ГонкаУбралаЯкорь_anchorNotFound_ТекстНеМеняется()
    {
        var suggestion = PatchedSuggestion("[a](старый.md)", "[a](новый.md)");
        var text = "# Карта\n\nссылку уже починили руками: [a](новый.md)\n";

        var (applied, failed, result) = ProjectMapApplyService.ApplyTo(text, [suggestion], [suggestion.Id]);

        applied.Should().BeEmpty();
        failed.Single().Reason.Should().Be(MapApplyReasons.AnchorNotFound);
        result.Should().Be(text);
    }

    // Якорь уехал внутрь забора (карту дописали между сканом и применением): для записи
    // он не существует — иначе замена залезла бы в пример
    [Fact]
    public void ЯкорьУехалВЗабор_anchorNotFound()
    {
        var suggestion = PatchedSuggestion("[a](старый.md)", "[a](новый.md)");
        var text = "# Карта\n\n```\n[a](старый.md)\n```\n";

        var (applied, failed, _) = ProjectMapApplyService.ApplyTo(text, [suggestion], [suggestion.Id]);

        applied.Should().BeEmpty();
        failed.Single().Reason.Should().Be(MapApplyReasons.AnchorNotFound);
    }

    // Повтор того же id в запросе — не вторая правка: без отсева он дал бы ложный
    // anchorNotFound по уже применённой замене
    [Fact]
    public void ПовторIdВЗапросе_ПрименяетсяОдинРаз()
    {
        var suggestion = PatchedSuggestion("[a](старый.md)", "[a](новый.md)");

        var (applied, failed, text) = ProjectMapApplyService.ApplyTo(
            "[a](старый.md)\n", [suggestion], [suggestion.Id, suggestion.Id]);

        applied.Should().Equal(suggestion.Id);
        failed.Should().BeEmpty();
        text.Should().Be("[a](новый.md)\n");
    }

    // ─── разметка заборов ───────────────────────────────────────────────────

    [Fact]
    public void Offsets_СчитаетТолькоВхожденияВнеЗаборов()
    {
        var text = "[a](x.md)\n```\n[a](x.md)\n```\n[a](x.md)\n";

        var offsets = MapAnchorText.Offsets(text, "[a](x.md)");

        offsets.Should().HaveCount(2);
        offsets[0].Should().Be(0);
        text.Substring(offsets[1], 9).Should().Be("[a](x.md)");
    }
}
