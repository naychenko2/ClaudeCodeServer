using System.Diagnostics;

namespace ClaudeHomeServer.Tests.Services;

// Уборка фейкового процесса в Dispose харнесса. Ловит ровно тот перечень, что и продукт при
// уборке (ClaudeSession.FinalizeRunAsync): гонка с уборкой сессии даёт эти типы. NullReferenceException
// сюда НЕ входит намеренно: это признак двойного освобождения процесса, и тест обязан упасть.
internal static class ProcessTestCleanup
{
    public static void SafeDispose(Process process)
    {
        try { process.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException
            or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[ProcessTestCleanup] Dispose процесса упал на гонке состояния ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
