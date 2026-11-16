using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm;

// Результат разбора текста `ipmitool sdr elist full` (поле `sdr` из /run/ccs-ipmi/sdr.json),
// ADR-029 «Узел — набор устройств» (b332a28d7). Индекс в SocketTempsC/Vrms — номер сокета
// из имени − 1; длина массивов не больше 8, пустой массив — таких датчиков в тексте нет.
public sealed record IpmiReading(
    int?[] SocketTempsC,
    int?[] Vrms,
    int? BoardVrm,
    HwmonFan[] Fans,
    int? BoardTempC,
    int? PeripheralTempC);

// Разбор — чистая функция (как MeminfoReader.Parse): вход недоверенный — на любой текст есть
// результат, исключение наружу не выходит никогда. Entity в прошивке Supermicro ненадёжен
// (на H11DSi в entity 3 лежат и CPU Temp, и вольтажи): температуры разбираются ПО ИМЕНИ И ЕДИНИЦАМ
// (`degrees C`), entity смотрятся только для вентиляторов (29).
public static class IpmiSdrParser
{
    private const int MaxLines = 512;  // Разбирается не больше строк, остальные отбрасываются.
    private const int MaxSockets = 8;  // Потолок длины SocketTempsC/Vrms.
    private const int MinTempC = -50;
    private const int MaxTempC = 200;  // Температура вне диапазона → null в поле (ADR-029 «Потолки»).

    private static readonly Regex SocketRe = Pattern(@"^CPU(\d+) Temp$");
    private static readonly Regex VrmCpuSocRe = Pattern(@"^VRM(?:Cpu|Soc)(\d+) Temp$");
    private static readonly Regex VrmGroupRe = Pattern(@"^VRMP(\d+)[A-H]+ Temp$");
    private static readonly Regex TempValueRe = Pattern(@"^(-?\d+) degrees C$");
    private static readonly Regex RpmValueRe = Pattern(@"^(\d+) RPM$");

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant);

    // null или пусто → null. Иначе результат есть всегда — возможно, пустый.
    public static IpmiReading? Parse(string? sdr)
    {
        if (string.IsNullOrWhiteSpace(sdr)) return null;
        try
        {
            var socketTemps = new int?[MaxSockets];
            var vrms = new int?[MaxSockets];
            var fans = new List<HwmonFan>();
            int? boardVrm = null;
            int? boardTemp = null;
            int? peripheralTemp = null;
            int socketsSeen = 0; // макс. индекс+1 виденного датчика сокета (длина массива).
            int vrmsSeen = 0;

            int linesTaken = 0;
            foreach (var rawLine in sdr.Split('\n'))
            {
                if (linesTaken == MaxLines) break;
                linesTaken++;

                var parts = rawLine.TrimEnd('\r').Split('|');
                if (parts.Length < 5) continue; // Строка без поля значения — не запись датчика.
                var name = parts[0].Trim();
                var status = parts[2].Trim();
                var value = parts[4].Trim();

                // Вентилятор — единственная ветка, которая смотрит entity (29): статус ok и RPM > 0.
                if (IsFanEntity(parts[3].Trim()) && status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    var rm = RpmValueRe.Match(value);
                    if (rm.Success && TryInt(rm.Groups[1].Value, out var rpm) && rpm > 0)
                        fans.Add(new HwmonFan { Label = name, Rpm = rpm });
                }

                // Вид датчика опознаётся по ИМЕНИ (не по entity) и существует независимо от значения:
                // `ns`/`No Reading` даёт null в поле, но датчик остаётся в модели. Значение,
                // не являющееся температурой (Volts, Presence Detected), поля не наполняет.
                var sm = SocketRe.Match(name);
                Match? vm = null;
                if (!sm.Success)
                {
                    var v1 = VrmCpuSocRe.Match(name);
                    vm = v1.Success ? v1 : VrmGroupRe.Match(name);
                }

                if (sm.Success || vm!.Success)
                {
                    int idx = SocketIndex(sm.Success ? sm : vm!);
                    if (idx >= 0)
                    {
                        int? temp = ParseTemp(value);
                        if (sm.Success)
                        {
                            socketsSeen = Math.Max(socketsSeen, idx + 1);
                            socketTemps[idx] = Max(socketTemps[idx], temp);
                        }
                        else
                        {
                            vrmsSeen = Math.Max(vrmsSeen, idx + 1);
                            vrms[idx] = Max(vrms[idx], temp);
                        }
                    }
                    continue;
                }

                if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase))
                    boardVrm = Max(boardVrm, ParseTemp(value)); // Неразведённый VRM — на плату, сокету не приписывается.
                else if (name == "System Temp")
                    boardTemp = Max(boardTemp, ParseTemp(value));
                else if (name == "Peripheral Temp")
                    peripheralTemp = Max(peripheralTemp, ParseTemp(value));
                // Всё прочее (DIMM, вольтажи, ns) пропускается без ошибки.
            }

            return new IpmiReading(
                TrimTo(socketTemps, socketsSeen), TrimTo(vrms, vrmsSeen),
                boardVrm, fans.ToArray(), boardTemp, peripheralTemp);
        }
        catch
        {
            return null; // Страховка: исключение разбора наружу не выходит никогда.
        }
    }

    // Номер сокета из имени (нумерация с 1) → индекс n − 1; вне 1..MaxSockets — строка пропускается.
    private static int SocketIndex(Match m) =>
        TryInt(m.Groups[1].Value, out var n) && n >= 1 && n <= MaxSockets ? n - 1 : -1;

    // Entity записи — часть до '.': вентилятор — 29.
    private static bool IsFanEntity(string entity)
    {
        var dot = entity.IndexOf('.');
        return TryInt(dot >= 0 ? entity[..dot] : entity, out var e) && e == 29;
    }

    // Значение температуры: «N degrees C» в −50…200; прочие единицы и вне диапазона → null.
    private static int? ParseTemp(string value)
    {
        var tm = TempValueRe.Match(value);
        return tm.Success && TryInt(tm.Groups[1].Value, out var t) && t >= MinTempC && t <= MaxTempC ? t : null;
    }

    private static bool TryInt(string s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    // Максимум из тех, у кого есть значение; null не участвует.
    private static int? Max(int? a, int? b) => b is null ? a : (a is null || b > a ? b : a);

    // Длина массива = макс. индекс виденного датчика + 1; 0 — датчиков нет.
    private static int?[] TrimTo(int?[] arr, int seen)
    {
        if (seen <= 0) return Array.Empty<int?>();
        var res = new int?[seen];
        Array.Copy(arr, res, seen);
        return res;
    }
}
