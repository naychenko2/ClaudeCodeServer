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
/// (включая <c>sp.GetRequiredService&lt;T&gt;()</c>) и типы локальных переменных.
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

    static BoundaryIlScanner()
    {
        // Files — отдельная сборка (ADR-016, задача 4.1): сканер зовут все сторожа границ,
        // форс-загрузка здесь страхует их от вакуумного прохода по вертикали Files.
        _ = typeof(ClaudeHomeServer.Services.Files.FileService).Assembly;

        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            if (op.Size == 1) SingleByte[v & 0xFF] = op;
            else TwoByte[v & 0xFF] = op;
        }
    }

    /// <summary>Все типы, «упомянутые» в теле метода: declaring-типы вызванных методов
    /// и полей, операнды ldtoken/newobj/castclass/isinst/box/newarr/initobj,
    /// generic-аргументы (в т.ч. <c>GetRequiredService&lt;T&gt;</c>),
    /// типы локальных переменных.</summary>
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
        foreach (var token in MetadataTokens(body))
            foreach (var t in ResolveToken(module, token, typeArgs, methodArgs))
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

        var module = method.Module;
        var (typeArgs, methodArgs) = GenericContext(method);
        foreach (var token in MetadataTokens(body))
        {
            MethodBase? called = null;
            try { called = module.ResolveMethod(token, typeArgs, methodArgs); } catch { }
            if (called is not null) yield return called;
        }
    }

    private static (Type[]? TypeArgs, Type[]? MethodArgs) GenericContext(MethodBase method)
    {
        Type[]? typeArgs = null, methodArgs = null;
        try { typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null; } catch { }
        try { methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null; } catch { }
        return (typeArgs, methodArgs);
    }

    // Метаданные-операнды (field/method/type/tok) тела метода по порядку опкодов
    private static IEnumerable<int> MetadataTokens(MethodBody body)
    {
        byte[]? il;
        try { il = body.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

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

            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR: pos += 4; break;
                case OperandType.InlineI8:
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
                        var token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        yield return token;
                        break;
                    }
                default: pos = il.Length; break;
            }
        }
    }

    private static IEnumerable<Type> ResolveToken(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        MemberInfo? member = null;
        try { member = module.ResolveMember(token, typeArgs, methodArgs); }
        catch
        {
            try { member = module.ResolveType(token, typeArgs, methodArgs); } catch { }
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
