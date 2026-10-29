using System.Diagnostics;

namespace ClaudeHomeServer.Tests.Services;

// Уборка фейкового процесса в Dispose харнесса. Ловит ровно тот перечень, что и продукт при
// уборке (ClaudeSession.FinalizeRunAsync): гонка с уборкой сессии даёт эти типы. NullReferenceException
// отдельно не глотается молча: это признак двойного освобождения процесса, пишется в stderr.
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
        catch (NullReferenceException ex)
        {
            // Харнесс убивает процесс, а сессия в FinalizeRunAsync освобождает тот же объект:
            // Process.Close из двух потоков даёт NRE. Не глотаем молча — пишем в stderr, чтобы
            // гонка оставалась видимой в логе прогона (дефект заведён отдельно)
            Console.Error.WriteLine($"[ProcessTestCleanup] ВНИМАНИЕ: NullReferenceException при Dispose процесса — двойное освобождение (харнесс и сессия): {ex.StackTrace}");
        }
    }
}
