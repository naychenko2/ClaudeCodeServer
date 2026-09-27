using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границы <c>ILocalLlmClient</c>: тип локального движка не должен упоминаться
/// ни одним типом вне сборки <c>ClaudeHomeServer.Llm</c>, кроме единственного легитимного
/// прямого потребителя — <c>SessionManager</c> (голосовой ход идёт мимо
/// CheapTextRunner и держит клиента напрямую). Цель — закрыть три обхода
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
    // SessionManager держит клиента ради голосового хода (RunLocalVoiceTurnAsync):
    // это собственный путь мимо CheapTextRunner, у него собственный стриминг и
    // своя логика прерывания. Никаких других легитимных потребителей быть не
    // должно: все текстовые места идут через ICheapTextRunner, прогрев — через
    // IHostedService в подсистеме Llm, настройки читает LocalActionRouter.
    private static readonly string[] AllowedOwners =
    [
        "ClaudeHomeServer.Services.SessionManager",
    ];

    [Fact]
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
                var ownerFull = t.FullName ?? t.Name;
                var allowed = AllowedOwners.Any(a =>
                    ownerFull == a || ownerFull.StartsWith(a + "+", StringComparison.Ordinal));
                if (allowed) continue;

                foreach (var needle in needlesMentioned)
                {
                    violations.Add($"{t.FullName} → {needle}");
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
            + "ClaudeHomeServer.Llm и вне allow-list. Легитимный прямой потребитель — "
            + "только SessionManager (голосовой ход). Все прочие места идут через "
            + "ICheapTextRunner или LocalActionRouter. Нарушения:\n"
            + string.Join("\n", violations));
        if (violations.Count > 0)
        {
            _out.WriteLine($"Нарушения ({violations.Count}):");
            foreach (var v in violations) _out.WriteLine("  " + v);
        }
    }
}