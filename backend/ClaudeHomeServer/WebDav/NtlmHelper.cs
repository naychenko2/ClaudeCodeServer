using System.Text;

namespace ClaudeHomeServer.WebDav;

/// <summary>
/// Вспомогательные функции для NT-хэша.
/// NTLM-хендшейк теперь полностью делегирован ASP.NET Core Negotiate authentication handler.
/// </summary>
internal static class NtlmHelper
{
    /// <summary>
    /// Вычисляет NT-хэш пароля: MD4(UTF-16LE(password)).
    /// Используется в UserStore для хранения NT-хэша пользователя.
    /// </summary>
    public static byte[] ComputeNtHash(string password) =>
        MD4.ComputeHash(Encoding.Unicode.GetBytes(password));

    // ────────────────────────────────────────────────────────────────────────
    // MD4 (RFC 1320) — нужен для NT-хэша в UserStore; убран из .NET Core
    // ────────────────────────────────────────────────────────────────────────

    private static class MD4
    {
        public static byte[] ComputeHash(byte[] data)
        {
            uint a = 0x67452301u, b = 0xEFCDAB89u, c = 0x98BADCFEu, d = 0x10325476u;

            int   rawLen = data.Length;
            int   padLen = ((rawLen + 8) / 64 + 1) * 64;
            var   m      = new byte[padLen];
            Buffer.BlockCopy(data, 0, m, 0, rawLen);
            m[rawLen] = 0x80;
            ulong bits = (ulong)rawLen * 8;
            for (int i = 0; i < 8; i++) m[padLen - 8 + i] = (byte)(bits >> (i * 8));

            var x = new uint[16];
            for (int blk = 0; blk < padLen; blk += 64)
            {
                for (int j = 0; j < 16; j++)
                    x[j] = BitConverter.ToUInt32(m, blk + j * 4);

                uint aa = a, bb = b, cc = c, dd = d;

                static uint F(uint B, uint C, uint D) => (B & C) | (~B & D);
                static uint G(uint B, uint C, uint D) => (B & C) | (B & D) | (C & D);
                static uint H(uint B, uint C, uint D) => B ^ C ^ D;
                static uint RL(uint v, int s) => (v << s) | (v >> (32 - s));

                int[] s1 = [3, 7, 11, 19];
                for (int j = 0; j < 16; j++)
                {
                    a = RL(a + F(b, c, d) + x[j], s1[j & 3]);
                    (a, b, c, d) = (d, a, b, c);
                }

                int[] s2   = [3, 5, 9, 13];
                int[] idx2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
                for (int j = 0; j < 16; j++)
                {
                    a = RL(a + G(b, c, d) + x[idx2[j]] + 0x5A827999u, s2[j & 3]);
                    (a, b, c, d) = (d, a, b, c);
                }

                int[] s3   = [3, 9, 11, 15];
                int[] idx3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
                for (int j = 0; j < 16; j++)
                {
                    a = RL(a + H(b, c, d) + x[idx3[j]] + 0x6ED9EBA1u, s3[j & 3]);
                    (a, b, c, d) = (d, a, b, c);
                }

                a += aa; b += bb; c += cc; d += dd;
            }

            var result = new byte[16];
            void WriteLE(int off, uint v)
            {
                result[off]     = (byte)v;
                result[off + 1] = (byte)(v >> 8);
                result[off + 2] = (byte)(v >> 16);
                result[off + 3] = (byte)(v >> 24);
            }
            WriteLE(0, a); WriteLE(4, b); WriteLE(8, c); WriteLE(12, d);
            return result;
        }
    }
}
