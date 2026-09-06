using System.Reflection;
using System.Reflection.Emit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Общий IL-скан тел методов для сторожей границ вертикалей.
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

        byte[]? il;
        try { il = body.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

        var module = method.Module;
        Type[]? typeArgs = null, methodArgs = null;
        try { typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null; } catch { }
        try { methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null; } catch { }

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
                        foreach (var t in ResolveToken(module, token, typeArgs, methodArgs))
                            yield return t;
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

    /// <summary>Единая точка сбора типов, упомянутых в IL тел всех методов типа
    /// (включая nested-типы). Используется ОДНОВРЕМЕННО из Theory-сторожей
    /// (<see cref="SubsystemBoundaryTests"/>, <see cref="RootSubsystemBoundaryTests"/>)
    /// и из регрессии (<see cref="IlBoundaryRegressionTests"/>): если сбор здесь
    /// сломать (например, вернуть к <c>GetMethods(DeclaredOnly)</c> без nested),
    /// регрессия покраснеет — гейт не декоративен.</summary>
    public static IEnumerable<Type> CollectAllReferencedTypes(Type t)
    {
        foreach (var method in AllMethodsWithNested(t))
            foreach (var referenced in TypesFromBody(method))
                yield return referenced;
    }
}
