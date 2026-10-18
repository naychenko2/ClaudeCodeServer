using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Проверка durability сабмита по транскрипту (инцидент 16.08.2026): skip-реаттемпт хода
// допустим, только если текст действительно durable в .jsonl — убитый до чтения stdin
// процесс его туда не пишет. LastUserText читает хвост файла и возвращает текст последнего
// user-сообщения с content-строкой; здесь все ветки разбора.
public class TranscriptProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-transcript-probe-tests");

    public TranscriptProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка — не критично */ }
    }

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private const string AssistantLine =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ответ"}]}}""";

    private static string UserLine(string text)
        => $$$"""{"type":"user","message":{"role":"user","content":"{{{text}}}"}}""";

    // Последний user-текст возвращается как есть — совпадение со стартовым текстом хода.
    [Fact]
    public void ПоследнийUserТекст_Возвращается()
    {
        var path = WriteTranscript(AssistantLine, UserLine("Проверь"));
        TranscriptProbe.LastUserText(path).Should().Be("Проверь");
    }

    // Служебные вставки CLI (isMeta — «Continue from where you left off.») — не стартовые
    // тексты ходов: сквозь них виден настоящий последний пользовательский текст.
    [Fact]
    public void МетаUser_Пропускается()
    {
        var meta = """{"type":"user","isMeta":true,"message":{"role":"user","content":"Continue from where you left off."}}""";
        var path = WriteTranscript(UserLine("Ну как"), meta);
        TranscriptProbe.LastUserText(path).Should().Be("Ну как");
    }

    // task-notification CLI вставляет как user-сообщение ПОСЛЕ нашего сабмита (агент фоновых
    // задач дожил) — служебная вставка не отменяет durability: текст хода всё ещё последний.
    [Fact]
    public void ТаскНотификация_ПослеТекста_Пропускается()
    {
        var notif = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>x</task-id>\n</task-notification>"}}""";
        var path = WriteTranscript(UserLine("Проверь"), notif);
        TranscriptProbe.LastUserText(path).Should().Be("Проверь");
    }

    // Вложение (content-массив) не сравнить со строкой хода — null (не durable).
    [Fact]
    public void ВложениеМассивом_Null()
    {
        var attach = """{"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64"}}]}}""";
        var path = WriteTranscript(UserLine("Проверь"), attach);
        TranscriptProbe.LastUserText(path).Should().BeNull();
    }

    // Только assistant-сообщения — user-текста нет.
    [Fact]
    public void БезUserСообщений_Null()
        => TranscriptProbe.LastUserText(WriteTranscript(AssistantLine)).Should().BeNull();

    // Файла нет / путь null — null (вызывающий не skip'ает сабмит).
    [Fact]
    public void ФайлаНет_Null()
    {
        TranscriptProbe.LastUserText(null).Should().BeNull();
        TranscriptProbe.LastUserText(Path.Combine(_root, "missing.jsonl")).Should().BeNull();
    }

    // Обрезанная последняя строка (CLI дописывает файл прямо сейчас) — читается предыдущая
    // целая: JsonException на битой строке не валит разбор.
    [Fact]
    public void ОбрезанныйХвост_БитаяСтрокаПропускается()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, AssistantLine + "\n" + UserLine("Ну как") + "\n" + """{"type":"user","message":{"role":"us""");
        TranscriptProbe.LastUserText(path).Should().Be("Ну как");
    }

    // Большой транскрипт: читается только хвост (tailBytes меньше файла) — последний
    // user-текст всё равно находится.
    [Fact]
    public void БольшойФайл_ЧитаетсяТолькоХвост()
    {
        var filler = string.Concat(Enumerable.Repeat("x", 200));
        var lines = new List<string>();
        for (var i = 0; i < 500; i++)
            lines.Add($$$"""{"type":"assistant","message":{"role":"assistant","content":"{{{filler}}}"}}""");
        lines.Add(UserLine("Проверь"));
        var path = WriteTranscript(lines.ToArray());
        new FileInfo(path).Length.Should().BeGreaterThan(64 * 1024, "файл обязан быть больше хвоста");
        TranscriptProbe.LastUserText(path, tailBytes: 64 * 1024).Should().Be("Проверь");
    }

    // --- LastRecordUuid: точный якорь границы хода для ветвления чата (§4) ---

    private static string UuidLine(string uuid, string parentUuid = "") =>
        $$$"""{"type":"assistant","parentUuid":"{{{parentUuid}}}","uuid":"{{{uuid}}}","message":{"role":"assistant","content":"ответ"}}""";

    // Якорь хода = uuid ПОСЛЕДНЕЙ записи файла, независимо от её типа.
    [Fact]
    public void ПоследнийUuid_Возвращается()
    {
        var path = WriteTranscript(UuidLine("a1"), UserLine("Проверь"), UuidLine("a2", parentUuid: "a1"));
        TranscriptProbe.LastRecordUuid(path).Should().Be("a2");
    }

    // Записи без собственного uuid (служебные строки CLI) пропускаются — берём ближайшую,
    // у которой он есть: якорь обязан указывать на реальную запись файла.
    [Fact]
    public void ЗаписьБезUuid_Пропускается()
    {
        var path = WriteTranscript(UuidLine("a1"), AssistantLine);
        TranscriptProbe.LastRecordUuid(path).Should().Be("a1");
    }

    // Недописанный хвост (kill посреди записи) — битая строка отбрасывается, якорь берётся
    // с последней целой: иначе ветка получила бы якорь, которого в файле нет.
    [Fact]
    public void НедописаннаяПоследняяСтрока_Пропускается()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, UuidLine("a1") + "\n" + """{"type":"assistant","uuid":"a""");
        TranscriptProbe.LastRecordUuid(path).Should().Be("a1");
    }

    // Ни одного uuid в файле — null, а не исключение: вызывающий остаётся на текстовом пути.
    [Fact]
    public void БезUuidВовсе_Null()
    {
        var path = WriteTranscript(AssistantLine, UserLine("Проверь"));
        TranscriptProbe.LastRecordUuid(path).Should().BeNull();
    }

    // Несуществующий файл и null-путь — тоже null (fail-open в сторону старого поведения).
    [Fact]
    public void ФайлаНет_UuidNull()
    {
        TranscriptProbe.LastRecordUuid(null).Should().BeNull();
        TranscriptProbe.LastRecordUuid(Path.Combine(_root, "нет-такого.jsonl")).Should().BeNull();
    }

    // Длинный транскрипт: читается только хвост — якорь всё равно находится.
    [Fact]
    public void БольшойФайл_UuidИзХвоста()
    {
        var filler = string.Concat(Enumerable.Repeat("x", 200));
        var lines = new List<string>();
        for (var i = 0; i < 500; i++)
            lines.Add($$$"""{"type":"assistant","uuid":"old-{{{i}}}","message":{"role":"assistant","content":"{{{filler}}}"}}""");
        lines.Add(UuidLine("tail-uuid"));
        var path = WriteTranscript(lines.ToArray());
        TranscriptProbe.LastRecordUuid(path, tailBytes: 64 * 1024).Should().Be("tail-uuid");
    }
}
