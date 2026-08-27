using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тесты санитайзера ЛОГОВ (<see cref="PiiSanitizingLogProcessor"/>).
///
/// Проверяются на реальном конвейере логирования: наш процессор + перехватчик сразу
/// за ним, поэтому видно ровно то, что уехало бы в SigNoz. Подделывать LogRecord руками
/// нельзя (у него internal-конструктор), да и незачем — так тест ближе к бою.
///
/// Контекст (регрессия): санитайзер существовал только для спанов
/// (<c>BaseProcessor&lt;Activity&gt;</c>), а логи экспортировались вообще без фильтра
/// при включённых IncludeFormattedMessage/ParseStateValues. Имя персоны в спане
/// дропалось, а то же имя в логе уезжало целиком.
/// </summary>
public class PiiLogSanitizerTests
{
    private sealed record Captured(string? Message, Dictionary<string, object?> Attributes, Exception? Exception);

    /// <summary>Перехватчик: копирует значения сразу (LogRecord переиспользуется из пула).</summary>
    private sealed class CapturingProcessor(List<Captured> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record)
        {
            var attributes = new Dictionary<string, object?>();
            if (record.Attributes is not null)
                foreach (var a in record.Attributes) attributes[a.Key] = a.Value;
            sink.Add(new Captured(record.FormattedMessage, attributes, record.Exception));
        }
    }

    private static Captured Log(Action<ILogger> write)
    {
        var sink = new List<Captured>();
        using (var factory = LoggerFactory.Create(b =>
               {
                   b.AddOpenTelemetry(o =>
                   {
                       o.IncludeFormattedMessage = true;
                       o.ParseStateValues = true;
                       // Порядок важен: сначала санитайзер, потом перехват результата
                       o.AddProcessor(new PiiSanitizingLogProcessor());
                       o.AddProcessor(new CapturingProcessor(sink));
                   });
               }))
        {
            write(factory.CreateLogger("test"));
        }

        return sink.Should().ContainSingle().Subject;
    }

    [Fact]
    public void ChatName_DoesNotLeakIntoMessage()
    {
        // Реальный лог из ChatExpiryService — имя чата задаёт пользователь
        var captured = Log(l => l.LogInformation(
            "Временный чат {SessionId} «{Name}» удалён", "ses-abc", "Отчёт по клиенту Иванову"));

        captured.Message.Should().NotContain("Иванову", "имя чата — пользовательские данные");
        captured.Message.Should().Be("Временный чат ses-abc «{Name}» удалён",
            "тело собирается из шаблона и ОЧИЩЕННЫХ атрибутов: разрешённый идентификатор "
            + "виден, а имя чата остаётся плейсхолдером");
    }

    [Fact]
    public void Render_KeepsDroppedPlaceholders_ButSubstitutesAllowed()
    {
        // Ровно та строка, ради которой рендер и заводился: без него в ленте SigNoz
        // читалось «действие {Action} — {Reason}», то есть ничего.
        var captured = Log(l => l.LogWarning(
            "cheap-runner: действие {Action} — {Reason}; текст {Message}",
            "team-memory-consolidate", "AI не ответил (лимит 180 с)", "пользовательский текст"));

        captured.Message.Should().Be(
            "cheap-runner: действие team-memory-consolidate — AI не ответил (лимит 180 с); текст {Message}");
        captured.Message.Should().NotContain("пользовательский текст");
    }

    [Fact]
    public void Render_AppliesFormat_WithInvariantCulture()
    {
        // {Idle:0.0} из DevServerService. Культура ВСЕГДА инвариантная: на ru-RU дробное
        // значение дало бы запятую, и одинаковые события перестали бы грепаться.
        var captured = Log(l => l.LogInformation("DevServer {Key}: простой {Idle:0.0} мин", "web", 12.5));

        captured.Message.Should().Be("DevServer {Key}: простой 12.5 мин",
            "{Key} закрыт намеренно — под ним может уехать секрет");
    }

    [Fact]
    public void Render_PutsHash_IntoMessage_NotRawPath()
    {
        var captured = Log(l => l.LogWarning("Не удалось записать {Path}", @"C:\Users\grisha\secret.json"));

        captured.Message.Should().NotContain("grisha");
        captured.Message.Should().NotContain("{Path}", "путь не дропается, а хэшируется — хэш и попадает в тело");
    }

    [Fact]
    public void KestrelUnhandledException_ReadsWhole()
    {
        // Дословное сообщение Kestrel об необработанном исключении. До открытия
        // trace_identifier в allowlist запись в SigNoz приезжала обрубком: первый
        // плейсхолдер со значением, второй — как есть.
        var captured = Log(l => l.LogError(
            "Connection id \"{ConnectionId}\", Request id \"{TraceIdentifier}\": "
            + "An unhandled exception was thrown by the application.",
            "0HNO3H40H5NAD", "0HNO3H40H5NAD:00000004"));

        captured.Message.Should().Be(
            "Connection id \"0HNO3H40H5NAD\", Request id \"0HNO3H40H5NAD:00000004\": "
            + "An unhandled exception was thrown by the application.",
            "оба плейсхолдера — opaque-идентификаторы соединения, PII в них нет");
    }

    [Fact]
    public void ChatName_DoesNotLeakIntoAttributes()
    {
        var captured = Log(l => l.LogInformation(
            "Временный чат {SessionId} «{Name}» удалён", "ses-abc", "Отчёт по клиенту Иванову"));

        captured.Attributes.Should().NotContainKey("Name");
        captured.Attributes.Values.Should().NotContain("Отчёт по клиенту Иванову");
    }

    [Fact]
    public void SessionId_IsKept_DespitePascalCase()
    {
        // В спане тег зовётся session_id, в логе — {SessionId}. Правило должно быть одно.
        var captured = Log(l => l.LogInformation(
            "Временный чат {SessionId} «{Name}» удалён", "ses-abc", "имя"));

        captured.Attributes.Should().ContainKey("SessionId")
            .WhoseValue.Should().Be("ses-abc");
    }

    [Fact]
    public void UserId_IsDropped()
    {
        var captured = Log(l => l.LogInformation("Сводка для {UserId}", "usr-42"));

        captured.Attributes.Should().NotContainKey("UserId");
    }

    [Fact]
    public void PersonaName_IsDropped()
    {
        var captured = Log(l => l.LogInformation("Персона {PersonaName} обновлена", "Марк"));

        captured.Attributes.Should().NotContainKey("PersonaName");
        captured.Attributes.Values.Should().NotContain("Марк");
    }

    [Fact]
    public void SecretPath_IsHashed_NotExposed()
    {
        // Реальный лог из JwtService — путь к файлу с секретом
        var captured = Log(l => l.LogInformation("Секрет JWT создан: {Path}", @"C:\deploy\claude\data\jwt.key"));

        captured.Attributes.Should().ContainKey("Path");
        captured.Attributes["Path"].Should().NotBe(@"C:\deploy\claude\data\jwt.key");
        captured.Attributes["Path"]!.ToString().Should().HaveLength(8, "путь хэшируется для корреляции");
    }

    [Fact]
    public void OriginalFormatTemplate_IsKept()
    {
        // Шаблон значений не содержит — он и есть замена развёрнутому сообщению
        var captured = Log(l => l.LogInformation("Чат {SessionId} удалён", "ses-1"));

        captured.Attributes.Should().ContainKey("{OriginalFormat}");
    }

    [Fact]
    public void ExceptionStacktrace_DoesNotReachExporter()
    {
        // Регрессия, найденная на живых данных: экспортёр OTLP разворачивает
        // LogRecord.Exception в exception.type/message/stacktrace УЖЕ ПОСЛЕ процессоров,
        // поэтому фильтрация record.Attributes до них не достаёт. В SigNoz лежали полные
        // стектрейсы с абсолютными путями сборки.
        var exception = new InvalidOperationException(
            @"Failed GET https://api.example.com/v1?api_key=SECRET123 at C:\build\src\Foo.cs");

        var captured = Log(l => l.LogError(exception, "Запрос к {Provider} не удался", "dify"));

        captured.Exception.Should().BeNull("иначе экспортёр развернёт его в атрибуты мимо allowlist'а");
        captured.Attributes["exception.type"].Should().Be("System.InvalidOperationException",
            "тип исключения для диагностики нужен и PII не содержит");
        captured.Attributes.Values.Any(v => v?.ToString()?.Contains("SECRET123") == true)
            .Should().BeFalse("ключ из текста исключения не должен уехать ни в один атрибут");
    }

    [Fact]
    public void OperationalAttributes_SurviveSanitizing()
    {
        // Санитайзер не должен обесценить логи: диагностические поля обязаны доезжать
        var captured = Log(l => l.LogInformation(
            "Ход завершён: {Provider} {Model} {Outcome}", "glm", "glm-5.2", "success"));

        captured.Attributes["Provider"].Should().Be("glm");
        captured.Attributes["Model"].Should().Be("glm-5.2");
        captured.Attributes["Outcome"].Should().Be("success");
    }
}
