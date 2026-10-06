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
    public static string Explain(IReadOnlyList<byte[]> type1s, IReadOnlyList<byte[]> type2s, byte[] type3, byte[] ntHash)
    {
        if (!TryParseType3(type3, out var t3, out var why)) return why;
        if (t3.NtResponse.Length < 44) return "NT-ответ короче NTLMv2, MIC не проверить";
        if (type2s.Count == 0) return "Type2 этого соединения не записан (запись началась после рукопожатия?)";

        var proof = t3.NtResponse.AsSpan(0, 16);
        var blob = t3.NtResponse.AsSpan(16);
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

        // MIC лежит сразу за Version; старые клиенты Version не шлют, и смещение сдвигается на 8
        var offsets = (t3.Flags & FlagVersion) != 0 ? new[] { 72 } : new[] { 72, 64 };
        var type1Options = new List<(string Name, byte[]? Msg)>();
        for (var i = 0; i < type1s.Count; i++) type1Options.Add(($"Type1 №{i + 1}", type1s[i]));
        type1Options.Add(("без Type1", null));

        var matches = new List<string>();
        var combos = 0;
        foreach (var offset in offsets)
        {
            if (offset + 16 > type3.Length) continue;
            var sent = type3.AsSpan(offset, 16).ToArray();
            var zeroed = (byte[])type3.Clone();
            Array.Clear(zeroed, offset, 16);
            foreach (var (t1Name, t1) in type1Options)
            foreach (var (keyName, key) in keys)
            {
                combos++;
                byte[] data = t1 is null ? [.. type2, .. zeroed] : [.. t1, .. type2, .. zeroed];
                if (HmacMd5(key, data).AsSpan().SequenceEqual(sent))
                    matches.Add($"{keyName}; {t1Name}; смещение MIC {offset}");
            }
        }

        var t1Flags = type1s.Count > 0 && type1s[^1].Length >= 16 ? $"0x{BitConverter.ToUInt32(type1s[^1], 12):X8}" : "?";
        var shape = $"флаги Type1 {t1Flags}, Type2 0x{BitConverter.ToUInt32(type2, 20):X8}, Type3 0x{t3.Flags:X8}, "
            + $"EncryptedRandomSessionKey {t3.EncryptedSessionKey.Length} байт, Type1 записано {type1s.Count}";
        return matches.Count > 0
            ? $"MIC СХОДИТСЯ при: {string.Join(" | ", matches)}; {shape}"
            : $"MIC не сходится ни в одной из {combos} комбинаций (NTProofStr верен); {shape}";
    }

    internal readonly record struct Type3(uint Flags, string Domain, string User, byte[] NtResponse, byte[] EncryptedSessionKey);

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
        var nt = Field(msg, 20);
        var domain = Field(msg, 28);
        var user = Field(msg, 36);
        var enc = Field(msg, 52);
        if (nt is null || domain is null || user is null || enc is null)
        {
            why = "поля Type3 вне границ";
            return false;
        }
        var enc2 = unicode ? Encoding.Unicode : Encoding.ASCII;
        result = new Type3(flags, enc2.GetString(domain), enc2.GetString(user), nt, enc);
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
