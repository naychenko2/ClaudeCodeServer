using System.Reflection;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границы <c>ILocalLlmClient</c>: тип локального движка не должен упоминаться
/// ни одним типом вне сборки <c>ClaudeHomeServer.Llm</c>, кроме голосового хода —
/// <c>LocalVoiceTurn</c> (собственный путь мимо <c>CheapTextRunner</c>, держит клиента
/// напрямую; фасад только переадресует ему) — и DI-проводки голосовой роли в
/// <c>SessionCoreRegistration</c>: она разрешена не целиком по типу, а только методу
/// регистрации, конструирующему <c>LocalVoiceTurn</c> (блок «Суженное разрешение» ниже).
/// Цель — закрыть три обхода
/// маршрутизации, через которые локальную модель могли звать кто угодно: <c>OllamaActionRankService</c>
/// (раньше прямой вызов), <c>UsageController</c> (снимок настроек через клиента)
/// и фоновый прогрев из Program.cs (теперь IHostedService в подсистеме).
///
/// Тест работает на готовом <see cref="BoundaryIlScanner.CollectAllReferencedTypes"/>:
/// видит static-вызовы, DI-резолвы <c>GetRequiredService&lt;T&gt;()</c>, generic-аргументы,
/// async-state-машины и замыкания (см. комментарий IlBoundaryRegressionTests).
///
/// Защита от вакуумного прохода: статический конструктор форсит загрузку сборки
/// <c>ClaudeHomeServer.Llm</c> (без неё AppDomain.GetAssemblies() не вернёт типы
/// вертикали — прецедент ревью выноса Git, 17/17 зелёных при пустом наборе). Каждый
/// искомый тип проверяется на наличие через рефлексию: не нашёлся — Assert.Fail
/// с понятным диагнозом, не а не молчаливый проход.
///
/// Мутация: добавление <c>ILocalLlmClient</c> в конструктор любого класса вне
/// <c>ClaudeHomeServer.Llm</c> (и не в allow-list) должно ловить этот тест.
/// </summary>
public class LocalLlmClientBoundaryTests
{
    private readonly ITestOutputHelper _out;

    // Форс-загрузка сборки `ClaudeHomeServer.Llm`: до выноса типы жили в Main,
    // теперь у вертикали своя .dll, и без явного typeof() она не подгружена в
    // изолированном прогоне теста. Без этого «нет типов → нет нарушений → зелёный».
    static LocalLlmClientBoundaryTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Llm.LocalActionRouter).Assembly;
    }

    public LocalLlmClientBoundaryTests(ITestOutputHelper output) => _out = output;

    // Легитимные прямые потребители ILocalLlmClient — allow-list точечных имён.
    // Голосовой ход (RunLocalVoiceTurnAsync) с S3-6 живёт в LocalVoiceTurn: это
    // собственный путь мимо CheapTextRunner, у него собственный стриминг и своя
    // логика прерывания; фасад держит ссылку на него же (параметр конструктора
    // LocalVoiceTurn — тот же голосовой ход). SessionCoreRegistration в общий
    // allow-list НЕ входит: он не потребитель движка, а DI-проводка ролей, и
    // сплошное разрешение целого типа позволило бы любой правке регистрации
    // назвать клиента молча (замечание ревью S3-7, правка 40fe9bed). Регистрация
    // разрешена узко и проверяется отдельно: только проводка голосовой роли —
    // см. блок «Суженное разрешение» ниже.
    // Никаких других легитимных потребителей быть не должно: все текстовые места
    // идут через ICheapTextRunner, прогрев — через IHostedService в подсистеме Llm,
    // настройки читает LocalActionRouter.
    private static readonly string[] AllowedOwners =
    [
        "ClaudeHomeServer.Services.SessionManager",
        "ClaudeHomeServer.Services.Sessions.LocalVoiceTurn",
    ];

    // Регистрация ядра (DI-проводка) и голосовая роль — единственное легитимное
    // место локального типа внутри регистрации (её проводка из конструктора фасада,
    // вызова движка в регистрации нет).
    private const string RegistrationOwner = "ClaudeHomeServer.Services.Composition.SessionCoreRegistration";
    private const string VoiceRole = "ClaudeHomeServer.Services.Sessions.LocalVoiceTurn";

    [Fact]
    [Trait("Category", "Slow")]
    public void ILocalLlmClient_НеВыходитЗаГраницуВертикалиLlm()
    {
        var asms = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n
                && (n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                && !n.EndsWith(".Tests", StringComparison.Ordinal))
            .ToList();

        // Искомые типы: интерфейс + обе реализации. По образцу IlBoundaryRegressionTests
        // — «не нашёлся → Fail с диагнозом»: иначе мутация (тип переименовали/удалили)
        // прошла бы молча.
        var needles = new (string Label, string FullName)[]
        {
            ("ILocalLlmClient (контракт)", "ClaudeHomeServer.Services.Llm.ILocalLlmClient"),
            ("OllamaClient (Ollama)", "ClaudeHomeServer.Services.Llm.OllamaClient"),
            ("LlamaServerClient (llama-server)", "ClaudeHomeServer.Services.Llm.LlamaServerClient"),
        };
        var needleTypes = new List<Type>();
        var missing = new List<string>();
        foreach (var (label, full) in needles)
        {
            var t = asms
                .Select(a => a.GetType(full, false))
                .FirstOrDefault(x => x is not null);
            if (t is null) missing.Add($"{label}: тип {full} не найден рефлексией — "
                + "вертикаль Llm не загружена или тип переименован");
            else needleTypes.Add(t);
        }
        Assert.True(missing.Count == 0,
            "сторож не смог разрешить искомые типы — проверка границ не состоялась: "
            + string.Join("; ", missing));

        var llmAssembly = needleTypes[0].Assembly;
        var violations = new List<string>();

        // Лог проверенных сборок в тестовый вывод — при падении видно сразу,
        // какая сборка могла притащить нарушение и не прошла ли форс-загрузка.
        _out.WriteLine($"Проверенные сборки ({asms.Count}): "
            + string.Join(", ", asms.Select(a => a.GetName().Name)));

        // Перебираем все типы во ВСЕХ продовых сборках: для каждого собираем
        // упоминания в IL тел методов и сигнатурах. Если тип вне Llm-сборки
        // ссылается на искомый — это нарушение, если тип не в allow-list.
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                // Тип внутри Llm — пропускаем: внутренние ссылки разрешены.
                if (t.Assembly == llmAssembly) continue;

                var ownerFull = t.FullName ?? t.Name;
                // Регистрация пропускает общий обход: она проверяется ниже и узко, по
                // методам; её nested-замыкания покрыты AllMethodsWithNested по регистрации.
                if (ownerFull == RegistrationOwner || ownerFull.StartsWith(RegistrationOwner + "+", StringComparison.Ordinal))
                    continue;

                var needlesMentioned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var referenced in BoundaryIlScanner.CollectAllReferencedTypes(t))
                {
                    foreach (var needle in needleTypes)
                    {
                        if (referenced.FullName == needle.FullName
                            || (referenced.FullName?.StartsWith(needle.FullName + "+", StringComparison.Ordinal) ?? false))
                        {
                            needlesMentioned.Add(needle.FullName!);
                        }
                    }
                }

                if (needlesMentioned.Count == 0) continue;

                // Allow-list учитывает nested-типы allow-listed типа: компилятор
                // генерирует для async-методов отдельный класс
                // `SessionManager+<RunLocalVoiceTurnAsync>d__238` (state machine), и
                // ссылки из тела async-метода живут в нём, а не в самом
                // SessionManager. Без суффикса `+` сторож видел бы легитимного
                // прямого потребителя (голосовой ход) как нарушение — та же грабля,
                // что IlBoundaryRegressionTests описывает как «3 из 7 известных
                // швов в nested-типах, без обхода сторож остаётся рабочим на вид».
                var allowed = AllowedOwners.Any(a =>
                    ownerFull == a || ownerFull.StartsWith(a + "+", StringComparison.Ordinal));
                if (allowed) continue;

                foreach (var needle in needlesMentioned)
                {
                    violations.Add($"{t.FullName} → {needle}");
                }
            }
        }

        // Суженное разрешение регистрации (S3-7b, решение по замечанию ревью):
        // SessionCoreRegistration может назвать локальный движок только там, где он
        // ставит голосовую роль. Проверка по каждому методу (включая замыкания):
        // если тело метода ссылается на иглу — то же тело обязано конструировать
        // LocalVoiceTurn (это и есть проводка голосовой роли). Иное место регистрации —
        // её сигнатура (поля, параметры методов): голосовая роль там не строится,
        // игла только там — нарушение.
        var registration = asms
            .Select(a => a.GetType(RegistrationOwner, false))
            .FirstOrDefault(x => x is not null);
        Assert.True(registration is not null,
            $"сторож не смог разрешить тип {RegistrationOwner} — суженное разрешение "
            + "регистрации не проверялась");
        var bodyMentions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in BoundaryIlScanner.AllMethodsWithNested(registration!))
        {
            var mentioned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var referenced in BoundaryIlScanner.TypesFromBody(method))
            {
                foreach (var needle in needleTypes)
                {
                    if (referenced.FullName == needle.FullName
                        || (referenced.FullName?.StartsWith(needle.FullName + "+", StringComparison.Ordinal) ?? false))
                    {
                        mentioned.Add(needle.FullName!);
                    }
                }
            }
            if (mentioned.Count == 0) continue;
            foreach (var n in mentioned) bodyMentions.Add(n);
            // Легитимное место локального типа — сборка голосовой роли: вызов конструктора
            // LocalVoiceTurn в том же теле. Просто назвать роль (GetRequiredService<LocalVoiceTurn>
            // в чужой проводке) недостаточно: клиент принадлежит там, где роль собирается.
            var constructsVoiceRole = BoundaryIlScanner.CalledMethods(method)
                .Any(c => c is ConstructorInfo && c.DeclaringType?.FullName == VoiceRole);
            if (constructsVoiceRole) continue;
            foreach (var n in mentioned)
                violations.Add($"{method.DeclaringType?.FullName ?? method.Name}::{method.Name} → {n}");
        }
        foreach (var referenced in BoundaryIlScanner.TypesFromSignatures(registration!))
        {
            foreach (var needle in needleTypes)
            {
                if (referenced.FullName == needle.FullName
                    || (referenced.FullName?.StartsWith(needle.FullName + "+", StringComparison.Ordinal) ?? false))
                {
                    if (bodyMentions.Add(needle.FullName!))
                        violations.Add($"{registration.FullName} (сигнатура) → {needle.FullName}");
                }
            }
        }

        // Стат.конструктор форсит загрузку Llm-сборки, но на всякий случай проверим
        // и набор прод-сборок: если фильтр сломался, набор пуст и сторож молчит —
        // это и есть та грабля, ради которой форс-загрузка существует.
        Assert.True(asms.Count >= 4,
            "ожидается как минимум Main+Core+Llm+Tests-dll: фактически загружено "
            + asms.Count);

        Assert.True(violations.Count == 0,
            "ILocalLlmClient/OllamaClient/LlamaServerClient упомянуты типами вне "
            + "ClaudeHomeServer.Llm и вне allow-list. Легитимные прямые потребители — "
            + "LocalVoiceTurn (голосовой ход) и фасад (его переадресация); регистрация — "
            + "только проводка голосовой роли. Все прочие места идут через "
            + "ICheapTextRunner или LocalActionRouter. Нарушения:\n"
            + string.Join("\n", violations));
        if (violations.Count > 0)
        {
            _out.WriteLine($"Нарушения ({violations.Count}):");
            foreach (var v in violations) _out.WriteLine("  " + v);
        }
    }
}