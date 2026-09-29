using System.Text;

namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Разбор командной строки по правилам <c>CommandLineToArgvW</c> без обращения к WinAPI —
/// одинаково на Windows и в тестах на Linux. Так гейт видит аргументы теми же, какими их увидит
/// запущенная программа, в том числе ключ, склеенный из кусков в кавычках.
/// </summary>
public static class HandsArguments
{
    /// <summary>
    /// Пробел и табуляция вне кавычек разделяют аргументы; 2n обратных слешей перед кавычкой —
    /// n слешей и переключение кавычек, 2n+1 — n слешей и буквальная кавычка; слеши не перед
    /// кавычкой — буквальные; <c>""</c> внутри кавычек — буквальная кавычка.
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (ch == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                hasToken = true;
                continue;
            }

            if (ch == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasToken = true;
                continue;
            }

            if (!inQuotes && ch is ' ' or '\t')
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(ch);
            hasToken = true;
        }

        if (hasToken)
            result.Add(current.ToString());

        return result;
    }
}
