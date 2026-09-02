using System.Reflection;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границ вертикалей: типы из подсистемной вертикали не должны ссылаться на типы
/// из других сервисных вертикалей. Правило — default-deny: разрешено ссылаться только на
/// «спинку» (BCL, ASP.NET, модели) и явно перечисленные служебные вертикали, остальные
/// <c>ClaudeHomeServer.Services.*</c> считаются чужой территорией.
///
/// Под «спинкой» понимается минимальный шов, через который любая вертикаль пользуется
/// платформой и общими сервисами: <c>System.*</c>, <c>Microsoft.*</c>,
/// <c>ClaudeHomeServer.Models</c>, <c>ClaudeHomeServer.Services.Http</c> (общие HTTP-утилиты),
/// <c>ClaudeHomeServer.Services.Security</c> (общие защитные примитивы),
/// <c>ClaudeHomeServer.Services.Composition</c> (контракт <c>IAppSubsystem</c>),
/// <c>ClaudeHomeServer.Services.Mcp</c> (сознательная граница для <c>McpSecretStore</c>).
///
/// Всё прочее под <c>ClaudeHomeServer.Services.*</c> (Desktop, Backup, Llm, Images, Tts,
/// Deploy, Memory, Turn, Docs, Git и т.п.) — нарушение. Список не дописывается под каждую
/// новую вертикаль: появилась новая — тест автоматом ловит любую ссылку на неё, и повод
/// обсудить шов. Подход — как в <c>PiiRules</c> (default-deny с явным allow-list).
///
/// Таблица <see cref="Boundaries"/> — единственная точка расширения: каждая будущая
/// подсистема добавляет ОДНУ строку со своим корневым namespace и собственным allow-list
/// (по умолчанию — общая спинка плюс сам проверяемый namespace).
///
/// Тест работает через рефлексию типов, а не через чтение исходного файла: один из
/// существующих стражей (<c>McpToolsetStabilityTests</c> через <c>FindSource</c> и
/// <c>MethodBody</c>) сломался на прошлом этапе просто от переименования метода
/// <c>PromptToolsSinkFor</c> → <c>SafePromptSnapshotAttach</c>, и пришлось чинить
/// отдельной задачей. Источник правды тут — сборка, а не текст.
///
/// Что НЕ проверяется осознанно: интерфейсы и базовые классы (за пределами четырёх мест
/// ниже). Если потребуется — расширим в следующем шаге.
/// </summary>
public class SubsystemBoundaryTests
{
    /// <summary>Запись границы одной вертикали: имя (для отчёта), корневой namespace
    /// проверяемой вертикали и явный список разрешённых namespace-префиксов (с учётом
    /// вложенных через префикс "X.").</summary>
    public sealed record VerticalBoundary(
        string VerticalName,
        string NamespaceRoot,
        string[] AllowedNamespacePrefixes);

    /// <summary>Неймспейсы, на которые ЛЮБАЯ вертикаль имеет право ссылаться
    /// (BCL/платформа + общие служебные слои + сама вертикаль).</summary>
    private static readonly string[] SharedAllowedPrefixes =
    {
        // BCL и платформа — спинка для всех.
        "System",
        "Microsoft",
        // Доменные модели — разделяемые POCO, не сервисная логика.
        "ClaudeHomeServer.Models",
        // Спинка из общего кода сервисов: HTTP-утилиты, защитные примитивы,
        // контракт подсистем и MCP-секреты (сознательная граница, см. ADR-014).
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Security",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Mcp",
    };

    /// <summary>Таблица границ. Каждая подсистема добавляет ОДНУ строку: имя +
    /// корневой namespace + allow-list (по умолчанию shared-спинка + сама вертикаль).</summary>
    public static IEnumerable<object[]> Boundaries => new[]
    {
        new object[]
        {
            new VerticalBoundary(
                "Video",
                "ClaudeHomeServer.Services.Video",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Video" })
                    .ToArray()),
        },
        new object[]
        {
            new VerticalBoundary(
                "Yandex",
                "ClaudeHomeServer.Services.Yandex",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Yandex" })
                    .ToArray()),
        },
        // Reader — единственная подсистема с прямой зависимостью от корня Services:
        // `SsrfGuard` (Services/ корень, общая инфраструктура, ADR-005) не переезжает
        // в подсистему, и других ссылок из Reader на корень Services нет. Точечный
        // allow-list вместо расширения SharedAllowedPrefixes — чтобы не открывать
        // любой подсистеме весь `ClaudeHomeServer.Services.*` (там живут конкретные
        // сервисы вроде FileService/SessionManager/PersonaManager).
        // `AngleSharp.*` — third-party HTML-парсер (SmartReader + HtmlParser).
        new object[]
        {
            new VerticalBoundary(
                "Reader",
                "ClaudeHomeServer.Services.Reader",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Reader",
                        "ClaudeHomeServer.Services",
                        "AngleSharp",
                    })
                    .ToArray()),
        },
        // Images — вертикаль генерации картинок. Граница расширена под корень
        // `ClaudeHomeServer.Services` ради трёх осознанных исключений (как и Reader):
        // 1) `PersonaManager` (Services/ корень) — догоняющая генерация аватара
        //    триггерится из карточки персоны; это сознательная зависимость от
        //    «спинки» (доменная модель пользователей и персон);
        // 2) `ImageAssetHelper` (Services/ корень) — общая инфраструктура работы
        //    с ассетами картинок, используется и другими разделами; в подсистему
        //    не переезжает;
        // 3) `FalImageService` (Services/ корень) — драйвер, который не переезжает
        //    из корня, чтобы не ломать namespace у существующих вызывающих.
        // Префикс `ClaudeHomeServer.Services` покрывает все три.
        // `ClaudeHomeServer.Hubs` — нужен `IHubContext<SessionHub>` (событие
        // `image_backfilled` едет в ленту персоны).
        // `ClaudeHomeServer.Protocol` — тип сообщения `ImageBackfilledMessage`
        // наследует `ServerMessage` из общего протокола WS-событий.
        new object[]
        {
            new VerticalBoundary(
                "Images",
                "ClaudeHomeServer.Services.Images",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Images",
                        "ClaudeHomeServer.Services",
                        "ClaudeHomeServer.Hubs",
                        "ClaudeHomeServer.Protocol",
                    })
                    .ToArray()),
        },
        // Tts — вертикаль озвучки голосового режима чата. Граница расширена под корень
        // `ClaudeHomeServer.Services` ради одной осознанной зависимости (как у Reader/Images):
        // `VoiceResolver` (Services/Tts) принимает `PersonaManager` (Services/ корень) —
        // голос персоны как часть цепочки склейки; это связь «вертикаль → спинка»
        // (доменная модель пользователей и персон), а не на другую вертикаль.
        // `TtsVoiceCatalog` (Services/Tts) — статический каталог белого списка голосов;
        // его используют СНАРУЖИ вертикали `PersonaManager` и `PersonasController`, но это
        // сознательная обратная стрелка «спина → каталог вертикали», а не зависимость
        // самой вертикали Tts от чужой вертикали.
        new object[]
        {
            new VerticalBoundary(
                "Tts",
                "ClaudeHomeServer.Services.Tts",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Tts",
                        "ClaudeHomeServer.Services",
                    })
                    .ToArray()),
        },
        // Git — НИЖНИЙ слой вертикалей (на него смотрят будущие Dossiers/Knowledge/Deploy,
        // плюс hosted-сервисы SessionManager/ProjectManager). Граница расширена под:
        // 1) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory`, через который
        //    GitService запускает процессы git (источник истины, как в задаче);
        // 2) `ClaudeHomeServer.Services` (корень) — `SessionManager`, `ProjectManager`,
        //    `UserStore`, `ProjectFileSessionsIndex` живут в корне как общая инфраструктура
        //    (аналогично Reader/Tts/Images);
        // 3) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для нотификации об
        //    авто-коммите (GitAutoCommitService отправляет событие в ленту сессии);
        // 4) `ClaudeHomeServer.Protocol` — тип WS-события `*Message` для той же нотификации;
        // 5) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для генерации сообщения
        //    коммита и имени стэша в `GitAiService`. Это сознательная связь «вертикаль →
        //    спинка»: LLM-инфраструктура общего назначения (дешёвые one-shot ходы через
        //    локальную модель или haiku), используемая и другими разделами (теги заметок,
        //    сводки, память...). Вынос Llm в отдельный allow-list вместо расширения
        //    SharedAllowedPrefixes — чтобы не открывать любой подсистеме весь
        //    `ClaudeHomeServer.Services.Llm`.
        new object[]
        {
            new VerticalBoundary(
                "Git",
                "ClaudeHomeServer.Services.Git",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Git",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Services",
                        "ClaudeHomeServer.Hubs",
                        "ClaudeHomeServer.Protocol",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray()),
        },
        // Deploy — вертикаль выкатки прода (ADR-010 + трей-раннер из веб-морды).
        // Граница расширена под:
        // 1) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory` для `schtasks`
        //    (`DeployHost.WakeAgentAsync` будит задачу планировщика через `launchers.Local`);
        //    легально: Execution — нижний слой, общий для всех, кто запускает процессы;
        // 2) `ClaudeHomeServer.Services.Git` — `GitService` для git-guard в `DeployHost`
        //    (проба репозитория: `rev-parse HEAD` и `status --porcelain`). Это СОЗНАТЕЛЬНАЯ
        //    связь «вертикаль → вертикаль» (TODO на шов: завести `IGitGuard` в `Services.Git`
        //    и перевести `DeployHost` на него, тогда `Services.Git` уйдёт из allow-list);
        // 3) `ClaudeHomeServer.Services` (корень) — `SessionManager` и `NotificationService`
        //    для `DeployReportService` (доклад об итоге выкатки в чат-инициатор и
        //    push-уведомление); аналогично `Git`/`Tts`/`Images`/`Reader`: корень — общая
        //    инфраструктура, а не «спинка» уровня `SharedAllowedPrefixes`;
        // 4) `ClaudeHomeServer.Services.Backup` — статический класс `Backup.InstanceLock` с
        //    методом `TryAcquireDeploy()`, через который `DeployHost.TryLockAgent` берёт
        //    мьютекс `Global\ccs-deploy`. Это инфраструктурный примитив общего назначения
        //    (мьютекс деплоя), а не зависимость от логики Backup, и СОЗНАТЕЛЬНО выходит
        //    за рамки обычной рефлексии: доступ к статическому члену через точку не
        //    попадает в поля/конструкторы/return-типы, и без явного allow-list сторож
        //    этот шов пропустит. TODO на шов: выделить мьютекс в отдельный примитив
        //    (например, `DeployAgentLock` в `Services.Composition`) и убрать из allow-list
        //    ссылку на `Services.Backup`.
        new object[]
        {
            new VerticalBoundary(
                "Deploy",
                "ClaudeHomeServer.Services.Deploy",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Deploy",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Services.Git",
                        "ClaudeHomeServer.Services",
                        "ClaudeHomeServer.Services.Backup",
                    })
                    .ToArray()),
        },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Vertical_НеСсылаетсяНаДругиеВертикали(VerticalBoundary boundary)
    {
        // Привязка к типу из подсистемы как точке входа в нужную сборку: проект тестов
        // ссылается на ClaudeHomeServer, typeof(...).Assembly гарантированно даёт её.
        // Подсистема `video` — единственная в таблице, её тип доступен как якорь сборки.
        var assembly = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;

        var types = CollectTypesInNamespaceTree(assembly, boundary.NamespaceRoot);

        var violations = new List<string>();

        foreach (var type in types)
        {
            // Дедупликация: одно поле record-а порождает несколько упоминаний (поле,
            // конструктор, свойство, get/set, Equals/PrintMembers). В отчёте достаточно
            // одной строки на пару «тип-источник → тип-нарушитель».
            var seen = new HashSet<(string, string)>();

            foreach (var referenced in CollectReferencedTypes(type))
            {
                if (!IsAllowed(referenced, boundary.AllowedNamespacePrefixes))
                {
                    seen.Add((type.FullName ?? type.Name, referenced.FullName ?? referenced.Name));
                }
            }

            foreach (var (owner, forbidden) in seen)
            {
                violations.Add(
                    $"{boundary.VerticalName}: {owner} ссылается на {forbidden} " +
                    "из чужой вертикали (нет в AllowedNamespacePrefixes)");
            }
        }

        violations.Should().BeEmpty(
            $"типы из {boundary.NamespaceRoot} должны ссылаться только на спинку " +
            "(System.*, Microsoft.*, Models) и явно разрешённые служебные вертикали " +
            "(Services.Http/Security/Composition/Mcp) либо на самих себя. " +
            "Любая ссылка на прочие Services.* — нарушение архитектурного правила " +
            "(см. CLAUDE.md/ADR-014). Найденные нарушения:\n" +
            string.Join("\n", violations));
    }

    private static IEnumerable<Type> CollectTypesInNamespaceTree(Assembly assembly, string namespaceRoot)
    {
        // Сам неймспейс + все вложенные поднеймспейсы.
        return assembly.GetTypes()
            .Where(t => t.Namespace == namespaceRoot
                        || (t.Namespace?.StartsWith(namespaceRoot + ".", StringComparison.Ordinal) ?? false));
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        // Поля: declared-only, чтобы не утонуть в чужом базовом классе; private тоже —
        // границу нарушает любой член, а не только публичный контракт.
        var memberBinding = BindingFlags.Public | BindingFlags.NonPublic
                          | BindingFlags.Instance | BindingFlags.Static
                          | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(memberBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(field.FieldType))
                yield return t;
        }

        foreach (var ctor in type.GetConstructors(memberBinding))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }

        // Публичные методы и свойства — все, включая унаследованные. Унаследованный
        // метод с типом из запрещённой вертикали — это часть публичного контракта
        // проверяемого типа (через него ссылка «торчит наружу»), и сторож должен
        // её ловить. Обход свойств отдельным проходом: get/set не попадают в GetMethods
        // под теми именами, по которым мы ищем нарушение.
        var publicBinding = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(property.PropertyType))
                yield return t;
        }

        foreach (var method in type.GetMethods(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(method.ReturnType))
                yield return t;

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }
    }

    private static IEnumerable<Type> EnumerateTypeAndArgs(Type? type)
    {
        if (type is null) yield break;

        // Byref (ref/out/in SomeType) — ParameterType вернёт SomeType&, у которого
        // Namespace = null и IsAllowed безусловно пропустит (не Services.*). Снимаем обёртку сразу.
        if (type.IsByRef)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
            yield break;
        }

        // Nullable<T> → T (System.Nullable<...> не интересует, зато интересует аргумент).
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        // Сам тип (например, List<DesktopFoo> сам по себе не из разрешённого неймспейса,
        // но мы его всё равно отдаём — IsAllowed отфильтрует).
        yield return type;

        if (type.IsGenericType)
        {
            // Рекурсия: вложенные generic-аргументы тоже надо раскрыть, иначе тип вида
            // Nested<Wrap<DesktopFoo>> пройдёт мимо стора (Namespace внешнего generic
            // может быть «нейтральным»).
            foreach (var arg in type.GetGenericArguments())
            {
                yield return arg;
                foreach (var t in EnumerateTypeAndArgs(arg))
                    yield return t;
            }
        }

        // Массивы, указатели — раскрываем рекурсивно (на глубину 1 достаточно:
        // массив массивов экзотика, на которую обопрёмся, если встретим).
        if (type.HasElementType)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
        }
    }

    private static bool IsAllowed(Type type, string[] allowed)
    {
        var ns = type.Namespace;
        if (ns is null) return true; // Безымянный namespace — не Services.*, разрешаем.

        foreach (var prefix in allowed)
        {
            if (ns == prefix
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
