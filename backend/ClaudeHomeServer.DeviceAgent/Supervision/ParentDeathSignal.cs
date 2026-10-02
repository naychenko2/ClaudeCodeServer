using System.Runtime.InteropServices;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Linux: дочерний супервизора просит ядро прислать ему SIGTERM, когда родитель умрёт
/// (<c>PR_SET_PDEATHSIG</c>) — жёстко убитый супервизор не оставляет сироту. На Windows ту
/// же гарантию даёт Job Object супервизора, здесь ничего не делается.
///
/// Грабля: «родитель» для ядра — ПОТОК, сделавший fork. Поэтому супервизор порождает детей
/// с выделенного вечного потока (<see cref="ProcessChildLauncher"/>): поток пула мог бы
/// завершиться сам, и ядро убило бы здорового дочернего.
///
/// Файл без зависимостей: его же компилирует фикстура теста совместимости.
/// </summary>
internal static class ParentDeathSignal
{
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGTERM = 15;

    /// <summary>
    /// Взвести сигнал. false — родитель уже не <paramref name="expectedParentPid"/>: он умер
    /// между fork и этим вызовом, дочернему пора выходить.
    /// </summary>
    public static bool Arm(int expectedParentPid)
    {
        if (!OperatingSystem.IsLinux()) return true;
        // Не взвёлся — живём как без супервизора, это не причина не работать
        if (prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0) != 0) return true;
        return getppid() == expectedParentPid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [DllImport("libc")]
    private static extern int getppid();
}
