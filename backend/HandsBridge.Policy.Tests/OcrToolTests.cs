using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// OCR (шаг A4 «руки без UIA»): контракт <c>OcrClient</c> на фейковом HTTP-обработчике
/// (200, каждый reason 503, 502, 403, 413, 401, мусор, больше 8 МБ, без эндпоинта),
/// гейт инструмента (запрещённое окно, «update the agent»), потолок ответа 4000 символов,
/// <c>saveTo</c> через <c>HandsProjectFiles</c> и лог (картинка и текст в нём не бывают).
/// Мост грузится рефлексией из вывода <c>hands-bridge/</c> (тот же путь, что
/// <see cref="HandsBridgeGateRuntimeTests"/>); статика моста общая — тесты в его коллекции.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class OcrToolTests
{
    private const string OcrClientType = "ClaudeHomeServer.HandsBridge.OcrClient";
    private const string OcrToolType = "Sbroenne.WindowsMcp.Tools.OcrTool";

    /// <summary>Сборка моста в отдельном контексте (общий ленивый загрузчик).</summary>
    private static Assembly Bridge => HandsBridgeGateRuntimeTests.Bridge.Value;

    /// <summary>
    /// Готовый результат снимка для фейка <c>ScreenshotService</c> (читает выпущенный
    /// <c>ExecuteAsync</c>). Public: выпущенный тип живёт в отдельной динамической сборке,
    /// и к internal/privat вложенного класса он не дотянется (FieldAccessException).
    /// </summary>
    public static class Canned
    {
        public static object? Result;
    }

    // ---------- OcrClient: контракт на фейковом HTTP-обработчике ----------

    [Fact]
    public async Task Success_200_returns_the_contract_fields()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(200,
            """{"text":"hello world","model":"qwen3.8-27b","ms":123,"promptTokens":10,"completionTokens":5}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.True(outcome.Ok);
        Assert.Equal("hello world", outcome.Text);
        Assert.Equal("qwen3.8-27b", outcome.Model);
        Assert.Equal(123, outcome.Ms);
        Assert.Equal(10, outcome.PromptTokens);
        Assert.Equal(5, outcome.CompletionTokens);
    }

    [Theory]
    [InlineData("disabled", "OCR is disabled on the server")]
    [InlineData("local_unavailable", "local OCR model is unavailable")]
    [InlineData("busy_timeout", "OCR slot is busy")]
    [InlineData("page_timeout", "OCR page timed out")]
    public async Task Busy_503_each_reason_has_its_message(string reason, string expected)
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(503, $$"""{"reason":"{{reason}}"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal(reason, outcome.Reason);
        Assert.Contains(expected, outcome.Message!);
    }

    [Fact]
    public async Task BadResponse_502_is_a_clear_refusal()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(502, """{"reason":"bad_response"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("bad_response", outcome.Reason);
        Assert.Contains("unparseable", outcome.Message!);
    }

    [Fact]
    public async Task HandsRefused_403_is_a_clear_refusal()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(403, """{"error":"hands_refused"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("hands_refused", outcome.Reason);
        Assert.Contains("refused", outcome.Message!);
    }

    [Fact]
    public async Task TooLarge_413_says_shrink_the_region()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(413, """{"reason":"too_large"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("too_large", outcome.Reason);
        Assert.Contains("shrink the region", outcome.Message!);
    }

    [Fact]
    public async Task Unauthorized_401_is_a_clear_refusal()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(401, """{"error":"unauthorized"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("unauthorized", outcome.Reason);
    }

    // Старый сервер без маршрута /gw/.../ocr: сайдкар пропускает пустой 404 —
    // модель получает понятное «обновите сервер», а не голый статус.
    [Fact]
    public async Task NotSupported_404_empty_body_says_update_the_server()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(404, "")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("not_supported", outcome.Reason);
        Assert.Contains("server does not support ocr (update the server)", outcome.Message!);
    }

    // Маршрут есть, но проект хода не найден (fail-closed, блокер 3): 404 с телом
    // project_not_found — отдельное сообщение, не «обновите сервер».
    [Fact]
    public async Task ProjectNotFound_404_with_body_says_ask_the_owner()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(404, """{"error":"project_not_found"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("project_not_found", outcome.Reason);
        Assert.Contains("ask the owner", outcome.Message!);
    }

    [Fact]
    public async Task Garbage_200_is_bad_response_not_an_exception()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(200, "not json at all")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("bad_response", outcome.Reason);
        Assert.Contains("not valid JSON", outcome.Message!);
    }

    [Fact]
    public async Task Garbage_503_falls_back_to_busy()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(503, "garbage")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("busy", outcome.Reason);
    }

    [Fact]
    public async Task Image_over_8mb_is_refused_before_sending()
    {
        var handler = new FakeOcrHandler(_ => Json(200, """{"text":"x"}"""));
        SetupClient("http://fake/ocr", handler);

        var outcome = ReadOutcome(await CallClient(new byte[8 * 1024 * 1024 + 1]));

        Assert.False(outcome.Ok);
        Assert.Equal("too_large", outcome.Reason);
        Assert.Empty(handler.Requests); // отказ до отправки: запрос не ушёл
    }

    [Fact]
    public async Task Without_endpoint_the_client_says_update_the_agent()
    {
        SetupClient(null, new FakeOcrHandler(_ => Json(200, """{"text":"x"}""")));

        var outcome = ReadOutcome(await CallClient(SmallPng()));

        Assert.False(outcome.Ok);
        Assert.Equal("no_endpoint", outcome.Reason);
        Assert.Contains("update the agent", outcome.Message!);
    }

    // ---------- инструмент: гейт, потолок, saveTo, лог ----------

    [Theory]
    [InlineData(4000, false)] // ровно потолок — без обрезки
    [InlineData(4001, false)] // saveTo не задан — пометка «обрезано, задайте saveTo»
    [InlineData(4001, true)] // saveTo задан — без пометки, полный текст в файле
    public void Reply_is_capped_at_4000_characters(int length, bool saved)
    {
        var method = Bridge.GetType(OcrToolType, throwOnError: true)!
            .GetMethod("TruncateForReply", BindingFlags.NonPublic | BindingFlags.Static)!;
        var (text, truncated) = ((string, bool))method.Invoke(null, [new string('a', length), saved])!;

        if (length <= 4000)
        {
            Assert.False(truncated);
            Assert.Equal(length, text.Length);
            return;
        }

        Assert.True(truncated);
        if (saved)
        {
            Assert.Equal(4000, text.Length);
            Assert.DoesNotContain("truncated", text, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith("aaaa", text);
            Assert.Contains("truncated", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(text.Length > 4000, "пометка дописана после обрезки");
        }
    }

    /// <summary>
    /// Запрещённое окно (гейт, уровень политики): cmd — «ввод туда — исполнение команд»,
    /// OCR такое окно не читает; блокнот человека — читает.
    /// </summary>
    [Fact]
    public void Forbidden_window_is_denied_by_the_gate()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(65552, 4)
            .Process(3, Machine.Notepad).Window(65553, 3);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("window", "65552", null, null, null, null);
        Assert.False(denied.Allowed);
        Assert.Contains("interpreter, a terminal or Explorer", denied.Reason);

        Assert.True(policy.CheckOcr("window", "65553", null, null, null, null).Allowed);
    }

    /// <summary>
    /// Region целиком над разрешённым окном (блокнот): сетка точек не находит запрещённого окна —
    /// снимок разрешён.
    /// </summary>
    [Fact]
    public void Region_over_allowed_window_is_allowed()
    {
        var windows = new FakeWindows()
            .Process(3, Machine.Notepad).Window(300, 3)
            .Cover(0, 0, 1000, 1000, 300);
        var policy = new HandsPolicy(windows);

        Assert.True(policy.CheckOcr("region", null, 0, 0, 1000, 1000).Allowed);
    }

    /// <summary>
    /// Region поверх запрещённого процесса (терминал в центре области): сетка ловит его в центре и
    /// отказывает ДО снимка.
    /// </summary>
    [Fact]
    public void Region_over_forbidden_process_in_center_is_denied_before_capture()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Cover(0, 0, 1000, 1000, 300)
            .At(500, 500, 400);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
        Assert.Contains("cmd.exe", denied.Reason);
    }

    /// <summary>
    /// Запрещённое окно (терминал) прижато к УГЛУ области: шаг сетки его не задевает, ловит
    /// отдельная проверка углов.
    /// </summary>
    [Fact]
    public void Region_with_forbidden_window_in_corner_is_denied()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Cover(0, 0, 1000, 1000, 300)
            .At(999, 0, 400);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
    }

    /// <summary>
    /// Запрещённое окно ПОЛНОСТЬЮ перекрыто разрешённым сверху: под каждой точкой области верхнее
    /// окно — блокнот, терминал под ним не виден — снимок разрешён.
    /// </summary>
    [Fact]
    public void Region_with_forbidden_window_covered_by_allowed_is_allowed()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Cover(0, 0, 1000, 1000, 300);
        var policy = new HandsPolicy(windows);

        Assert.True(policy.CheckOcr("region", null, 0, 0, 1000, 1000).Allowed);
    }

    /// <summary>
    /// Экранная цель (primary_screen) при наличии ВИДИМОГО запрещённого окна на экране: сетка по
    /// всему виртуальному экрану его ловит и отказывает ДО снимка.
    /// </summary>
    [Fact]
    public void Screen_with_forbidden_visible_window_is_denied()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Bounds(new HandsScreenBounds(0, 0, 1920, 1080))
            .Cover(0, 0, 1920, 1080, 300)
            .At(960, 540, 400);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("primary_screen", null, null, null, null, null);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
    }

    /// <summary>
    /// Неизвестный процесс под точкой области (образ не прочитался) — как у target=window:
    /// закрыто по умолчанию, отказ ДО снимка.
    /// </summary>
    [Fact]
    public void Region_with_unknown_process_under_point_is_denied()
    {
        var windows = new FakeWindows()
            .Process(6, null).Window(600, 6)
            .Cover(0, 0, 1000, 1000, 600);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
    }

    /// <summary>
    /// Окно без известного процесса (мост без Configure): закрыто по умолчанию —
    /// инструмент отказывает ДО снимка и ДО отправки на сервер.
    /// </summary>
    [Fact]
    public async Task Window_with_unknown_process_is_denied_before_anything()
    {
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(200, """{"text":"x"}""")));

        var (isError, text) = await HandsBridgeGateRuntimeTests.Call(OcrToolType,
            new() { ["target"] = "window", ["windowHandle"] = "65552" });

        Assert.True(isError, text);
        Assert.Contains("interpreter, a terminal or Explorer", text);
    }

    /// <summary>
    /// Гейту — known bounds: screen-цель (<c>primary_screen</c>) проходит <c>CheckOcrScreen</c>
    /// (fail-closed: неизвестные границы — отказ). По умолчанию гейт — <c>NoWindows</c> (null).
    /// Возвращает исходную политику, чтобы тест вернул её в <c>finally</c> (статика моста общая).
    /// </summary>
    private static object SetGatePolicyWithKnownBounds()
    {
        var gateType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
        var policyField = gateType.GetField("s_policy", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = policyField.GetValue(null)!;

        // HandsPolicy — в ALC моста (тестовая копия — другой тип), и живёт в сборке Policy, а не
        // HandsBridge: тип берём от живого экземпляра, а не строкой. Обёртываем эмитированный
        // IHandsWindowSystem (known bounds, пустое перечисление); browserProfilesRoot — null.
        var policyType = original.GetType();
        policyField.SetValue(null, Activator.CreateInstance(policyType, [FakeWindowSystem(), null])!);
        return original;
    }

    /// <summary>Возвращает исходную политику гейта (статика моста общая для коллекции).</summary>
    private static void RestoreGatePolicy(object original)
    {
        var gateType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
        gateType.GetField("s_policy", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, original);
    }

    /// <summary>
    /// Эмит <c>IHandsWindowSystem</c> в ALC моста (тестовая копия интерфейса — другой тип):
    /// known bounds (0,0,1920,1080), пустое перечисление, 0 под точками — screen-цель проходит гейт.
    /// Без состояния: один экземпляр на все тесты коллекции (serial).
    /// </summary>
    private static object? s_fakeWindowSystem;
    private static object FakeWindowSystem() => s_fakeWindowSystem ??= EmitFakeWindowSystem(BridgeWindowSystemType());

    /// <summary>
    /// Bridge-контекстный <c>IHandsWindowSystem</c>: тип поля <c>&lt;windows&gt;P</c> живого <c>HandsPolicy</c>
    /// (сборка Policy, а не HandsBridge — строкой из <c>Bridge</c> не резолвится; primary ctor → суффикс P).
    /// </summary>
    private static Type BridgeWindowSystemType()
    {
        var gateType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
        var policy = gateType.GetField("s_policy", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return policy.GetType().GetField("<windows>P", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType;
    }

    private static object EmitFakeWindowSystem(Type interfaceType)
    {
        // HandsScreenBounds / HandsVisibleWindow — из сигнатур методов интерфейса (сборка Policy).
        var boundsType = interfaceType.GetMethod("GetVirtualScreenBounds")!.ReturnType.GetGenericArguments()[0];
        var visibleType = interfaceType.GetMethod("EnumerateVisibleWindows")!.ReturnType.GetGenericArguments()[0];

        var dynAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("OcrToolTestsWinDyn"), AssemblyBuilderAccess.Run);
        var module = dynAssembly.DefineDynamicModule("OcrToolTestsWinDyn");
        var tb = module.DefineType("FakeWindowSystem", TypeAttributes.Public, typeof(object), [interfaceType]);

        // int? GetWindowProcessId(long) → null
        DefineConstMethod(tb, "GetWindowProcessId", typeof(int?), [typeof(long)],
            il => { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret); });
        // string? GetProcessImagePath(int) → null
        DefineConstMethod(tb, "GetProcessImagePath", typeof(string), [typeof(int)],
            il => { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret); });
        // long GetRootWindowAt(int, int) → 0
        DefineConstMethod(tb, "GetRootWindowAt", typeof(long), [typeof(int), typeof(int)],
            il => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); });
        // long GetRootWindow(long) → 0
        DefineConstMethod(tb, "GetRootWindow", typeof(long), [typeof(long)],
            il => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); });
        // long GetForegroundWindow() → 0
        DefineConstMethod(tb, "GetForegroundWindow", typeof(long), [],
            il => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); });

        // HandsScreenBounds? GetVirtualScreenBounds() → new(0, 0, 1920, 1080)
        var boundsCtor = boundsType.GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int)])!;
        var nullableCtor = typeof(Nullable<>).MakeGenericType(boundsType).GetConstructor([boundsType])!;
        DefineConstMethod(tb, "GetVirtualScreenBounds", typeof(Nullable<>).MakeGenericType(boundsType), [],
            il =>
            {
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, 1920);
                il.Emit(OpCodes.Ldc_I4, 1080);
                il.Emit(OpCodes.Newobj, boundsCtor);
                il.Emit(OpCodes.Newobj, nullableCtor);
                il.Emit(OpCodes.Ret);
            });

        // IReadOnlyList<HandsVisibleWindow>? EnumerateVisibleWindows() → пустой список
        var listCtor = typeof(List<>).MakeGenericType(visibleType).GetConstructor([])!;
        DefineConstMethod(tb, "EnumerateVisibleWindows", typeof(IReadOnlyList<>).MakeGenericType(visibleType), [],
            il => { il.Emit(OpCodes.Newobj, listCtor); il.Emit(OpCodes.Ret); });

        return tb.CreateType()!.GetConstructor([])!.Invoke([]);
    }

    /// <summary>Метод с фиксированным телом: <paramref name="body"/> эмитит IL до <c>ret</c>.</summary>
    private static void DefineConstMethod(TypeBuilder tb, string name, Type returnType, Type[] paramTypes, Action<ILGenerator> body)
    {
        // Virtual|NewSlot|Final: без Virtual CLR не сопоставляет метод с интерфейсным.
        var method = tb.DefineMethod(name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
            | MethodAttributes.NewSlot | MethodAttributes.Final,
            returnType, paramTypes);
        body(method.GetILGenerator());
    }

    /// <summary>
    /// Без <c>--ocr-endpoint</c> (агент старый) инструмент отвечает «update the agent»
    /// — до снимка и до отправки на сервер.
    /// </summary>
    [Fact]
    public async Task Without_endpoint_the_tool_says_update_the_agent()
    {
        SetupClient(null, new FakeOcrHandler(_ => Json(200, """{"text":"x"}""")));
        var originalPolicy = SetGatePolicyWithKnownBounds();
        try
        {
            var (isError, text) = await HandsBridgeGateRuntimeTests.Call(OcrToolType,
                new() { ["target"] = "primary_screen" });

            Assert.True(isError, text);
            Assert.Contains("update the agent", text);
        }
        finally
        {
            RestoreGatePolicy(originalPolicy);
        }
    }

    /// <summary>
    /// Полный цикл на Linux: фейковый снимок (PNG в памяти) → фейковый сервер (200, длинный
    /// текст) → <c>saveTo</c> пишет ПОЛНЫЙ текст в проект через <c>HandsProjectFiles</c>,
    /// а в ответе — обрезанные 4000 символов и фактический путь.
    /// </summary>
    [Fact]
    public async Task SaveTo_writes_the_full_text_to_the_project()
    {
        var fullText = new string('x', 5000);
        SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(200,
            JsonSerializer.Serialize(new { text = fullText, model = "m", ms = 1, promptTokens = 1, completionTokens = 1 }))));
        var originalPolicy = SetGatePolicyWithKnownBounds();

        // Снимок: фейк ScreenshotService (ExecuteAsync — canned PNG в памяти, на диск не пишется);
        // шов OcrTool.CaptureService — WindowsToolsBase (и его WinAPI) тест не трогает.
        Canned.Result = ScreenshotSuccess(SmallPng());
        var screenshotType = Bridge.GetType("Sbroenne.WindowsMcp.Capture.ScreenshotService", throwOnError: true)!;
        var fake = EmitFakeScreenshotService(screenshotType);
        // CaptureService — internal: по умолчанию GetProperty его не видит
        var ocrToolType = Bridge.GetType(OcrToolType, throwOnError: true)!;
        var captureProp = ocrToolType.GetProperty(
            "CaptureService", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        captureProp.SetValue(null, fake);

        // Корень проекта: HandsProjectFiles в свежем временном каталоге.
        var root = Path.Combine(Path.GetTempPath(), "hands-a4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectFilesType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
            var projectFiles = Activator.CreateInstance(projectFilesType, (object?)root)!;
            var gateType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
            gateType.GetField("s_projectFiles", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, projectFiles);

            var (isError, text) = await HandsBridgeGateRuntimeTests.Call(OcrToolType,
                new() { ["target"] = "primary_screen", ["saveTo"] = "ocr-out.txt" });

            Assert.False(isError, text);
            var reply = JsonSerializer.Deserialize<JsonElement>(text);
            Assert.Equal(4000, reply.GetProperty("text").GetString()!.Length);
            Assert.True(reply.GetProperty("truncated").GetBoolean());
            Assert.Equal("ocr-out.txt", reply.GetProperty("savedPath").GetString());

            // Полный текст (5000) — в файле проекта, а не в ответе.
            var fileText = File.ReadAllText(Path.Combine(root, "ocr-out.txt"), Encoding.UTF8);
            Assert.Equal(fullText, fileText);
        }
        finally
        {
            RestoreGatePolicy(originalPolicy);
            captureProp.SetValue(null, null); // шов вернуть, чтобы не влиял на другие тесты
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Лог (HandsLog): в нём агрегат (статус, размер, длительность), но НЕ картинка и НЕ текст.
    /// </summary>
    [Fact]
    public async Task Image_and_text_do_not_reach_the_log()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "hands-a4-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var handsLogType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsLog", throwOnError: true)!;
            handsLogType.GetMethod("Configure")!.Invoke(null, [logPath]);

            var text = "the recognized secret text";
            SetupClient("http://fake/ocr", new FakeOcrHandler(_ => Json(200,
                JsonSerializer.Serialize(new { text, model = "m", ms = 1, promptTokens = 1, completionTokens = 1 }))));

            var png = SmallPng();
            await CallClient(png);

            var log = File.ReadAllText(logPath, Encoding.UTF8);
            Assert.Contains("ocr: 200", log); // агрегат записан
            Assert.DoesNotContain(text, log); // текст — нет
            Assert.DoesNotContain(Convert.ToBase64String(png), log); // картинка — нет
        }
        finally
        {
            var handsLogType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsLog", throwOnError: true)!;
            handsLogType.GetMethod("Configure")!.Invoke(null, [(object?)null]);
            File.Delete(logPath);
        }
    }

    // ---------- швы ----------

    /// <summary>Задаёт <c>OcrClient.Endpoint</c> и <c>HandlerFactory</c> (рефлексия в сборку моста).</summary>
    private static void SetupClient(string? endpoint, FakeOcrHandler handler)
    {
        var type = Bridge.GetType(OcrClientType, throwOnError: true)!;
        type.GetProperty("Endpoint")!.SetValue(null, endpoint);
        // HandlerFactory — internal: по умолчанию GetProperty его не видит
        type.GetProperty("HandlerFactory", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            !.SetValue(null, (Func<HttpMessageHandler>)(() => handler));
    }

    /// <summary>Вызывает <c>OcrClient.RecognizeAsync</c> и читает результат (рефлексия).</summary>
    private static async Task<object> CallClient(byte[] png)
    {
        var type = Bridge.GetType(OcrClientType, throwOnError: true)!;
        var task = (Task)type.GetMethod("RecognizeAsync")!.Invoke(null, [png, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static (bool Ok, string? Text, string? Model, long? Ms, int? PromptTokens, int? CompletionTokens, string? Reason, string? Message)
        ReadOutcome(object outcome)
    {
        var t = outcome.GetType();
        return (
            (bool)t.GetProperty("Ok")!.GetValue(outcome)!,
            (string?)t.GetProperty("Text")!.GetValue(outcome),
            (string?)t.GetProperty("Model")!.GetValue(outcome),
            (long?)t.GetProperty("Ms")!.GetValue(outcome),
            (int?)t.GetProperty("PromptTokens")!.GetValue(outcome),
            (int?)t.GetProperty("CompletionTokens")!.GetValue(outcome),
            (string?)t.GetProperty("Reason")!.GetValue(outcome),
            (string?)t.GetProperty("Message")!.GetValue(outcome));
    }

    /// <summary>Успешный снимок: PNG в памяти (base64), без диска.</summary>
    private static object ScreenshotSuccess(byte[] png)
    {
        var resultType = Bridge.GetType("Sbroenne.WindowsMcp.Models.ScreenshotControlResult", throwOnError: true)!;
        var factory = resultType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetParameters().Length == 4
                && m.GetParameters()[0].ParameterType == typeof(string));
        return factory.Invoke(null, [Convert.ToBase64String(png), 10, 10, "png"])!;
    }

    /// <summary>
    /// Фейк <c>ScreenshotService</c> для сборки моста: подкласс через <see cref="TypeBuilder"/>
    /// (на момент компиляции теста видна только его собственная копия Policy-сборки).
    /// <c>ExecuteAsync</c> возвращает готовый результат из <see cref="Canned"/>.
    /// </summary>
    private static object EmitFakeScreenshotService(Type baseType)
    {
        var dynAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("OcrToolTestsDyn"), AssemblyBuilderAccess.Run);
        var module = dynAssembly.DefineDynamicModule("OcrToolTestsDyn");
        var tb = module.DefineType("FakeScreenshotService", TypeAttributes.Public, baseType, []);

        var baseCtor = baseType.GetConstructors()[0]!;
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, []);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);

        var requestType = Bridge.GetType("Sbroenne.WindowsMcp.Models.ScreenshotControlRequest", throwOnError: true)!;
        var resultType = Bridge.GetType("Sbroenne.WindowsMcp.Models.ScreenshotControlResult", throwOnError: true)!;
        var methodDef = tb.DefineMethod(
            "ExecuteAsync",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(Task<>).MakeGenericType(resultType),
            [requestType, typeof(CancellationToken)]);
        var il2 = methodDef.GetILGenerator();
        var cannedField = typeof(Canned).GetField("Result")!;
        il2.Emit(OpCodes.Ldsfld, cannedField);
        il2.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(resultType));
        il2.Emit(OpCodes.Ret);

        return tb.CreateType()!.GetConstructor([])!.Invoke([]);
    }

    /// <summary>Маленький «PNG» (содержимое для клиента не важно — сервер фейковый).</summary>
    private static byte[] SmallPng() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Фейковый HTTP-обработчик: запоминает запросы, отвечает по рецепту.</summary>
    private sealed class FakeOcrHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeOcrHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
