using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Инвариант: СОСТАВ инструментов MCP-серверов не зависит от хода.
///
/// Набор ключей серверов и отпечаток состава их инструментов входят в сигнатуру запуска CLI
/// (BuildLaunchSignature). Как только состав начинает зависеть от свойств хода — глубины
/// делегирования, текста, флага подавления — сигнатура «мерцает» между ходами, и каждый такой
/// переход убивает процесс claude вместе со ВСЕМИ MCP-серверами: незавершённые вызовы падают
/// «Stream closed», а инструменты то появляются, то исчезают («No such tool available»).
///
/// На эти грабли наступали трижды: WORKSPACE_WRITE по интенту хода, PERSONAS_WRITE/MENTIONS по
/// тексту, TASKS_EXECUTE и срезание секций chats/destructive по agentDepth. Все ограничения
/// теперь проверяет бэкенд по актуальному состоянию сессии (DenyOnDelegatedTurnAttribute),
/// а состав остаётся постоянным.
///
/// Тест сторожевой: читает исходник BuildTurnMcpConfig и следит, чтобы в нём не появилось
/// обращений к состоянию хода. Подсказки в системном промпте под запрет не попадают — они
/// в сигнатуру не входят и меняться от хода к ходу могут.
/// </summary>
public class McpToolsetStabilityTests
{
    private static string? FindSource() => FindSource("Services", "Llm", "Claude", "ClaudeSession.cs");

    private static string? FindSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, "backend", "ClaudeHomeServer", .. relative]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // Тело метода SessionManager по его сигнатуре (до начала следующего объявления)
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"метод «{signature}» обязан существовать");
        var end = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        if (end < 0) end = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за методом обязано идти следующее объявление");
        return string.Join('\n', source[start..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [SkippableFact]
    public void СоставИнструментовХода_НеЗависитОтСостоянияХода()
    {
        var path = FindSource();
        Skip.If(path is null, "ClaudeSession.cs не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        // Сигнатуру ищем регулярным выражением, а не точной строкой: кортеж возврата растёт
        // (Path, ServerKeys, потом ServerNames…), и на каждом расширении сторож падал «метод
        // не найден» — то есть сообщал не о нарушении инварианта, а о собственной хрупкости.
        var signature = Regex.Match(source, @"private \([^)]*\) BuildTurnMcpConfig\(");
        signature.Success.Should().BeTrue("метод сборки MCP-конфига хода обязан существовать");
        var start = signature.Index;

        // Конец метода — начало следующего (MapMcpPath идёт сразу за ним)
        var end = source.IndexOf("private string? MapMcpPath", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за BuildTurnMcpConfig обязан идти MapMcpPath");

        var body = source[start..end];

        // Комментарии из проверки убираем: они объясняют ИСТОРИЮ гейтов и упоминают их имена
        var code = string.Join('\n', body.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        code.Should().NotContain("_currentTurnAgentDepth",
            "глубина делегирования не должна влиять на состав инструментов — гейт живёт на бэкенде "
            + "(DenyOnDelegatedTurnAttribute), иначе процесс CLI перезапускается между ходами");
        code.Should().NotContain("_currentTurnSuppressTasksExecute",
            "подавление запуска исполнителя проверяет бэкенд, а не состав инструментов");
        code.Should().NotContain("turnText",
            "текст хода не должен влиять на состав инструментов (грабли WriteIntentGate)");
    }

    /// <summary>
    /// Per-persona рубильники серверов (ключи personas/consultants/codegraph/notifications/
    /// widgets): гейт обязан жить в SessionManager и решаться ТОЛЬКО по персоне — иначе состав
    /// tools/list начнёт зависеть от хода со всеми последствиями выше.
    /// </summary>
    [SkippableTheory]
    [InlineData("private WidgetsMcpContext? BuildWidgetsContext", "widgets")]
    [InlineData("private CodeGraphMcpContext? BuildCodeGraphContext", "codegraph")]
    [InlineData("private Func<string?, Task<string?>>? BuildCodeGraphProvider", "codegraph")]
    [InlineData("private bool PersonasEnabled", "personas")]
    [InlineData("private bool ConsultantsEnabled", "consultants")]
    // Серверы личного реестра владельца: ключ каталога — «mcp:<ключ сервера>»,
    // выключает только Off-привязка персоны. Состав от хода не зависит.
    [InlineData("private Func<ExternalMcpContext?>? BuildExternalMcpProvider", "mcp:")]
    public void РубильникиСерверов_ГейтятсяТолькоПоПерсоне(string signature, string key)
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!), signature);

        body.Should().Contain($"\"{key}\"",
            $"сервер обязан гейтиться Tool-ключом {key} (Off-привязка type: tool, target: {key})");
        body.Should().MatchRegex(@"(ServerToolEnabled|McpServerGranted|ConsultantsEnabled|NotificationsEnabled)\(",
            "решение принимает единая точка PersonaBindingsService.ServerToolEnabled "
            + "(для консультантов и персон — обёртки ConsultantsEnabled/PersonasEnabled "
            + "с исключением для групповых чатов; для уведомлений — NotificationsEnabled "
            + "с дефолтом по роли; для серверов реестра в allow-модели — McpServerGranted)");
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав серверов");
    }

    /// <summary>
    /// Каскад доступности серверов личного реестра в теле BuildExternalMcpProvider.
    /// На период флага mcp-allowlist в методе живут ОБЕ модели — deny (реестр →
    /// Project.McpServersOff → Off-привязка ServerToolEnabled) и allow (реестр →
    /// Project.McpServersOn → выдача персоне McpServerGranted, чистое условие McpDelivery).
    /// Все оси — свойства owner/project/persona, ни одна не смотрит на ход; выпадение
    /// любой означало бы, что настройка молча перестала действовать. При зачистке deny-ветки
    /// ассерты на McpServersOff/ServerToolEnabled уходят вместе с ней.
    /// </summary>
    [SkippableFact]
    public void КаскадРеестра_ТриОсиНаМесте()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private Func<ExternalMcpContext?>? BuildExternalMcpProvider");

        // Общая ось 0 (рубильник записи) и оси deny-модели
        body.Should().Contain("record.Enabled", "первая ось каскада — рубильник самой записи реестра");
        body.Should().Contain("McpServersOff",
            "deny-модель: сервер, выключенный в проекте (McpServersOff), в ход не едет");
        body.Should().Contain("ServerToolEnabled(", "deny-модель: Off-привязка персоны");
        // Оси allow-модели
        body.Should().Contain("McpServersOn",
            "allow-модель: сервер включается в проекте через McpServersOn");
        body.Should().Contain("McpServerGranted(", "allow-модель: выдача сервера персоне");
        body.Should().Contain("ShouldDeliver",
            "allow-модель: чистое условие доставки вынесено в McpDelivery.ShouldDeliver");
        // Свойства хода в условие не входят — состав tools/list стабилен
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав серверов реестра");
    }

    /// <summary>
    /// Чистое условие allow-доставки (McpDelivery.ShouldDeliver) обязано сверять все входы
    /// правила: рубильник записи (Enabled), ось «вне проектов» (AllowOutsideProjects) и
    /// AND-гейт профиля «Только чтение» (AllowReadOnlyPersonas). Выпадение любого — тихая
    /// потеря настройки; все входы — свойства owner/project/persona/записи, не хода.
    /// </summary>
    [SkippableFact]
    public void AllowМодель_ЧистоеУсловиеДоставки_СодержитВсеоси()
    {
        var path = FindSource("Services", "Mcp", "McpDelivery.cs");
        Skip.If(path is null, "McpDelivery.cs не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("public static bool ShouldDeliver(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "метод ShouldDeliver обязан существовать");
        // Тело метода — от сигнатуры до конца файла (в McpDelivery.cs единственный метод);
        // XML-doc перед сигнатурой сюда не попадает, а внутренние //-комментарии выкидываем,
        // чтобы ассенты судили по коду, а не по пояснениям.
        var body = string.Join('\n', source[start..].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        body.Should().Contain("record.Enabled", "ось 0 — рубильник записи реестра");
        body.Should().Contain("AllowOutsideProjects",
            "ось «чаты вне проектов»: без неё внепроектный чат теряет сервер");
        body.Should().Contain("AllowReadOnlyPersonas",
            "AND-гейт профиля «Только чтение» — не поглощается allow-list");
        body.Should().Contain("personaGranted", "выдача персоне — вторая половина OR-правила");
        body.Should().Contain("readOnly", "профиль персоны передаётся в условие явно");
    }

    /// <summary>
    /// Статус MCP-серверов пишется из ОДНОЙ точки — приёмника состава инструментов, который
    /// уже получает system/init хода. Заводить ради статуса второй канал (правку ClaudeSession,
    /// новое поле протокола, фоновый поллинг) не нужно: init перечисляет все поднятые серверы.
    /// </summary>
    [SkippableFact]
    public void СтатусСерверов_ПишетсяИзПриёмникаSystemInit()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerInfo>>? PromptToolsSinkFor");

        body.Should().Contain("RecordFromInit(",
            "наблюдение из system/init обязано попадать в McpStatusStore");
        body.Should().Contain("ResolveOwnerId(",
            "статусы per-owner: владельца сессии резолвит единая точка SessionManager.ResolveOwnerId");
    }

    /// <summary>
    /// Профиль «Только чтение» режет серверы реестра ЦЕЛИКОМ (кроме записей с явным
    /// AllowReadOnlyPersonas), а не запретами на отдельные инструменты: имена инструментов
    /// чужого сервера мы не знаем, они меняются на его стороне, а неизвестное имя в
    /// deny-правиле роняет запуск CLI (история MultiEdit в PersonaAccessPolicy).
    /// </summary>
    [SkippableFact]
    public void ПрофильReadOnly_РежетСерверЦеликом_АНеЕгоИнструменты()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private Func<ExternalMcpContext?>? BuildExternalMcpProvider");

        body.Should().Contain("PersonaAccess.ReadOnly",
            "персона «только чтение» не получает серверы реестра по умолчанию");
        body.Should().Contain("AllowReadOnlyPersonas",
            "исключение — только явное разрешение в самой записи реестра");

        // Список запретов профиля не смеет обрастать именами чужих инструментов
        var policy = FindSource("Services", "PersonaAccessPolicy.cs");
        Skip.If(policy is null, "PersonaAccessPolicy.cs не найден");
        File.ReadAllText(policy!).Should().NotContain("mcp__mcp_",
            "инструменты серверов реестра гасятся отключением сервера, а не deny-правилами");
    }

    /// <summary>
    /// Секции-надстройки с пресетом по роли (git/kb в workspace, manage/automation в сервере
    /// персон): решаются ТОЛЬКО по персоне через единую точку SectionEnabled. Свой набор
    /// инструментов у каждой, поэтому зависимость от хода тут так же смертельна.
    /// </summary>
    [SkippableTheory]
    [InlineData("private WorkspaceMcpContext? BuildWorkspaceContext", "git", "kb")]
    [InlineData("private PersonasMcpContext? BuildPersonasContext", "personas-manage", "personas-automation")]
    public void СекцииПоРоли_ГейтятсяТолькоПоПерсоне(string signature, string first, string second)
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!), signature);

        foreach (var key in new[] { first, second })
            body.Should().Contain($"\"{key}\"",
                $"секция обязана гейтиться Tool-ключом {key} (привязка type: tool, target: {key})");
        body.Should().Contain("SectionEnabled(",
            "решение принимает единая точка PersonaBindingsService.SectionEnabled "
            + "(привязка → Persona.Tools → пресет по specialty)");
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав секций");
    }

    /// <summary>
    /// Сервер уведомлений: дефолт сузили до персон с модулем автоматизации, решение —
    /// единая точка NotificationsEnabled (ключ notifications живёт внутри неё). Обычный
    /// чат получает сервер как раньше. Зависимость от хода тут так же смертельна.
    /// </summary>
    [SkippableFact]
    public void СерверУведомлений_РешаетсяЕдинойТочкойПоПерсоне()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private NotificationsMcpContext? BuildNotificationsContext");

        body.Should().Contain("NotificationsEnabled(",
            "решение принимает PersonaBindingsService.NotificationsEnabled "
            + "(Off-привязка → Persona.Tools → модуль автоматизации по роли)");
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав серверов");
    }

    /// <summary>
    /// Модуль заметок «комментарии к документам + редкие операции» (ключ notes-annotations,
    /// env NOTES_ANNOTATIONS): состав tools/list сервера заметок зависит от него, значит
    /// решать его смеет только персона.
    /// </summary>
    [SkippableFact]
    public void МодульЗаметок_ГейтитсяТолькоПоПерсоне()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private NotesMcpContext? BuildNotesContext");

        body.Should().Contain("\"notes-annotations\"",
            "модуль обязан гейтиться Tool-ключом notes-annotations");
        body.Should().Contain("SectionEnabled(",
            "решение принимает единая точка PersonaBindingsService.SectionEnabled");
        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на состав инструментов заметок");
    }

    /// <summary>
    /// Провайдер сабагентов-консультантов (pmem-серверы + --add-dir) гейтится тем же
    /// ConsultantsEnabled, а не собственной копией правила.
    /// </summary>
    [SkippableFact]
    public void ПровайдерКонсультантов_ГейтитсяЧерезConsultantsEnabled()
    {
        var path = FindSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var body = MethodBody(File.ReadAllText(path!),
            "private Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider");

        body.Should().Contain("ConsultantsEnabled(");
    }

    // --- personas-server: personas_set_default безусловно в tools/list ---

    private static string? FindMcpServer(string server)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", server, "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // tools/list живого personas-server с заданными env (паттерн NotesAndTasksMcpServerToolsTests).
    // null — node недоступен (тест скипается).
    private static IReadOnlyList<string>? ListPersonasTools(string serverPath,
        params (string Key, string Value)[] env)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Бэкенд не нужен: tools/list считается из env, в сеть сервер не ходит
        psi.Environment["PERSONAS_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["PERSONAS_API_TOKEN"] = "test";
        foreach (var (key, value) in env) psi.Environment[key] = value;

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/list");
            using var doc = System.Text.Json.JsonDocument.Parse(line!);
            return doc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
        }
    }

    /// <summary>
    /// personas_set_default обязан присутствовать в tools/list ВСЕГДА — даже с выключенными
    /// модулями manage/automation и аварийным PERSONAS_WRITE=0 (инвариант стабильности состава:
    /// ограничение живёт на бэкенде — вызов разрешён только онбординг-сессии).
    /// </summary>
    [SkippableFact]
    public void PersonasSetDefault_ВСоставеВсегда_ДажеБезМодулей()
    {
        var serverPath = FindMcpServer("personas-server");
        Skip.If(serverPath is null, "mcp/personas-server/index.js не найден");

        var tools = ListPersonasTools(serverPath!,
            ("PERSONAS_WRITE", "0"), ("PERSONAS_MANAGE", "0"), ("PERSONAS_AUTOMATION", "0"));
        if (tools is null) return;

        tools.Should().Contain("personas_set_default");
        // Ядро сервера на месте — разрез модулей его не задел
        tools.Should().Contain("personas_list").And.Contain("personas_get");
    }

    /// <summary>И в полном составе (все модули включены) инструмент никуда не девается.</summary>
    [SkippableFact]
    public void PersonasSetDefault_ВСоставе_СМодулями()
    {
        var serverPath = FindMcpServer("personas-server");
        Skip.If(serverPath is null, "mcp/personas-server/index.js не найден");

        var tools = ListPersonasTools(serverPath!);
        if (tools is null) return;

        tools.Should().Contain("personas_set_default").And.Contain("personas_create");
    }
}
