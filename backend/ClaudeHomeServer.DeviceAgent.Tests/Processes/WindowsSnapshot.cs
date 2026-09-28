using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClaudeHomeServer.DeviceAgent.Tests.Processes;

/// <summary>
/// Потомки процесса по снимку ToolHelp: внуков ищем по pid родителя, а не файлом от самого
/// процесса — на Windows записать pid умеет только тяжёлый шелл вроде PowerShell.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x2;

    public static int[] ChildrenOf(int parentPid, string exeName)
    {
        using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "снимок процессов не снят");
        var result = new List<int>();
        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            if (entry.th32ParentProcessID == parentPid && string.Equals(entry.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                result.Add((int)entry.th32ProcessID);
        return [.. result];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle hSnapshot, ref PROCESSENTRY32W lppe);
}
