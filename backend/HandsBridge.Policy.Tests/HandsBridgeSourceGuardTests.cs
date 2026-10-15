using System.Text.RegularExpressions;
using ClaudeHomeServer.HandsBridge.Policy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сторожа по исходникам моста. Сам мост — net10.0-windows, на Linux CI его код не исполнить,
/// поэтому то, что гейт вообще вызывается, проверяется по тексту: каждый инструмент зовёт свою
/// проверку и возвращает отказ ДО первого <c>await</c>, а лишних инструментов в сборке нет.
/// </summary>
public class HandsBridgeSourceGuardTests
{
    private static readonly string BridgeDir = FindBridgeDir();

    /// <summary>Инструмент → файл и вызов гейта, который обязан стоять до первого действия.</summary>
    public static readonly TheoryData<string, string, string> Gates = new()
    {
        { HandsTools.App, "Tools/AppTool.cs", "HandsGate.Policy.CheckLaunch(" },
        { HandsTools.UiSnapshot, "Automation/Tools/UISnapshotTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiSnapshot," },
        { HandsTools.UiFind, "Automation/Tools/UIFindTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiFind," },
        { HandsTools.UiClick, "Automation/Tools/UIClickTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiClick," },
        { HandsTools.UiType, "Automation/Tools/UITypeTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiType," },
        { HandsTools.UiRead, "Automation/Tools/UIReadTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiRead," },
        { HandsTools.WindowManagement, "Tools/WindowManagementTool.cs", "HandsPolicy.CheckWindowAction(" },
        { HandsTools.ScreenshotControl, "Tools/ScreenshotControlTool.cs", "HandsPolicy.CheckScreenshot(" },
        // Браузерная рука (ADR-016 §7.1): инструменты появляются в Ш6, до него эти строки красные
        { HandsTools.BrowserNavigate, "Tools/BrowserNavigateTool.cs", "HandsPolicy.CheckBrowserUrl(" },
        { HandsTools.BrowserSnapshot, "Tools/BrowserSnapshotTool.cs", "HandsPolicy.CheckBrowserSnapshot(" },
        { HandsTools.BrowserClick, "Tools/BrowserClickTool.cs", "HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick," },
        { HandsTools.BrowserType, "Tools/BrowserTypeTool.cs", "HandsPolicy.CheckBrowserRef(HandsTools.BrowserType," },
        { HandsTools.BrowserTabs, "Tools/BrowserTabsTool.cs", "HandsPolicy.CheckBrowserTabs(" },
        { HandsTools.BrowserWait, "Tools/BrowserWaitTool.cs", "HandsPolicy.CheckBrowserWait(" },
        { HandsTools.BrowserScreenshot, "Tools/BrowserScreenshotTool.cs", "HandsPolicy.CheckBrowserScreenshot(" },
        { HandsTools.BrowserQuery, "Tools/BrowserQueryTool.cs", "HandsPolicy.CheckBrowserQuery(" },
        { HandsTools.BrowserEvaluate, "Tools/BrowserEvaluateTool.cs", "HandsPolicy.CheckBrowserEvaluate(" },
        // Руки без UIA: мышь — гейт по точке (форма + границы + окно под точкой) до первого действия
        { HandsTools.MouseControl, "Tools/MouseControlTool.cs", "HandsGate.Policy.CheckPointer(" },
    };

    [Fact]
    public void Bridge_exposes_exactly_the_hands_tools()
    {
        var names = Directory.EnumerateFiles(BridgeDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\[McpServerTool\(Name = ""([^""]+)""").Select(m => m.Groups[1].Value))
            .Order()
            .ToList();

        // TODO(D2a–D2e): убрать при добавлении инструмента в мост.
        // Инструменты уже в HandsTools.All, но в мосту появятся в D2a–D2e — до того их отсутствие норма.
        var notYetInBridge = new[]
        {
            HandsTools.KeyboardControl, HandsTools.ClipboardText, HandsTools.HandsBatch,
            HandsTools.ScanDocument, HandsTools.ScanCanvas,
        };
        var expected = HandsTools.All.Where(t => !notYetInBridge.Contains(t)).Order().ToList();

        Assert.Equal(expected, names);
        // Напоминание: когда инструмент появится в мосту, тест покраснеет и заставит убрать имя из списка
        foreach (var name in notYetInBridge)
            Assert.DoesNotContain(name, names);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public void Tool_calls_its_gate_before_any_action(string tool, string file, string gateCall)
    {
        var body = ToolBody(file, tool);

        var gateAt = body.IndexOf(gateCall, StringComparison.Ordinal);
        var denyAt = body.IndexOf($"return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(gateAt >= 0, $"{tool}: нет вызова гейта «{gateCall}»");
        Assert.True(denyAt > gateAt, $"{tool}: отказ гейта не возвращается");
        Assert.True(firstAwait < 0 || denyAt < firstAwait, $"{tool}: действие раньше гейта");
    }

    /// <summary>
    /// Сигнал «ход действует руками» (<see cref="HandsActivity"/>): каждый инструмент, который может
    /// тронуть машину, сообщает о действии сразу после гейта — после возврата отказа, чтобы отказ
    /// сигнал не поднимал, и до первого действия. Чтение дерева окна о действии не сообщает.
    /// </summary>
    [Theory]
    [MemberData(nameof(Gates))]
    public void Tool_reports_its_action_after_the_gate(string tool, string file, string gateCall)
    {
        var body = ToolBody(file, tool);
        var actedAt = body.IndexOf("HandsGate.Acted(HandsTools.", StringComparison.Ordinal);

        if (tool is HandsTools.UiSnapshot or HandsTools.UiFind or HandsTools.UiRead)
        {
            Assert.True(actedAt < 0, $"{tool}: чтение не должно зажигать плашку");
            return;
        }

        var gateAt = body.IndexOf(gateCall, StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(actedAt >= 0, $"{tool}: нет HandsGate.Acted — первое действие не зажжёт плашку");
        Assert.Contains($"HandsGate.Acted(HandsTools.{ToolConstant(tool)}", body);
        // Отказ по замку машины — ответ этого вызова: действие не идёт, ход продолжается
        Assert.Matches(@"if \(HandsGate\.Acted\(HandsTools\.\w+(, \w+)?\) is \{ \} busy\)\s*\{\s*return busy;", body);
        Assert.True(gateAt >= 0 && actedAt > denyAt, $"{tool}: сигнал раньше отказа гейта");
        Assert.True(firstAwait < 0 || actedAt < firstAwait, $"{tool}: сигнал после действия");
    }

    [Fact]
    public void Window_and_screenshot_report_their_action_name()
    {
        var windows = Read("Tools/WindowManagementTool.cs");
        Assert.Contains("HandsPolicy.CheckWindowAction(windowAction);", windows);
        Assert.Contains("HandsGate.Acted(HandsTools.WindowManagement, windowAction)", windows);
        Assert.Contains("HandsGate.Acted(HandsTools.ScreenshotControl, action)", Read("Tools/ScreenshotControlTool.cs"));
    }

    [Fact]
    public void Bridge_passes_the_turn_events_to_the_gate() =>
        Assert.Contains("HandsGate.Configure(browserProfile, GetOption(args, HandsBridgeArgs.ActivityEvent),\n" +
            "    GetOption(args, HandsBridgeArgs.BusyEvent), GetOption(args, HandsBridgeArgs.TurnEndEvent));",
            Read("Program.cs").Replace("\r\n", "\n"));

    private static string ToolConstant(string tool) =>
        typeof(HandsTools).GetFields().Single(f => f.IsLiteral && (string?)f.GetRawConstantValue() == tool).Name;

    /// <summary>
    /// Граница «только свои окна» снята (ADR-016 §7): список, поиск (в том числе по заголовку) и
    /// окно переднего плана отдают все окна, а не фильтруются, и поиск окна после запуска — тоже.
    /// </summary>
    [Fact]
    public void List_find_and_foreground_return_every_window()
    {
        var windows = Read("Tools/WindowManagementTool.cs");
        var app = Read("Tools/AppTool.cs");

        foreach (var source in new[] { windows, app })
        {
            Assert.DoesNotContain("FilterOwn", source);
            Assert.DoesNotContain("IsOwnWindow", source);
            Assert.DoesNotContain("CheckForegroundResult", source);
        }
        Assert.Contains("HandleWaitForAsync(title,", windows);
    }

    [Fact]
    public void App_launches_without_shell_into_the_job_and_never_by_title()
    {
        var source = Read("Tools/AppTool.cs");

        Assert.Contains("UseShellExecute = false", source);
        Assert.DoesNotContain("UseShellExecute = true", source);
        Assert.Contains("AssignToAppsJob(process)", source);
        Assert.Contains("FileName = programPath", source);
        Assert.Contains("HandleLaunchAsync(gate.ProgramPath,", source);
        Assert.DoesNotContain("Title?.Contains", source);
        Assert.DoesNotContain("w.ProcessName, process.ProcessName", source);
    }

    [Fact]
    public void Keyboard_checks_system_combinations_on_every_press()
    {
        var source = Read("Input/KeyboardInputService.cs");

        Assert.Contains("HandsPolicy.CheckKeys(", source);
        Assert.Contains("HandsPolicy.CheckKeyDown(", source);
    }

    /// <summary>
    /// Руки без UIA — мышь (D2b-1): инструмент зовёт только ВНУТРЕННИЕ перегрузки
    /// <c>MouseInputService</c>, которые принимают целевое окно (<c>nint</c>) — иначе повторная
    /// проверка точки перед <c>SendInput</c> (гонка «гейт → ввод») не сработает. Публичные
    /// перегрузки окна не принимают и проверки не делают. Разбор синтаксический (Roslyn):
    /// каждый вызов члена <c>MouseInputService</c> обязан передавать окно (не null/default/IntPtr.Zero)
    /// и иметь достаточно аргументов для внутренней перегрузки.
    /// </summary>
    [Fact]
    public void Mouse_tool_calls_only_the_guarded_overloads()
    {
        var root = ParseTree(Read("Tools/MouseControlTool.cs"));

        // Внутренние перегрузки, которые инструмент вправе вызывать (мин. число аргументов)
        var internalMinArgs = new Dictionary<string, int>
        {
            ["MoveAsync"] = 4,
            ["ClickAsync"] = 5,
            ["DoubleClickAsync"] = 5,
            ["RightClickAsync"] = 5,
            ["MiddleClickAsync"] = 4,
            ["DragAsync"] = 7,   // startX, startY, endX, endY, button, окно, токен
            ["ScrollAsync"] = 6, // direction, amount, x, y, окно, токен
        };
        // Polyline не в схеме: StrokeAsync инструмент не зовёт
        var forbidden = new[] { "StrokeAsync" };

        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsMouseInputServiceCall)
            .ToList();

        Assert.NotEmpty(calls);

        foreach (var call in calls)
        {
            var member = (MemberAccessExpressionSyntax)call.Expression;
            var name = member.Name.Identifier.Text;

            Assert.Contains(name, internalMinArgs);
            Assert.DoesNotContain(name, forbidden);

            var argCount = call.ArgumentList.Arguments.Count;
            Assert.True(argCount >= internalMinArgs[name],
                $"{name}: {argCount} аргументов — меньше {internalMinArgs[name]} внутренней перегрузки (публичная без окна?)");

            // Последний аргумент перед CancellationToken — окно: не null/default/IntPtr.Zero
            var windowArg = call.ArgumentList.Arguments[^2].Expression;
            Assert.False(IsNullOrDefault(windowArg),
                $"{name}: аргумент окна = «{windowArg}» (должен быть не null/default/IntPtr.Zero)");
        }
    }

    /// <summary>
    /// Перед каждым <c>SendInput</c> в перегрузках, которые использует инструмент, стоит повторная
    /// проверка точки: окно ПОД ТЕКУЩЕЙ ПОЗИЦИЕЙ КУРСОРА принимает ввод и совпадает с целевым.
    /// Закрывает гонку между проверкой гейта в инструменте и самим вводом.
    /// Разбор синтаксический (Roslyn): в каждом проверяемом методе вызов <c>CursorPointAcceptsInput</c>
    /// стоит ДО первого <c>SendInput</c> и вложен в <c>if</c> с ранним <c>return</c>.
    /// Любой НОВЫЙ метод с <c>SendInput</c> обязан попасть в список проверяемых или в явные исключения.
    /// </summary>
    [Fact]
    public void Mouse_input_rechecks_the_point_before_every_send_input()
    {
        var root = ParseTree(Read("Input/MouseInputService.cs"));

        // Методы, которые использует инструмент: обязаны иметь проверку точки перед SendInput
        var checkedMethods = new[]
        {
            "MoveCoreAsync", "ClickCoreAsync", "DoubleClickCoreAsync", "RightClickCoreAsync", "MiddleClickCoreAsync",
            "StrokeCoreAsync", "ScrollCoreAsync", // drag (через StrokeCore) и scroll — D2b-2
        };
        // Методы с SendInput вне инструмента — явные исключения (сейчас таких нет)
        var excludedMethods = Array.Empty<string>();

        var allMethodsWithSendInput = new List<string>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var body = method.Body;
            if (body is null) continue;

            if (!body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsSendInputCall))
                continue;

            allMethodsWithSendInput.Add(method.Identifier.Text);

            if (!checkedMethods.Contains(method.Identifier.Text) && !excludedMethods.Contains(method.Identifier.Text))
            {
                Assert.Fail($"Метод {method.Identifier.Text} содержит SendInput, но не в списке проверяемых и не в исключениях");
            }

            if (!checkedMethods.Contains(method.Identifier.Text))
                continue;

            // Вызовы CursorPointAcceptsInput в теле метода
            var pointChecks = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsPointCheckCall)
                .ToList();
            Assert.NotEmpty(pointChecks);

            // Вызовы SendInput в теле метода
            var sendInputs = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsSendInputCall)
                .ToList();
            Assert.NotEmpty(sendInputs);

            // Первая проверка точки стоит ДО первого SendInput. Проверок может быть несколько:
            // перетаскивание проверяет и начало (перед нажатием), и конец (перед отпусканием)
            var firstSendInput = sendInputs.MinBy(inv => inv.SpanStart)!;
            var firstPointCheck = pointChecks.MinBy(inv => inv.SpanStart)!;
            Assert.True(firstPointCheck.SpanStart < firstSendInput.SpanStart,
                $"{method.Identifier.Text}: проверка точки (span {firstPointCheck.SpanStart}) после первого SendInput (span {firstSendInput.SpanStart})");

            // Каждый вызов проверки точки вложен в if с ранним выходом (return или goto)
            foreach (var check in pointChecks)
            {
                var ifStmt = FindEnclosingIfWithReturn(check);
                Assert.NotNull(ifStmt);

                // Условие — ровно «!CursorPointAcceptsInput(...)»: ни «false && …», ни «|| …» проверку не отключают
                Assert.True(
                    ifStmt!.Condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                    && not.Operand == check,
                    $"{method.Identifier.Text}: условие проверки точки «{ifStmt.Condition}» не равно «!CursorPointAcceptsInput(...)»");
            }
        }

        // Множество методов с SendInput == проверяемые + исключения
        var expected = checkedMethods.Concat(excludedMethods).ToHashSet(StringComparer.Ordinal);
        var actual = allMethodsWithSendInput.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Перетаскивание (D2b-2): кнопка мыши обязана отпускаться в <c>finally</c>, а конец перетаскивания —
    /// проверяться ПОСЛЕ движения к нему и до отпускания (иначе drop упадёт в терминал). Разбор Roslyn:
    /// в <c>StrokeCoreAsync</c> есть <c>try</c> с <c>finally</c>, где стоит <c>SendInput</c> (кнопка вверх),
    /// и есть проверка точки, лежащая после цикла движения по точкам.
    /// </summary>
    [Fact]
    public void Drag_releases_the_button_in_finally_and_rechecks_the_end_point()
    {
        var root = ParseTree(Read("Input/MouseInputService.cs"));
        var stroke = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "StrokeCoreAsync");

        var finallyBlock = stroke.DescendantNodes().OfType<TryStatementSyntax>()
            .Select(t => t.Finally?.Block)
            .FirstOrDefault(b => b is not null && b.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsSendInputCall));
        Assert.NotNull(finallyBlock);

        // Цикл движения по точкам при зажатой кнопке — тот, что зовёт MoveForStrokeAsync (первый for только проверяет границы)
        var loop = stroke.DescendantNodes().OfType<ForStatementSyntax>()
            .Single(f => f.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "MoveForStrokeAsync"));
        var checks = stroke.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsPointCheckCall).ToList();
        Assert.True(checks.Count(c => c.SpanStart > loop.Span.End) >= 1,
            "StrokeCoreAsync: нет проверки конечной точки после цикла движения (перед отпусканием кнопки)");
        Assert.True(checks.Count(c => c.SpanStart < loop.SpanStart) >= 1,
            "StrokeCoreAsync: нет проверки начальной точки до цикла движения");
    }

    /// <summary>
    /// Drag (D2b-2.1): конец перетаскивания над запрещённым окном → курсор возвращён в начало, и
    /// результат возврата проверяется вызовом <c>FinishDragAfterEndRejected</c>. Он обязан стоять ПОСЛЕ
    /// возврата <c>MoveForStrokeAsync(points[0].…)</c> и ДО <c>finally</c>, где отпускается кнопка, —
    /// иначе кнопка отпустится над запрещённым окном (drop). Условие перед вызовом — повторная проверка
    /// текущей позиции курсора (<c>CursorPointAcceptsInput</c>). Разбор Roslyn: порядок по позиции в исходнике.
    /// </summary>
    [Fact]
    public void StrokeCoreAsync_finishes_the_drag_after_returning_to_start()
    {
        var root = ParseTree(Read("Input/MouseInputService.cs"));
        var stroke = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "StrokeCoreAsync");

        // Возврат курсора в начальную точку: MoveForStrokeAsync(points[0].X, points[0].Y, …)
        var returnToStart = stroke.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsMoveForStrokeCall)
            .Where(c => c.ArgumentList.Arguments.Count >= 1
                && c.ArgumentList.Arguments[0].ToString().Contains("points[0]"))
            .Single();

        // Вызов FinishDragAfterEndRejected — решение о завершении drag после возврата
        var finishCalls = stroke.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression is IdentifierNameSyntax { Identifier.Text: "FinishDragAfterEndRejected" })
            .ToList();
        Assert.True(finishCalls.Count > 0, "StrokeCoreAsync: нет вызова FinishDragAfterEndRejected после возврата курсора");
        var finish = finishCalls.Single();

        // finally, где отпускается кнопка (SendInput)
        var finallyBlock = stroke.DescendantNodes().OfType<TryStatementSyntax>()
            .Select(t => t.Finally?.Block)
            .FirstOrDefault(b => b is not null && b.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsSendInputCall));
        Assert.True(finallyBlock is not null, "StrokeCoreAsync: нет finally с отпусканием кнопки (SendInput)");

        // Порядок: возврат в начало → FinishDragAfterEndRejected → finally
        Assert.True(finish.SpanStart > returnToStart.Span.End,
            "FinishDragAfterEndRejected стоит до возврата курсора в начальную точку");
        Assert.True(finish.SpanStart < finallyBlock!.SpanStart,
            "FinishDragAfterEndRejected стоит после finally — кнопка уже отпущена");

        // Условие перед вызовом — повторная проверка текущей позиции курсора
        var guard = finish.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.True(guard is not null, "FinishDragAfterEndRejected не вложен в if с проверкой");
        Assert.True(guard!.Condition.ToString().Contains("CursorPointAcceptsInput"),
            "условие перед FinishDragAfterEndRejected не проверяет позицию курсора");
    }

    /// <summary>
    /// Инструмент кладёт в гейт ОБЕ точки перетаскивания: конец drag тоже проверяется на запрещённое окно.
    /// </summary>
    [Fact]
    public void Mouse_tool_passes_the_drag_end_point_to_the_gate()
    {
        var root = ParseTree(Read("Tools/MouseControlTool.cs"));
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

        // Разбор Roslyn, а не текста: закомментированная строка вызовом не считается
        Assert.Contains(calls, c =>
            c.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add", Expression: IdentifierNameSyntax { Identifier.Text: "pointList" } }
            && c.ArgumentList.ToString().Contains("toX") && c.ArgumentList.ToString().Contains("toY"));

        var gateCall = calls.Single(c => c.Expression.ToString() == "HandsGate.Policy.CheckPointer");
        Assert.Equal("pointList", gateCall.ArgumentList.Arguments[1].ToString());
        Assert.Equal("wheelAmount", gateCall.ArgumentList.Arguments[3].ToString());
    }

    [Fact]
    public void Screen_pixels_are_never_copied_for_a_window()
    {
        Assert.DoesNotContain("CopyFromScreen", Read("Automation/Tools/UIReadTool.cs"));
        Assert.DoesNotContain(
            "var region = new CaptureRegion(windowRect.Left, windowRect.Top, width, height);",
            Read("Capture/ScreenshotService.cs"));
    }

    [Fact]
    public void Removed_tool_classes_stay_removed()
    {
        string[] removed =
        [
            "Tools/KeyboardControlTool.cs", "Automation/Tools/UIBatchTool.cs",
            "Automation/Tools/UIFileTool.cs", "Automation/Tools/UIOpenFileTool.cs", "Automation/Tools/UIReadTableTool.cs",
            "Automation/Tools/UISelectTool.cs", "Automation/Tools/UIWaitTool.cs", "Clipboard/Tools", "Macros/Tools", "Processes/Tools",
        ];

        foreach (var path in removed)
        {
            var full = Path.Combine(BridgeDir, path);
            Assert.False(File.Exists(full) || Directory.Exists(full), path);
        }
    }

    /// <summary>Тело метода с атрибутом <c>[McpServerTool(Name = tool)]</c> — до следующего члена класса.</summary>
    private static string ToolBody(string file, string tool)
    {
        var source = Read(file);
        var start = source.IndexOf($"[McpServerTool(Name = \"{tool}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file}: нет инструмента {tool}");

        var open = source.IndexOf('{', source.IndexOf("CancellationToken cancellationToken)", start, StringComparison.Ordinal));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"{file}: тело {tool} не закрыто");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(BridgeDir, relative));

    /// <summary>
    /// Самопроверка: <see cref="BridgeDir"/> лежит в том же дереве, что и тестовая сборка.
    /// Без этого мутации в worktree дают ложные результаты (Глеб, D2b-1).
    /// </summary>
    [Fact]
    public void Bridge_dir_is_in_the_same_tree_as_the_test_assembly()
    {
        var bridge = Path.GetFullPath(BridgeDir);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (bridge.StartsWith(dir.FullName + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return;
        }
        Assert.Fail($"BridgeDir «{bridge}» не в дереве сборки «{AppContext.BaseDirectory}»");
    }

    private static string FindBridgeDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "HandsBridge");
            if (File.Exists(Path.Combine(candidate, "HandsBridge.csproj")))
                return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "HandsBridge", "HandsBridge.csproj")))
                return Path.Combine(dir.FullName, "HandsBridge");
        }

        throw new InvalidOperationException("Не найден каталог backend/HandsBridge выше " + AppContext.BaseDirectory);
    }

    // ─── Roslyn-хелперы ───────────────────────────────────────────────────────────────────────

    private static CompilationUnitSyntax ParseTree(string source) =>
        CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    /// <summary>Вызов <c>NativeMethods.SendInput(...)</c> — любой метод с именем SendInput через MemberAccess.</summary>
    private static bool IsSendInputCall(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SendInput" };

    /// <summary>Вызов <c>MoveForStrokeAsync(...)</c> — движение/возврат курсора в рамках stroke, без квалификатора.</summary>
    private static bool IsMoveForStrokeCall(InvocationExpressionSyntax inv) =>
        inv.Expression is IdentifierNameSyntax { Identifier.Text: "MoveForStrokeAsync" };

    /// <summary>Вызов <c>CursorPointAcceptsInput(...)</c> — метод того же класса, без квалификатора.</summary>
    private static bool IsPointCheckCall(InvocationExpressionSyntax inv) =>
        inv.Expression is IdentifierNameSyntax { Identifier.Text: "CursorPointAcceptsInput" };

    /// <summary>
    /// Вызов члена <c>MouseInputService</c>: <c>WindowsToolsBase.MouseInputService.XxxAsync(...)</c>.
    /// Локальные алиасы (var mouse = WindowsToolsBase.MouseInputService) пока не покрываются —
    /// в текущем коде их нет.
    /// </summary>
    private static bool IsMouseInputServiceCall(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax member
        && member.Expression is MemberAccessExpressionSyntax outer
        && outer.Name is IdentifierNameSyntax { Identifier.Text: "MouseInputService" };

    /// <summary>
    /// Выражение-«пустота»: <c>null</c>, <c>default</c>, <c>IntPtr.Zero</c>, <c>nint.Zero</c>.
    /// </summary>
    private static bool IsNullOrDefault(ExpressionSyntax expr)
    {
        if (expr is LiteralExpressionSyntax { Token.Text: "null" }) return true;
        if (expr is LiteralExpressionSyntax { Token.Text: "default" }) return true;
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" }) return true;
        return false;
    }

    /// <summary>
    /// Ближайший предок-<c>IfStatement</c>, чьё условие содержит <paramref name="node"/>
    /// и чьё тело содержит ранний выход: <c>return</c> или <c>goto</c> (перетаскивание уходит на метку
    /// <c>Finish</c>, чтобы finally отпустил кнопку и результат не потерялся).
    /// </summary>
    private static IfStatementSyntax? FindEnclosingIfWithReturn(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is not IfStatementSyntax ifStmt) continue;
            if (!ifStmt.Condition.DescendantNodesAndSelf().Contains(node)) continue;
            if (ifStmt.Statement.DescendantNodes().Any(n => n is ReturnStatementSyntax or GotoStatementSyntax))
                return ifStmt;
        }
        return null;
    }
}
