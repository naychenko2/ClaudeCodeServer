using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AiHomeDesktop.App.Tray;

/// <summary>
/// Иконка в трее: показать и скрыть окно, выход, индикатор активного сеанса рук.
///
/// Разница между «скрыть» и «выйти» здесь смысловая, а не про удобство: закрытие окна
/// оболочки гасит сеанс рук на сервере, а жизнь в трее закрытием НЕ считается (ADR-008,
/// «Сеанс рук и согласие»). Поэтому подсказка проговаривает это человеку прямым текстом —
/// иначе он свернёт окно и будет думать, что руки отобраны.
///
/// NotifyIcon — из WinForms: своей иконки трея у WPF нет, а тянуть пакет ради одной
/// иконки незачем.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    /// <summary>Оранжевый акцент продукта (frontend/src/lib/theme.css, --c-accent).</summary>
    private static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _handsItem;
    private readonly Icon _idleIcon;
    private readonly Icon _activeIcon;

    public TrayIcon()
    {
        _idleIcon = BuildIcon(filled: false);
        _activeIcon = BuildIcon(filled: true);

        _handsItem = new ToolStripMenuItem("Сеанс рук не начат") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_handsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Показать окно", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Скрыть окно", null, (_, _) => HideRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выйти из AI Home Desktop", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new NotifyIcon
        {
            Icon = _idleIcon,
            // Подсказка трея — 63 символа потолка, длиннее Windows обрежет молча
            Text = "AI Home Desktop — руки на этом компьютере",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Человек просит окно (двойной клик или пункт меню).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Человек прячет окно в трей. Сеанс рук от этого не гаснет.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Настоящий выход: окно оболочки закрывается, сеанс рук — тоже.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Индикатор активного сеанса: заливка вместо кольца плюс строка в меню.</summary>
    public void SetHandsActive(bool active, string? chatName = null)
    {
        _icon.Icon = active ? _activeIcon : _idleIcon;
        _handsItem.Text = active
            ? $"Руки отданы чату: {chatName ?? "без названия"}"
            : "Сеанс рук не начат";
    }

    /// <summary>Всплывающая подсказка. Тише некуда: у трея это единственный способ позвать.</summary>
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idleIcon.Dispose();
        _activeIcon.Dispose();
    }

    // Иконку рисуем сами: два состояния — кольцо (сеанса нет) и заливка (руки отданы).
    // Бинарного ресурса в репозитории ради двух кружков не заводим.
    private static Icon BuildIcon(bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            if (filled)
            {
                using var brush = new SolidBrush(Accent);
                graphics.FillEllipse(brush, 4, 4, 24, 24);
            }
            else
            {
                using var pen = new Pen(Accent, 4f);
                graphics.DrawEllipse(pen, 5, 5, 22, 22);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Клон нужен, чтобы иконка пережила освобождение хэндла: Icon.FromHandle
            // владельцем не становится, а хэндл течёт GDI-объектом
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
