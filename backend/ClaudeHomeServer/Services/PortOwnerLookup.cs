using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace ClaudeHomeServer.Services;

/// <summary>Кто слушает порт: номер процесса и его имя (имя может не читаться — процесс чужой).</summary>
public sealed record PortOwner(int Pid, string? ProcessName);

/// <summary>
/// Находит процесс, слушающий локальный порт.
///
/// Зачем: сервис, поднятый вне продукта (или переживший его перезапуск), нельзя остановить
/// штатно — своего объекта процесса у нас нет. Чтобы предложить «Стоп», нужно сначала
/// узнать, кого гасить.
///
/// Кроссплатформенного API для этого в .NET нет: <c>IPGlobalProperties</c> отдаёт слушающие
/// адреса, но без владельцев. Поэтому здесь Windows-путь через iphlpapi — продукт по
/// архитектуре работает на хосте Windows. На других платформах честно возвращаем null:
/// кнопка остановки чужого процесса там просто не появится, а врать про владельца хуже,
/// чем не знать его.
/// </summary>
public static class PortOwnerLookup
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    // TCP_TABLE_OWNER_PID_LISTENER — только слушающие сокеты с владельцами
    private const int TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tblClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;   // сетевой порядок байт
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    /// <summary>
    /// Кто слушает порт, либо null (никто, платформа не поддержана или запрос не удался).
    /// Смотрим обе семьи адресов: dev-серверы на Node по умолчанию слушают ::1.
    /// </summary>
    public static PortOwner? Find(int port)
    {
        if (port <= 0 || !OperatingSystem.IsWindows()) return null;

        var pid = FindPid(port, AfInet) ?? FindPid(port, AfInet6);
        if (pid is null or 0) return null;

        string? name = null;
        try { name = Process.GetProcessById(pid.Value).ProcessName; }
        catch { /* процесс успел уйти или доступ закрыт — имени просто не будет */ }
        return new PortOwner(pid.Value, name);
    }

    private static int? FindPid(int port, int family)
    {
        var size = 0;
        // Первый вызов — узнать нужный размер буфера
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                return null;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = family == AfInet ? Marshal.SizeOf<TcpRowOwnerPid>() : Marshal.SizeOf<Tcp6RowOwnerPid>();
            var cursor = buffer + sizeof(int);

            for (var i = 0; i < count; i++)
            {
                var (rowPort, rowPid) = family == AfInet
                    ? ReadV4(Marshal.PtrToStructure<TcpRowOwnerPid>(cursor))
                    : ReadV6(Marshal.PtrToStructure<Tcp6RowOwnerPid>(cursor));
                if (rowPort == port) return (int)rowPid;
                cursor += rowSize;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (int Port, uint Pid) ReadV4(TcpRowOwnerPid row) => (HostPort(row.LocalPort), row.OwningPid);

    private static (int Port, uint Pid) ReadV6(Tcp6RowOwnerPid row) => (HostPort(row.LocalPort), row.OwningPid);

    /// <summary>Порт в таблице лежит в сетевом порядке байт, в младших двух.</summary>
    private static int HostPort(uint raw) => IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF)) & 0xFFFF;
}
