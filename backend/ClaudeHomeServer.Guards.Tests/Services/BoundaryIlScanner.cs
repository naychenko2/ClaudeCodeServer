using System.Reflection;
using System.Reflection.Emit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Общий скан для сторожей границ вертикалей: IL тел методов плюс метаданные типа
/// (поля, сигнатуры, базовый тип, интерфейсы, события, атрибуты — см.
/// <see cref="TypesFromSignatures"/>).
/// Перенесён из разведочного <c>IlBoundaryScanProbe.cs</c> (задача <c>cd6dd658</c>),
/// включён в постоянные сторожа <see cref="SubsystemBoundaryTests"/> и
/// <see cref="RootSubsystemBoundaryTests"/> (задача <c>8beee75e</c>, волна 1).
///
/// Сканирует IL-опкоды методов (call/callvirt/ldsfld/ldfld/ldtoken/newobj/castclass/
/// isinst/box/newarr/initobj), generic-аргументы инстанцированных методов
/// (включая <c>sp.GetRequiredService&lt;T&gt;()</c>), типы локальных переменных и
/// enum-аргументы, переданные константой (<c>kind: PendingKind.Report</c> — по
/// позиции аргумента в сигнатуре вызова, см. <see cref="WalkBody"/>).
/// Из типов извлекаются declaring-типы вызванных методов/полей, операнды и
/// generic-аргументы. Без зависимостей: <c>MethodBody.GetILAsByteArray()</c> +
/// ручной обход опкодов + <c>Module.ResolveMember/ResolveType</c> — всё из BCL.
///
/// Важно: обход ВСЕХ методов всех типов дерева namespace. Вложенные типы
/// (async-state-машины <c>Foo+&lt;BarAsync&gt;d__12</c>, замыкания
/// <c>Foo+&lt;&gt;c__DisplayClass3_0</c>) попадают в выборку автоматически,
/// потому что у них тот же <c>Namespace</c>, что у родителя. Без обхода nested-типов
/// сторож видит 4 из 7 известных швов и выглядит рабочим — см. разведку
/// `docs/research/il-boundary-scan-2026-09.md`, раздел про слепые пятна CLAUDE.md.
/// </summary>
internal static class BoundaryIlScanner
{
    private static readonly OpCode[] SingleByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    // Опкоды, кладущие на стек КОНСТАНТУ: ldc.* и ldstr. Нужны для атрибуции
    // enum-констант: обращение к константе enum компилируется в ldc.i4 без
    // метадата-токена (проверено дампом IL, задача fada2814), и единственный её
    // след в сборке — позиция аргумента в сигнатуре вызываемого метода.
    private static readonly bool[] ConstPushSingle = new bool[256];
    private static readonly bool[] ConstPushTwo = new bool[256];

    static BoundaryIlScanner()
    {
        // Files — отдельная сборка (ADR-016, задача 4.1): сканер зовут все сторожа границ,
        // форс-загрузка здесь страхует их от вакуумного прохода по вертикали Files.
        _ = typeof(ClaudeHomeServer.Services.Files.FileService).Assembly;

        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            // Имя именно op.Name (мнемоника IL: «ldc.i4.2»), а не f.Name: поля OpCodes
            // названы Ldc_I4_2 / Ldstr — по имени поля таблица осталась бы пустой.
            var name = op.Name;
            var constPush = name is not null
                && (name.StartsWith("ldc.", StringComparison.Ordinal) || name == "ldstr");
            if (op.Size == 1) { SingleByte[v & 0xFF] = op; ConstPushSingle[v & 0xFF] = constPush; }
            else { TwoByte[v & 0xFF] = op; ConstPushTwo[v & 0xFF] = constPush; }
        }
    }

    /// <summary>Все типы, «упомянутые» в теле метода: declaring-типы вызванных методов
    /// и полей, операнды ldtoken/newobj/castclass/isinst/box/newarr/initobj,
    /// generic-аргументы (в т.ч. <c>GetRequiredService&lt;T&gt;</c>), типы локальных
    /// переменных и enum-типы аргументов, переданных константой
    /// (<c>kind: PendingKind.Report</c>) — см. <see cref="WalkBody"/>.</summary>
    public static IEnumerable<Type> TypesFromBody(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { yield break; }
        if (body is null) yield break;

        foreach (var local in body.LocalVariables)
            foreach (var t in Expand(local.LocalType)) yield return t;

        var module = method.Module;
        var (typeArgs, methodArgs) = GenericContext(method);
        foreach (var use in WalkBody(body, method, typeArgs, methodArgs))
            foreach (var t in ResolveUse(module, use, typeArgs, methodArgs))
                yield return t;
    }

    /// <summary>Методы, вызванные (call/callvirt/newobj/ldftn…) из тела метода — для
    /// сторожей, которым важен конкретный вызов, а не тип (G8: <c>ILauncherFactory.ForOwner</c>).</summary>
    public static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { yield break; }
        if (body is null) yield break;

        var (typeArgs, methodArgs) = GenericContext(method);
        foreach (var use in WalkBody(body, method, typeArgs, methodArgs))
            if (use.Member is MethodBase called) yield return called;
    }

    private static (Type[]? TypeArgs, Type[]? MethodArgs) GenericContext(MethodBase method)
    {
        Type[]? typeArgs = null, methodArgs = null;
        try { typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null; } catch { }
        try { methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null; } catch { }
        return (typeArgs, methodArgs);
    }

    /// <summary>Что дал один опкод тела метода: метадата-токен (0 — его нет), уже
    /// разрешённый callee для method-опкодов (чтобы токен не разрешался второй раз)
    /// и enum-типы тех параметров вызова, на которые положена константа.</summary>
    private readonly struct IlUse
    {
        public readonly int Token;
        public readonly MemberInfo? Member;
        public readonly Type[] ConstArgs;
        public IlUse(int token, MemberInfo? member, Type[]? constArgs)
        {
            Token = token;
            Member = member;
            ConstArgs = constArgs ?? Array.Empty<Type>();
        }
    }

    // Что опкод кладёт на вершину стека значений: ничего / константу (ldc.*, ldstr) /
    // значение из переменного источника / «модель не знает».
    private const int PushNone = -1;
    private const int PushUnknown = -2;
    private const int PushConst = 1;
    private const int PushValue = 2;

    /// <summary>
    /// Один обход IL тела метода: метаданные-операнды (field/method/type/tok) по порядку
    /// опкодов — как раньше — плюс атрибуция enum-констант.
    ///
    /// Зачем вторая часть: обращение к константе enum не оставляет в IL метадата-токена
    /// вовсе — оно компилируется в <c>ldc.i4 &lt;число&gt;</c> (проверено дампом тел,
    /// задача <c>fada2814</c>), и ни <c>ResolveMember</c>, ни <c>ResolveType</c> его не
    /// видят. Единственный след такого обращения — позиция аргумента: если значение для
    /// параметра <c>enum</c>-типы положено <c>ldc.*</c>, это и есть константа этого enum.
    /// Поэтому обход ведёт стек значений: длину и происхождение каждого значения
    /// (константа/нет) по собственным метаданным опкодов (<c>StackBehaviourPop</c> /
    /// <c>StackBehaviourPush</c>) и по сигнатуре разрешённого callee для
    /// <c>call/callvirt/newobj</c> (там же «this» экземплярного вызова).
    ///
    /// Консервативность важнее полноты: после опкода, который модель не умеет
    /// (<c>Varpop</c> вне разобранного вызова, опкод не найден в таблице), позиции
    /// аргументов дальше по телу не считаются вовсе — чтобы не придумывать ребро,
    /// которого нет. Значение, происхождение которого непонятно, считается НЕ
    /// константой: лишний допуск сторожу дороже пропущенного раза. Так же отброшен
    /// подставленный компилятором <c>default</c> optional-параметра: он выглядит в IL
    /// идентично явной константе, но в исходниках обращения к типу нет (см.
    /// <see cref="ConstEnumArgs"/>).
    ///
    /// ИЗВЕСТНЫЕ ПРЕДЕЛЫ (закреплены тестами в <c>IlBoundaryRegressionTests</c>; расширять
    /// видимость — отдельное решение, а не «заодно»; принципиально закрывается только
    /// гейтом по исходникам, он вне объёма сканера):
    ///
    /// 1. НЕВИДИМО: константа enum, не попавшая ни в позицию аргумента enum-параметра,
    /// ни в сигнатуру/поле/локальную переменную того же типа. Чистый cast с сравнением
    /// <c>(PendingKind)i == PendingKind.Report</c> (значение из <c>int</c>, локальной
    /// переменной этого типа нет, сигнатура метода тип не называет) компилируется в
    /// <c>ldarg; ldc.i4; ceq</c> и не оставляет в сборке ни токена типа, ни локальной.
    /// (<c>switch</c> по cast-значению проверен: компилятор заводит временную локальную
    /// этого типа, и такая форма видна.) Видимы лишь формы,
    /// где тип всплывает отдельно: параметр/возврат метода (<see cref="TypesFromSignatures"/>),
    /// поле, локальная переменная этого типа (<c>LocalVariables</c>) либо константа
    /// в позиции enum-параметра вызова (этот обход). Утверждение «сравнение и cast видны по
    /// сигнатуре или локальной» верно ТОЛЬКО когда такая сигнатура/локальная есть.
    ///
    /// 2. ЛОЖНОЕ РЕБРО: <c>ldc.i4 0</c> неотличим от «названного enum». C# неявно приводит
    /// литерал <c>0</c> (и <c>const int</c> = 0) к любому enum, поэтому <c>Sink.Take(x, 0)</c>
    /// даёт тот же IL, что <c>Sink.Take(x, Kind.Agent)</c>, и сканер приписывает
    /// тип параметра, хотя в исходниках имени типа нет. Отделить это от техники нельзя;
    /// сегодня таких вызовов в кодовой базе нет, при появлении потребуется лишняя строка
    /// allow-list.
    ///
    /// 3. НЕВИДИМО (сознательный компромисс): явная константа, равная объявленному
    /// default optional-параметра (<c>Opt(x, Kind.Agent)</c> при <c>kind = Kind.Agent</c>).
    /// Подстановку default и явный аргумент компилятор печёт идентичным <c>ldc</c> на
    /// том же месте стека, и ни опкод, ни позиция их не различают, а без отсечения default каждый вызов без аргумента
    /// стал бы допуском в allow-list. Цена — пропуск явной записи, совпавшей с default.
    /// </summary>
    private static IEnumerable<IlUse> WalkBody(MethodBody body, MethodBase method, Type[]? typeArgs, Type[]? methodArgs)
    {
        byte[]? il;
        try { il = body.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

        var module = method.Module;
        var returnsValue = method is not MethodInfo self || self.ReturnType != typeof(void);
        var stack = new List<(int Kind, long Value)>();
        var positionsUnknown = false;

        var pos = 0;
        while (pos < il.Length)
        {
            OpCode op;
            var b = il[pos++];
            if (b == 0xFE)
            {
                if (pos >= il.Length) break;
                op = TwoByte[il[pos++]];
            }
            else op = SingleByte[b];

            int token = 0;
            long operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.ShortInlineI:
                    if (pos >= il.Length) { pos = il.Length; break; }
                    operand = (sbyte)il[pos]; pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineBrTarget: pos += 4; break;
                case OperandType.InlineI:
                    if (pos + 4 > il.Length) { pos = il.Length; break; }
                    operand = BitConverter.ToInt32(il, pos); pos += 4; break;
                case OperandType.ShortInlineR: pos += 4; break;
                case OperandType.InlineI8:
                    if (pos + 8 > il.Length) { pos = il.Length; break; }
                    operand = BitConverter.ToInt64(il, pos); pos += 8; break;
                case OperandType.InlineR: pos += 8; break;
                case OperandType.InlineSwitch:
                    {
                        if (pos + 4 > il.Length) { pos = il.Length; break; }
                        var n = BitConverter.ToInt32(il, pos);
                        pos += 4 + 4 * n;
                        break;
                    }
                case OperandType.InlineString:
                case OperandType.InlineSig: pos += 4; break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    {
                        if (pos + 4 > il.Length) { pos = il.Length; break; }
                        token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        break;
                    }
                default: pos = il.Length; break;
            }

            MemberInfo? member = null;
            if (token != 0 && op.OperandType == OperandType.InlineMethod)
            {
                try { member = module.ResolveMethod(token, typeArgs, methodArgs); } catch { }
            }

            // Значение константы нужно, чтобы отличить явную константу от подставленного
            // компилятором значения по умолчанию (см. ConstEnumArgs).
            long constValue = 0;
            if (IsConstPush(op))
                constValue = op.OperandType switch
                {
                    OperandType.ShortInlineI or OperandType.InlineI or OperandType.InlineI8 => operand,
                    // ldc.i4.m1 (0x15) … ldc.i4.8 (0x1E): значение зашито в самом опкоде
                    // и идёт подряд от 0x15 → −1.
                    OperandType.InlineNone => op.Value - 0x16,
                    _ => 0,
                };

            Type[]? constArgs = null;
            // Признак «опкода нет в таблице» — Size: у реального опкода он 1 или 2,
            // у default(OpCode) — 0. По Value судить нельзя: nop — это op.Value == 0,
            // а nop компилятор ставит в каждом теле.
            if (op.Size == 0) positionsUnknown = true;
            else if (!positionsUnknown)
            {
                var (pops, pushed, value) = StackEffect(op, member, stack, returnsValue, constValue);
                if (pops < 0 || pushed == PushUnknown) positionsUnknown = true;
                else
                {
                    if (member is MethodBase callee &&
                        (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
                        constArgs = ConstEnumArgs(callee, stack);
                    ApplyStack(stack, pops, pushed, value);
                }
            }

            if (token != 0 || constArgs is { Length: > 0 })
                yield return new IlUse(token, member, constArgs);
        }
    }

    /// <summary>Стек значений для вызова: если значение, положенное на параметр
    /// <c>enum</c>-типы, — константа, это обращение к её типу. Позиции берутся сверху
    /// стека по числу параметров (сигнатурный порядок аргументов — порядок кладки),
    /// «this» экземпляра над параметрами не стоит. Если значений меньше, чем параметров,
    /// позиции не считаются вовсе: какой аргумент опущен — из IL не видно.</summary>
    private static Type[]? ConstEnumArgs(MethodBase callee, List<(int Kind, long Value)> stack)
    {
        var ps = callee.GetParameters();
        if (ps.Length == 0 || stack.Count < ps.Length) return null;

        List<Type>? found = null;
        var base_ = stack.Count - ps.Length;
        for (var i = 0; i < ps.Length; i++)
        {
            var slot = stack[base_ + i];
            var pt = ps[i].ParameterType;
            if (!pt.IsEnum || slot.Kind != PushConst) continue;
            // Значение по умолчанию optional-параметра компилятор печёт той же константой
            // на месте вызова (ldc.i4 0 для PendingKind.Agent): в исходниках такого
            // обращения нет, и ребром оно не является. Отличаем по совпадению с
            // объявленным default — иначе каждая ленивая запись о необязательном
            // параметре стала бы допуском в allow-list.
            if (IsInlinedDefault(ps[i], slot.Value)) continue;
            (found ??= new List<Type>()).Add(pt);
        }
        return found?.ToArray();
    }

    private static bool IsInlinedDefault(ParameterInfo p, long constValue)
    {
        if (!p.IsOptional || !p.HasDefaultValue) return false;
        try { return p.DefaultValue is { } d && Convert.ToInt64(d) == constValue; }
        catch { return true; } // сравнить не смогли → не выдумываем ребро
    }

    private static (int Pops, int Pushed, long Value) StackEffect(
        OpCode op, MemberInfo? member, List<(int Kind, long Value)> stack, bool returnsValue, long constValue)
    {
        // Явные случаи, где метаданных опкода мало или они трактуются двусмысленно.
        if (op == OpCodes.Ret) return (returnsValue ? 1 : 0, PushNone, 0);
        if (op == OpCodes.Switch) return (1, PushNone, 0); // switch снимает переключаемое значение
        if (op.Name is "leave" or "leave.s") return (int.MaxValue, PushNone, 0);
        if (op == OpCodes.Dup) return (0, stack.Count > 0 ? stack[^1].Kind : PushValue, stack.Count > 0 ? stack[^1].Value : 0);
        if (IsConstPush(op)) return (0, PushConst, constValue);

        // Вызов: арность и «this» — из сигнатуры разрешённого callee.
        if (member is MethodBase callee && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
        {
            var args = callee.GetParameters().Length;
            var thisSlot = op == OpCodes.Callvirt || (op == OpCodes.Call && !callee.IsStatic) ? 1 : 0;
            var returns = op == OpCodes.Newobj
                || (callee is MethodInfo rm && rm.ReturnType != typeof(void));
            return (args + thisSlot, returns ? PushValue : PushNone, 0);
        }

        var pops = PopCount(op.StackBehaviourPop);
        return pops < 0 ? (-1, PushUnknown, 0) : (pops, PushOf(op), 0);
    }

    private static int PushOf(OpCode op)
    {
        var b = op.StackBehaviourPush;
        return b switch
        {
            StackBehaviour.Push0 => PushNone,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
                or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => PushValue,
            _ => PushUnknown, // Push1_push1 (кроме разобранного dup), Varpush (разобраны вызовы)
        };
    }

    // Сколько значений снимает опкод — по его же метаданным. Имена StackBehaviour вида
    // «Popi_pop1» описывают снятие по порядку (ближайшее к вершине первым), поэтому
    // число = количество сегментов. Varpop — только у вызовов (разобраны выше по
    // сигнатуре callee); всё прочее → -1, и обход перестаёт считать позиции.
    private static int PopCount(StackBehaviour b) => b switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popref_popi_pop1 or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            or StackBehaviour.Popi_popi_popi => 3,
        _ => -1,
    };

    private static bool IsConstPush(OpCode op)
    {
        var v = unchecked((ushort)op.Value);
        return op.Size == 1 ? ConstPushSingle[v & 0xFF] : ConstPushTwo[v & 0xFF];
    }

    private static void ApplyStack(List<(int Kind, long Value)> stack, int pops, int pushed, long value)
    {
        // «leave» сбрасывает стек до глубины входа в защищённый блок; глубину входа
        // обход не трекет, поэтому сброс до пустого — консервативно: лишнего значения
        // не останется, а нехватка приведёт к отказу от атрибуции, а не к ложному ребру.
        if (pops >= stack.Count) stack.Clear();
        else stack.RemoveRange(stack.Count - pops, pops);
        if (pushed == PushConst || pushed == PushValue) stack.Add((pushed, value));
    }

    private static IEnumerable<Type> ResolveUse(Module module, IlUse use, Type[]? typeArgs, Type[]? methodArgs)
    {
        // Атрибутированные enum-типы — уже типы, разворачивать нечего (enum не generic).
        foreach (var t in use.ConstArgs) yield return t;

        if (use.Token == 0) yield break;

        var member = use.Member;
        if (member is null)
        {
            try { member = module.ResolveMember(use.Token, typeArgs, methodArgs); }
            catch
            {
                try { member = module.ResolveType(use.Token, typeArgs, methodArgs); } catch { }
            }
        }
        if (member is null) yield break;

        if (member is Type type)
        {
            foreach (var t in Expand(type)) yield return t;
            yield break;
        }

        if (member.DeclaringType is { } owner)
            foreach (var t in Expand(owner)) yield return t;

        if (member is MethodInfo mi)
        {
            // Ключевой случай: sp.GetRequiredService<T>() — T живёт в generic-аргументах
            // инстанцированного метода, declaring-тип при этом чужой (Microsoft.*).
            if (mi.IsGenericMethod)
            {
                Type[] args;
                try { args = mi.GetGenericArguments(); } catch { yield break; }
                foreach (var a in args)
                    foreach (var t in Expand(a)) yield return t;
            }
        }
        else if (member is FieldInfo fi)
        {
            foreach (var t in Expand(fi.FieldType)) yield return t;
        }
    }

    private static IEnumerable<Type> Expand(Type? t)
    {
        if (t is null) yield break;
        if (t.IsByRef || t.HasElementType)
        {
            foreach (var x in Expand(t.GetElementType())) yield return x;
            if (!t.IsByRef) yield return t;
            yield break;
        }
        var nn = Nullable.GetUnderlyingType(t);
        if (nn is not null) { yield return nn; yield break; }
        yield return t;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                foreach (var x in Expand(a)) yield return x;
    }

    /// <summary>Все методы типа (включая nested-типы: async-state-машины и
    /// замыкания). Именно обход nested — то, что отличает IL-скан от простой
    /// рефлексии по публичным сигнатурам: <c>Foo+&lt;BarAsync&gt;d__12.MoveNext</c>
    /// содержит IL вызовов из тела <c>Foo.BarAsync</c>.</summary>
    public static IEnumerable<MethodBase> AllMethodsWithNested(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodBase[] a, b;
        try { a = t.GetMethods(F).Cast<MethodBase>().ToArray(); } catch { a = Array.Empty<MethodBase>(); }
        try { b = t.GetConstructors(F).Cast<MethodBase>().ToArray(); } catch { b = Array.Empty<MethodBase>(); }
        var methods = a.Concat(b).ToArray();
        Type[] nested;
        try { nested = t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
        catch { nested = Array.Empty<Type>(); }
        foreach (var n in nested)
            methods = methods.Concat(AllMethodsWithNested(n)).ToArray();
        return methods;
    }

    /// <summary>Типы, упомянутые в метаданных САМОГО типа (без тел методов и без
    /// nested): базовый тип и интерфейсы, поля всех видимостей (включая backing-поля
    /// свойств и событий), сигнатуры методов и конструкторов (возвращаемый тип,
    /// параметры), свойства и индексаторы, события, атрибуты на типе и его членах
    /// (тип атрибута и аргументы-<c>typeof</c>). Generic-аргументы разворачиваются
    /// рекурсивно (<c>List&lt;Dictionary&lt;string, Foo&gt;&gt;</c> даёт и <c>Foo</c>).
    /// Нужен потому, что поле или параметр конструктора, ни разу не тронутые в теле
    /// метода, в IL-операндах не появляются вовсе.</summary>
    public static IEnumerable<Type> TypesFromSignatures(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<Type>();
        void Add(Type? x) { foreach (var e in Expand(x)) found.Add(e); }
        void AddAttributes(Func<IList<CustomAttributeData>> get)
        {
            IList<CustomAttributeData> attrs;
            try { attrs = get(); } catch { return; }
            foreach (var a in attrs)
            {
                Add(a.AttributeType);
                foreach (var arg in a.ConstructorArguments) AddAttributeArgument(arg);
                foreach (var arg in a.NamedArguments) AddAttributeArgument(arg.TypedValue);
            }
        }
        void AddAttributeArgument(CustomAttributeTypedArgument arg)
        {
            if (arg.Value is Type typeArg) Add(typeArg);
            else if (arg.Value is IEnumerable<CustomAttributeTypedArgument> items)
                foreach (var item in items) AddAttributeArgument(item);
        }
        void AddParameters(MethodBase m)
        {
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); } catch { return; }
            foreach (var p in ps)
            {
                Add(p.ParameterType);
                AddAttributes(p.GetCustomAttributesData);
            }
        }

        try { Add(t.BaseType); } catch { }
        try { foreach (var i in t.GetInterfaces()) Add(i); } catch { }
        AddAttributes(t.GetCustomAttributesData);

        try
        {
            foreach (var f in t.GetFields(F))
            {
                Add(f.FieldType);
                AddAttributes(f.GetCustomAttributesData);
            }
        }
        catch { }

        try
        {
            foreach (var p in t.GetProperties(F))
            {
                Add(p.PropertyType);
                foreach (var ip in p.GetIndexParameters()) Add(ip.ParameterType);
                AddAttributes(p.GetCustomAttributesData);
            }
        }
        catch { }

        try
        {
            foreach (var e in t.GetEvents(F))
            {
                Add(e.EventHandlerType);
                AddAttributes(e.GetCustomAttributesData);
            }
        }
        catch { }

        try
        {
            foreach (var m in t.GetMethods(F))
            {
                Add(m.ReturnType);
                AddParameters(m);
                AddAttributes(m.GetCustomAttributesData);
            }
        }
        catch { }

        try
        {
            foreach (var c in t.GetConstructors(F))
            {
                AddParameters(c);
                AddAttributes(c.GetCustomAttributesData);
            }
        }
        catch { }

        return found;
    }

    /// <summary>Сам тип и все его nested-типы (рекурсивно).</summary>
    private static IEnumerable<Type> SelfAndNested(Type t)
    {
        yield return t;
        Type[] nested;
        try { nested = t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
        catch { yield break; }
        foreach (var n in nested)
            foreach (var x in SelfAndNested(n)) yield return x;
    }

    /// <summary>Единая точка сбора типов, упомянутых типом (включая nested-типы):
    /// IL тел всех методов (<see cref="TypesFromBody"/>) плюс метаданные — поля,
    /// сигнатуры, базовые типы, интерфейсы, события, атрибуты
    /// (<see cref="TypesFromSignatures"/>). Используется ОДНОВРЕМЕННО из Theory-сторожей
    /// (<see cref="SubsystemBoundaryTests"/>, <see cref="RootSubsystemBoundaryTests"/>)
    /// и из регрессии (<see cref="IlBoundaryRegressionTests"/>): если сбор здесь
    /// сломать (например, вернуть к <c>GetMethods(DeclaredOnly)</c> без nested или
    /// убрать скан сигнатур), регрессия покраснеет — гейт не декоративен.</summary>
    public static IEnumerable<Type> CollectAllReferencedTypes(Type t)
    {
        foreach (var method in AllMethodsWithNested(t))
            foreach (var referenced in TypesFromBody(method))
                yield return referenced;

        foreach (var type in SelfAndNested(t))
            foreach (var referenced in TypesFromSignatures(type))
                yield return referenced;
    }
}
