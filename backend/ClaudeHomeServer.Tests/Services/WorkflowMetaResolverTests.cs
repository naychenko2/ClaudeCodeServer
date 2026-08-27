using System.Text;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

public class WorkflowMetaResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly ILogger _savedLog;

    public WorkflowMetaResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wfmeta_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Подменяем статический Log на наш собирающий — иначе warning'и про mojibake
        // уходят в NullLogger и тесты не видят, что сторож сработал.
        _savedLog = WorkflowMetaResolver.Log;
        // Фоновое значение статика непредсказуемо: Program.cs перезаписывает его логгером
        // каждого поднимаемого тестового хоста, и после чужого Dispose тот мёртв (EventLog →
        // ObjectDisposedException на записи — флак полного прогона). Работаем от NullLogger;
        // тесты, которым нужны warnings, подменяют Log своим коллектором (ниже)
        WorkflowMetaResolver.Log = NullLogger.Instance;
    }

    public void Dispose()
    {
        WorkflowMetaResolver.Log = _savedLog;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteScript(string name, string content)
    {
        var path = Path.Combine(_dir, name + ".js");
        File.WriteAllText(path, content);
        return path;
    }

    private const string PanelScript = """
        export const meta = {
          name: 'panel-of-experts',
          description: 'Многоагентная дискуссия',
          phases: [
            { title: 'Раунд 1' },
            { title: 'Финальный синтез' },
          ],
        }

        const topic = args.topic
        phase('Раунд 1')
        """;

    [Fact]
    public void Вырезает_meta_блок_с_фазами()
    {
        // Уникальное имя, чтобы не поймать одноимённый скрипт из claude-defaults-фолбэка
        WriteScript("panel-test-unique", PanelScript);

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir], "panel-test-unique");

        block.Should().NotBeNull();
        block.Should().StartWith("export const meta");
        block.Should().Contain("phases:");
        block.Should().Contain("Финальный синтез");
        // Тело скрипта после meta-блока в вырезку не попадает
        block.Should().NotContain("phase('Раунд 1')");
    }

    [Fact]
    public void Нет_файла_возвращает_null()
    {
        WorkflowMetaResolver.TryGetMetaBlock([_dir], "не-существует-xyz").Should().BeNull();
    }

    [Fact]
    public void Скрипт_без_meta_возвращает_null()
    {
        WriteScript("no-meta-unique", "const x = 1\nphase('a')\n");
        WorkflowMetaResolver.TryGetMetaBlock([_dir], "no-meta-unique").Should().BeNull();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Небезопасное_имя_отклоняется(string name)
    {
        WorkflowMetaResolver.TryGetMetaBlock([_dir], name).Should().BeNull();
    }

    // Сторож от кракозябр на карточке механики: meta-блок едет в input.script вызова Workflow,
    // и фронт рисует из него заголовок и подписи фаз. Файл пишем БАЙТАМИ в UTF-8 без BOM —
    // ровно как лежат встроенные механики — и требуем посимвольного совпадения кириллицы.
    [Fact]
    public void Кириллица_из_utf8_файла_читается_без_потерь()
    {
        const string script = """
            export const meta = {
              name: 'red-team-unique',
              description: 'Красная команда: N атакующих с разных углов',
              phases: [
                { title: 'Атака' },
                { title: 'Усиление' },
                { title: 'Синтез' },
              ],
            }

            phase('Атака')
            """;
        File.WriteAllBytes(Path.Combine(_dir, "red-team-unique.js"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script));

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir], "red-team-unique");

        block.Should().NotBeNull();
        block.Should().Contain("Красная команда: N атакующих с разных углов");
        block.Should().Contain("Атака").And.Contain("Усиление").And.Contain("Синтез");
        // Признак однобайтового декодирования UTF-8: кириллица из d0-байтов вырождается в пары,
        // начинающиеся с U+0420 «Р» («Атака» → «РђС‚Р°РєР°»). В самом скрипте этой буквы нет.
        block.Should().NotContain("Р");
    }

    [Fact]
    public void Первый_каталог_приоритетнее()
    {
        var dir2 = Path.Combine(_dir, "second");
        Directory.CreateDirectory(dir2);
        WriteScript("dup-unique", "export const meta = { name: 'first' }");
        File.WriteAllText(Path.Combine(dir2, "dup-unique.js"), "export const meta = { name: 'second' }");

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir, dir2], "dup-unique");

        block.Should().Contain("first");
        block.Should().NotContain("second");
    }

    // Инцидент 23.08: в hostовом ~/.claude/workflows лежали копии механик, дважды
    // перекодированные UTF-8→CP1251→UTF-8 — кириллица вырождалась в «Р РµРІСЊС�».
    // CLI исполнял правильный скрипт из профиля, но в карточку ехал битый meta-блок.
    // Сторож должен пропустить первый каталог и уйти фолбэком ко второму (claude-defaults).
    [Fact]
    public void Битый_meta_в_первом_каталоге_пропускается_и_идёт_к_следующему()
    {
        var dir2 = Path.Combine(_dir, "clean");
        Directory.CreateDirectory(dir2);

        // dir1 — двойная перекодировка: «Ревью» → «Р РµРІСЊСЋ» (U+0420 + U+00A0 между буквами).
        // Структура скрипта сохранена, иначе ExtractMeta не нашёл бы блок.
        // U+00A0 пишем явным   — иначе JSON-передача превратит NBSP в обычный space.
        const string mojibake = "export const meta = {\r\n"
            + "  name: 'Р РµРІСЊСЋ',\r\n"
            + "  description: 'Р Р°Р РЅРґРѕ Р С‚Р°РєСѓСЋС‰РёС…',\r\n"
            + "  phases: [ { title: 'Р РµРІСЊСЋ' } ],\r\n"
            + "}";
        File.WriteAllText(Path.Combine(_dir, "mojibake-unique.js"), mojibake);
        File.WriteAllText(Path.Combine(dir2, "mojibake-unique.js"),
            "export const meta = { name: 'clean', description: 'чистый', phases: [{ title: 'Ok' }] }");

        var log = new CollectingLogger();
        WorkflowMetaResolver.Log = log;

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir, dir2], "mojibake-unique");

        block.Should().NotBeNull();
        block.Should().Contain("clean").And.NotContain("Р РµРІСЊСЋ");
        // В логе — явный warning с путём к битому файлу и именем workflow
        log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("отбракован")
            && e.Message.Contains("mojibake-unique")
            && e.Message.Contains(Path.Combine(_dir, "mojibake-unique.js")));
    }

    // Сторож не должен ложно срабатывать на честном русском meta-блоке с «Р», «—», «→».
    [Fact]
    public void Чистый_русский_meta_с_тире_и_стрелками_не_ложится_на_guard()
    {
        const string clean = "export const meta = {\r\n"
            + "  name: 'red-team',\r\n"
            + "  description: 'Ревью — Красная команда: атакующие → отчёт',\r\n"
            + "  phases: [\r\n"
            + "    { title: 'Раунд 1 → атака' },\r\n"
            + "    { title: 'Усиление — синтез' },\r\n"
            + "  ],\r\n"
            + "}";
        File.WriteAllBytes(Path.Combine(_dir, "red-team-clean-unique.js"),
            new UTF8Encoding(false).GetBytes(clean));

        var log = new CollectingLogger();
        WorkflowMetaResolver.Log = log;

        var block = WorkflowMetaResolver.TryGetMetaBlock([_dir], "red-team-clean-unique");

        block.Should().NotBeNull();
        block.Should().Contain("Ревью — Красная команда");
        block.Should().Contain("Раунд 1 → атака");
        // Сторож не должен был сработать: ни одного warning про mojibake
        log.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("перекодирован"));
    }

    // Успешное чтение логируется с полным путём файла и именем workflow —
    // иначе при следующем инциденте пришлось бы снова перебирать гипотезы.
    [Fact]
    public void Успех_логирует_путь_источника_и_имя_workflow()
    {
        WriteScript("log-info-unique", "export const meta = { name: 'x' }");
        var expectedPath = Path.Combine(_dir, "log-info-unique.js");

        var log = new CollectingLogger();
        WorkflowMetaResolver.Log = log;

        WorkflowMetaResolver.TryGetMetaBlock([_dir], "log-info-unique");

        log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains(expectedPath)
            && e.Message.Contains("log-info-unique"));
    }

    // Полный промах (в dirs вообще нет такого файла) — тоже виден в логе как warning,
    // чтобы молчаливая тишина не маскировала регрессию резолвера.
    [Fact]
    public void Промах_мета_логируется_как_warning()
    {
        var log = new CollectingLogger();
        WorkflowMetaResolver.Log = log;

        WorkflowMetaResolver.TryGetMetaBlock([_dir], "missing-workflow-name");

        log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("missing-workflow-name")
            && e.Message.Contains("не найден"));
    }

    // Локальный in-memory логгер — собирает записи для утверждений и не зависит от TestContext.
    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
