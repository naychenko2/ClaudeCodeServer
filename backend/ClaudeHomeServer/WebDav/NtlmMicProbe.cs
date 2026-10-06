using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.WebDav;

/// <summary>
/// Самодиагностика отказа по MIC. gss-ntlmssp при неверной подписи рукопожатия отдаёт общий
/// DEFECTIVE_TOKEN и не говорит, КАКОЙ из входов разошёлся с клиентом: ключ сессии, Type1,
/// Type2 или смещение MIC. Сервер знает NT-хэш пользователя (файл NTLM_USER_FILE), поэтому по
/// записанным Type1/Type2 и присланному Type3 может сам пересчитать MIC при каждой гипотезе и
/// сказать, какая из них совпала с подписью клиента (MS-NLMP 3.1.5.1.2, 3.2.5.1.2, 2.2.2.9.1).
///
/// В результат попадают только признаки (совпало/нет, флаги, номера); ключи, хэш и подписи не
/// выводятся никогда. Чистая функция без обращений к диску и сети — поэтому тестируется на
/// сообщениях, собранных в самом тесте.
/// </summary>
internal static class NtlmMicProbe
{
    private const uint FlagUnicode = 0x1;
    private const uint FlagVersion = 0x02000000;

    /// <param name="type1s">Все Type1, виденные на соединении (на HTTP/2 их может быть несколько).</param>
    /// <param name="type2s">Все Type2, отданные сервером на этом соединении.</param>
    /// <param name="type3">Присланный Type3 (чистый NTLMSSP, без SPNEGO-обёртки).</param>
    /// <param name="ntHash">NT-хэш пользователя из файла NTLM_USER_FILE.</param>
    public static string Explain(IReadOnlyList<byte[]> type1s, IReadOnlyList<byte[]> type2s, byte[] type3, byte[] ntHash, string? timeline = null)
    {
        if (!TryParseType3(type3, out var t3, out var why)) return why;
        if (t3.NtResponse.Length < 44) return "NT-ответ короче NTLMv2, MIC не проверить";
        if (type2s.Count == 0) return "Type2 этого соединения не записан (запись началась после рукопожатия?)";

        var proof = t3.NtResponse.AsSpan(0, 16);
        var blob = t3.NtResponse.AsSpan(16);
        var blobBytes = t3.NtResponse[16..];
        var responseKey = HmacMd5(ntHash, Encoding.Unicode.GetBytes(t3.User.ToUpperInvariant() + t3.Domain));

        // NTProofStr привязан к серверному вызову: по нему находим именно тот Type2, на который ответил клиент
        byte[]? type2 = null;
        foreach (var candidate in type2s)
        {
            if (candidate.Length < 32) continue;
            var expected = HmacMd5(responseKey, [.. candidate.AsSpan(24, 8), .. blob]);
            if (expected.AsSpan().SequenceEqual(proof)) { type2 = candidate; break; }
        }
        if (type2 is null)
            return $"NTProofStr не сходится ни с одним из {type2s.Count} Type2: пароль/хэш другой или домен '{t3.Domain}' в хэше записан иначе";

        var sessionBaseKey = HmacMd5(responseKey, proof.ToArray());
        var keys = new List<(string Name, byte[] Key)> { ("KeyExchangeKey (без расшифровки)", sessionBaseKey) };
        if (t3.EncryptedSessionKey.Length == 16)
            keys.Add(("RC4(KeyExchangeKey, EncryptedRandomSessionKey)", Rc4(sessionBaseKey, t3.EncryptedSessionKey)));
        // KXKEY расширенной защиты в буквальном чтении MS-NLMP 3.4.5.1: HMAC(SessionBaseKey, ServerChallenge ‖ LmResponse[0..8]).
        // gss-ntlmssp и Samba для NTLMv2 берут SessionBaseKey как есть; клиент, считающий иначе, дал бы именно этот ключ
        if (t3.LmResponse.Length >= 8)
        {
            var kxEss = HmacMd5(sessionBaseKey, [.. type2.AsSpan(24, 8), .. t3.LmResponse.AsSpan(0, 8)]);
            keys.Add(("KXKEY-ESS (HMAC(SessionBaseKey, ServerChallenge‖LM[0..8])) без расшифровки", kxEss));
            if (t3.EncryptedSessionKey.Length == 16)
                keys.Add(("RC4(KXKEY-ESS, EncryptedRandomSessionKey)", Rc4(kxEss, t3.EncryptedSessionKey)));
        }

        // MIC лежит сразу за Version; старые клиенты Version не шлют, и смещение сдвигается на 8
        var offsets = (t3.Flags & FlagVersion) != 0 ? new[] { 72 } : new[] { 72, 64 };
        var type1Options = new List<(string Name, byte[]? Msg)>();
        for (var i = 0; i < type1s.Count; i++) type1Options.Add(($"Type1 №{i + 1}", type1s[i]));
        for (var i = 0; i < type1s.Count; i++)
            if (type1s[i].Length > 32) type1Options.Add(($"Type1 №{i + 1} без Version (32 байта)", type1s[i][..32]));
        type1Options.Add(("без Type1", null));

        // Type2 перебираем все записанные (а не только по NTProofStr) и Type3 в двух видах:
        // с обнулённым MIC (MS-NLMP) и как есть — на случай, если клиент считает иначе
        var type2Options = new List<(string Name, byte[] Msg)> { ("Type2 по NTProofStr", type2) };
        for (var i = 0; i < type2s.Count; i++)
            if (!ReferenceEquals(type2s[i], type2)) type2Options.Add(($"Type2 №{i + 1}", type2s[i]));

        var matches = new List<string>();
        var combos = 0;
        foreach (var offset in offsets)
        {
            if (offset + 16 > type3.Length) continue;
            var sent = type3.AsSpan(offset, 16).ToArray();
            var zeroed = (byte[])type3.Clone();
            Array.Clear(zeroed, offset, 16);
            foreach (var (t3Name, t3Body) in new[] { ("Type3 с нулевым MIC", zeroed), ("Type3 как есть", type3) })
            foreach (var (t2Name, t2) in type2Options)
            foreach (var (t1Name, t1) in type1Options)
            foreach (var (keyName, key) in keys)
            {
                combos++;
                byte[] data = t1 is null ? [.. t2, .. t3Body] : [.. t1, .. t2, .. t3Body];
                if (HmacMd5(key, data).AsSpan().SequenceEqual(sent))
                    matches.Add($"{keyName}; {t1Name}; {t2Name}; {t3Name}; смещение MIC {offset}");
            }
        }

        var t1Flags = type1s.Count > 0 && type1s[^1].Length >= 16 ? $"0x{BitConverter.ToUInt32(type1s[^1], 12):X8}" : "?";
        var avs = $"AV Type3: {AvList(blobBytes[28..])}; AV Type2: {AvList(TargetInfo(type2))}; не вернулись из Type2 в Type3: {MissingFromType2(TargetInfo(type2), blobBytes[28..])}; LM-ответ {(t3.LmResponse.AsSpan().IndexOfAnyExcept((byte)0) < 0 ? "нулевой" : "ненулевой")}";
        var shape = $"флаги Type1 {t1Flags}, Type2 0x{BitConverter.ToUInt32(type2, 20):X8}, Type3 0x{t3.Flags:X8}, "
            + $"EncryptedRandomSessionKey {t3.EncryptedSessionKey.Length} байт, Type1 записано {type1s.Count}";
        // Type1/Type2 — открытые сообщения рукопожатия (клиент видел их на проводе), секретов нет;
        // Type3 не выводим: по нему и Type2 офлайн подбирается пароль
        var wire = $"Type1 {type1s.Count}шт {string.Join("/", type1s.Select(m => m.Length))} байт: {string.Join(" | ", type1s.Select(Convert.ToHexString))}; "
            + $"Type2 {type2s.Count}шт, ответ клиента на {type2Options[0].Msg.Length} байт: {Convert.ToHexString(type2)}; "
            + $"Type3 {type3.Length} байт, заголовок: {Convert.ToHexString(type3.AsSpan(0, Math.Min(72, type3.Length)))}"
            + (timeline is null ? "" : $"; хроника соединения: {timeline}");
        return matches.Count > 0
            ? $"MIC СХОДИТСЯ при: {string.Join(" | ", matches)}; {shape}"
            : $"MIC не сходится ни в одной из {combos} комбинаций (NTProofStr верен); {shape}; {avs}; {wire}";
    }

    private static byte[] TargetInfo(byte[] type2)
    {
        if (type2.Length < 48) return [];
        int len = BitConverter.ToUInt16(type2, 40);
        var off = (int)BitConverter.ToUInt32(type2, 44);
        return off < 0 || off + len > type2.Length ? [] : type2.AsSpan(off, len).ToArray();
    }

    /// <summary>AV-пары списком «id:длина» — содержимое не выводим (там есть идентификатор машины и хэш привязки канала).</summary>
    internal static string AvList(ReadOnlySpan<byte> av)
    {
        var parts = new List<string>();
        for (var p = 0; p + 4 <= av.Length;)
        {
            int id = BitConverter.ToUInt16(av[p..]);
            int len = BitConverter.ToUInt16(av[(p + 2)..]);
            p += 4;
            if (id == 0 || p + len > av.Length) break;
            parts.Add($"{id}:{len}");
            p += len;
        }
        return string.Join(",", parts);
    }

    /// <summary>Пары TargetInfo Type2, которых нет в Type3 с тем же содержимым (MsvAvFlags клиент правит сам). Пусто — Type2 вернулся целиком.</summary>
    internal static string MissingFromType2(ReadOnlySpan<byte> type2Info, ReadOnlySpan<byte> type3Av)
    {
        var have = new List<(int Id, byte[] Value)>();
        for (var p = 0; p + 4 <= type3Av.Length;)
        {
            int id = BitConverter.ToUInt16(type3Av[p..]);
            int len = BitConverter.ToUInt16(type3Av[(p + 2)..]);
            p += 4;
            if (id == 0 || p + len > type3Av.Length) break;
            have.Add((id, type3Av.Slice(p, len).ToArray()));
            p += len;
        }
        var missing = new List<string>();
        for (var p = 0; p + 4 <= type2Info.Length;)
        {
            int id = BitConverter.ToUInt16(type2Info[p..]);
            int len = BitConverter.ToUInt16(type2Info[(p + 2)..]);
            p += 4;
            if (id == 0 || p + len > type2Info.Length) break;
            var value = type2Info.Slice(p, len).ToArray();
            p += len;
            if (id != 6 && !have.Any(h => h.Id == id && h.Value.AsSpan().SequenceEqual(value))) missing.Add($"{id}:{len}");
        }
        return missing.Count == 0 ? "ничего" : string.Join(",", missing);
    }

    internal readonly record struct Type3(uint Flags, string Domain, string User, byte[] NtResponse, byte[] EncryptedSessionKey, byte[] LmResponse);

    internal static bool TryParseType3(byte[] msg, out Type3 result, out string why)
    {
        result = default;
        why = "";
        if (msg.Length < 64 || !msg.AsSpan(0, 8).SequenceEqual("NTLMSSP\0"u8) || BitConverter.ToUInt32(msg, 8) != 3)
        {
            why = "не Type3";
            return false;
        }
        var flags = BitConverter.ToUInt32(msg, 60);
        var unicode = (flags & FlagUnicode) != 0;
        var lm = Field(msg, 12);
        var nt = Field(msg, 20);
        var domain = Field(msg, 28);
        var user = Field(msg, 36);
        var enc = Field(msg, 52);
        if (lm is null || nt is null || domain is null || user is null || enc is null)
        {
            why = "поля Type3 вне границ";
            return false;
        }
        var enc2 = unicode ? Encoding.Unicode : Encoding.ASCII;
        result = new Type3(flags, enc2.GetString(domain), enc2.GetString(user), nt, enc, lm);
        return true;

        static byte[]? Field(byte[] m, int at)
        {
            int len = BitConverter.ToUInt16(m, at);
            var off = (int)BitConverter.ToUInt32(m, at + 4);
            return off < 0 || off + len > m.Length ? null : m.AsSpan(off, len).ToArray();
        }
    }

    internal static byte[] HmacMd5(byte[] key, byte[] data) => HMACMD5.HashData(key, data);

    /// <summary>RC4 (MS-NLMP RC4K). Нужен только для расшифровки ключа сессии.</summary>
    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return output;
    }
}
