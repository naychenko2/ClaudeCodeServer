using System.Diagnostics;

namespace AiHomeDesktop.Windows.Interop;

/// <summary>Что сейчас на входном рабочем столе устройства.</summary>
public enum DesktopInputState
{
    /// <summary>Обычный рабочий стол пользователя.</summary>
    Normal,

    /// <summary>Экран заблокирован — снимать и действовать нечего.</summary>
    Locked,

    /// <summary>Защищённый рабочий стол: UAC или экран входа.</summary>
    Secure
}

/// <summary>
/// Состояние рабочего стола. Нужно, чтобы вместо чёрного кадра модель получала честный
/// исход session_locked / secure_desktop: чёрный прямоугольник она истолкует как «экран
/// пустой» и пойдёт действовать вслепую.
/// </summary>
public static class DesktopState
{
    public static DesktopInputState Current()
    {
        var desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DesktopSwitchDesktop);
        if (desktop == 0)
        {
            // Входной рабочий стол чужой: либо блокировка, либо запрос UAC.
            return IsConsentRunning() ? DesktopInputState.Secure : DesktopInputState.Locked;
        }

        try
        {
            var name = NameOf(desktop);
            if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
                return DesktopInputState.Normal;

            // Winlogon — и блокировка, и UAC; различает их наличие процесса согласия.
            return IsConsentRunning() ? DesktopInputState.Secure : DesktopInputState.Locked;
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }

    private static string NameOf(nint desktop)
    {
        var buffer = new char[256];
        return NativeMethods.GetUserObjectInformation(desktop, NativeMethods.UoiName, buffer, buffer.Length * 2, out _)
            ? new string(buffer).TrimEnd('\0')
            : "";
    }

    private static bool IsConsentRunning()
    {
        try
        {
            return Process.GetProcessesByName("consent").Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
