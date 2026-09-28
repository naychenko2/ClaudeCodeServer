using System.Text.Json;

namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>Прямоугольник в физических пикселях виртуального экрана (как <c>RECT</c> Win32).</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Пересечение; пустое — нулевой ширины или высоты.</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        int left = Math.Max(Left, other.Left), top = Math.Max(Top, other.Top);
        return new ScreenRect(left, top, Math.Max(left, Math.Min(Right, other.Right)), Math.Max(top, Math.Min(Bottom, other.Bottom)));
    }
}

/// <summary>Монитор глазами плашки.</summary>
/// <param name="Device">Имя устройства (<c>\\.\DISPLAY1</c>) — по нему узнаётся монитор при следующем показе.</param>
/// <param name="Bounds">Весь монитор.</param>
/// <param name="Work">Рабочая область — без панели задач.</param>
/// <param name="Scale">DPI монитора / 96.</param>
/// <param name="Primary">Основной монитор.</param>
internal sealed record PlateMonitor(string Device, ScreenRect Bounds, ScreenRect Work, double Scale, bool Primary);

/// <summary>
/// Запомненная позиция плашки: монитор и смещение её левого верхнего угла от угла монитора в
/// логических пикселях (DIP). В DIP, а не в физических: сменят масштаб монитора — плашка
/// останется на том же месте экрана.
/// </summary>
internal sealed record PlatePosition(string Monitor, double X, double Y);

/// <summary>Куда ставить плашку: монитор, левый верхний угол и размер в физических пикселях.</summary>
internal sealed record PlatePlacement(PlateMonitor Monitor, int Left, int Top, int Width, int Height);

/// <summary>
/// Геометрия и прозрачность плашки «ИИ управляет компьютером» без Win32. Плашка полупрозрачна,
/// под мышью и при перетаскивании — непрозрачна, чтобы «Стоп» читался и попадался уверенно.
/// Позиция запоминается; если запомненное место не на экране (монитор отключили, сменилось
/// разрешение) — правый нижний угол рабочей области основного монитора.
/// </summary>
internal static class PlateLayout
{
    /// <summary>Около 80 % непрозрачности.</summary>
    public const byte TranslucentAlpha = 204;
    public const byte OpaqueAlpha = 255;
    /// <summary>Отступ от угла рабочей области, DIP.</summary>
    public const int Margin = 12;
    /// <summary>
    /// Сколько плашки (DIP по каждой оси) должно остаться на рабочей области запомненного монитора,
    /// чтобы место считалось видимым. Меньше — позиция сбрасывается в угол.
    /// </summary>
    public const int MinVisible = 40;

    public static byte Alpha(bool hover, bool dragging) => hover || dragging ? OpaqueAlpha : TranslucentAlpha;

    public static int Scaled(int dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>Место плашки размера <paramref name="width"/>×<paramref name="height"/> DIP.</summary>
    public static PlatePlacement Place(PlatePosition? saved, IReadOnlyList<PlateMonitor> monitors, int width, int height)
    {
        if (monitors.Count == 0) throw new ArgumentException("нет ни одного монитора", nameof(monitors));
        if (saved is not null
            && monitors.FirstOrDefault(m => string.Equals(m.Device, saved.Monitor, StringComparison.OrdinalIgnoreCase)) is { } monitor)
        {
            int w = Scaled(width, monitor.Scale), h = Scaled(height, monitor.Scale), min = Scaled(MinVisible, monitor.Scale);
            int left = monitor.Bounds.Left + (int)Math.Round(saved.X * monitor.Scale);
            int top = monitor.Bounds.Top + (int)Math.Round(saved.Y * monitor.Scale);
            var visible = new ScreenRect(left, top, left + w, top + h).Intersect(monitor.Work);
            if (visible.Width >= Math.Min(min, w) && visible.Height >= Math.Min(min, h))
            {
                // Край торчит за рабочую область — прижать: «Стоп» должен быть виден целиком
                var work = monitor.Work;
                left = Math.Max(work.Left, Math.Min(left, work.Right - w));
                top = Math.Max(work.Top, Math.Min(top, work.Bottom - h));
                return new PlatePlacement(monitor, left, top, w, h);
            }
        }
        return Corner(monitors.FirstOrDefault(m => m.Primary) ?? monitors[0], width, height);
    }

    /// <summary>Угол по умолчанию: правый нижний угол рабочей области, отступ <see cref="Margin"/>.</summary>
    public static PlatePlacement Corner(PlateMonitor monitor, int width, int height)
    {
        int w = Scaled(width, monitor.Scale), h = Scaled(height, monitor.Scale), margin = Scaled(Margin, monitor.Scale);
        return new PlatePlacement(monitor, monitor.Work.Right - w - margin, monitor.Work.Bottom - h - margin, w, h);
    }

    /// <summary>Что запомнить, когда плашку отпустили в точке <paramref name="left"/>, <paramref name="top"/> на мониторе.</summary>
    public static PlatePosition ToSaved(PlateMonitor monitor, int left, int top) =>
        new(monitor.Device, (left - monitor.Bounds.Left) / monitor.Scale, (top - monitor.Bounds.Top) / monitor.Scale);
}

/// <summary>
/// Файл состояния трея <c>tray.json</c> в каталоге данных агента. Сбой чтения или записи не
/// мешает трею: позиция плашки не стоит того, чтобы из-за неё падать, — плашка встанет в угол.
/// </summary>
internal sealed class TrayStateStore(string directory)
{
    private sealed record TrayState(PlatePosition? Plate);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath => Path.Combine(directory, "tray.json");

    /// <summary>Каталог данных агента на Windows — тот же, что у <c>AgentPaths.ForCurrentUser</c>.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiHomeAgent");

    public PlatePosition? LoadPlate()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plate = JsonSerializer.Deserialize<TrayState>(File.ReadAllText(FilePath), Json)?.Plate;
            return plate is { Monitor.Length: > 0 } && double.IsFinite(plate.X) && double.IsFinite(plate.Y) ? plate : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SavePlate(PlatePosition plate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Через временный файл: оборванная запись не оставит битый tray.json
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new TrayState(plate), Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
