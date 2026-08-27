using AiHomeDesktop.App.Shell;
using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;
using UserControl = System.Windows.Controls.UserControl;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Visibility = System.Windows.Visibility;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace AiHomeDesktop.App.Pairing;

/// <summary>
/// Экран сопряжения. Обмен кода на токен устройства идёт по существующему эндпоинту
/// <c>POST /api/devices/pair</c>; отпечаток машины и версию клиента подставляет ядро.
///
/// Отказы сервер формулирует сам и по-человечески — перебор попыток (429), совпадение с
/// машиной бэкенда, отозванная веб-сессия, протухший код, — поэтому текст показывается
/// как есть. Клиент проверяет до отправки ровно две вещи, которые сервер не увидит:
/// разбор адреса и защищённость канала (по открытому http код не отдаём никому, кроме
/// петли) — иначе код успел бы уехать в открытом виде до первого отказа.
/// </summary>
public partial class PairingView : UserControl
{
    private readonly DeviceApi _api;
    private bool _busy;

    public PairingView(DeviceApi api, string? serverUrl, string? deviceName)
    {
        _api = api;
        InitializeComponent();

        ServerBox.Text = serverUrl ?? "";
        NameBox.Text = string.IsNullOrWhiteSpace(deviceName)
            ? DefaultDeviceName()
            : deviceName;
    }

    /// <summary>Сопряжение прошло: наружу уезжает учётка устройства и ничего кроме неё.</summary>
    public event EventHandler<DeviceCredentials>? Paired;

    /// <summary>Фокус на первое незаполненное поле — человек пришёл сюда вводить код.</summary>
    public void FocusFirstField()
    {
        if (string.IsNullOrWhiteSpace(ServerBox.Text)) ServerBox.Focus();
        else CodeBox.Focus();
    }

    private void OnCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = PairAsync();
    }

    private void OnPairClick(object sender, RoutedEventArgs e) => _ = PairAsync();

    private async Task PairAsync()
    {
        if (_busy) return;

        ShowError(null);

        if (!ServerAddress.TryParse(ServerBox.Text, out var server, out var addressError))
        {
            ShowError(addressError);
            ServerBox.Focus();
            return;
        }

        var name = DeviceRegistryName(NameBox.Text);
        if (name.Length == 0)
        {
            ShowError("Укажите имя устройства: им вы адресуете руки в чате.");
            NameBox.Focus();
            return;
        }

        var code = (CodeBox.Text ?? "").Trim();
        if (code.Length != 8)
        {
            ShowError("Код сопряжения — восемь символов. Выпустите его в веб-морде AI Home.");
            CodeBox.Focus();
            return;
        }

        SetBusy(true);
        try
        {
            var outcome = await _api.PairAsync(server!, code, name, ClientInfo.Version);
            if (!outcome.Ok || outcome.Credentials is null)
            {
                // Текст сочинил сервер — он и знает, что именно не сошлось
                ShowError(outcome.Error ?? "Сопряжение не прошло.");
                CodeBox.SelectAll();
                CodeBox.Focus();
                return;
            }

            CodeBox.Clear();
            Paired?.Invoke(this, outcome.Credentials);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PairButton.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        CodeBox.IsEnabled = !busy;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorPane.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Имя по умолчанию — имя машины, приведённое к правилам реестра устройств: буквы,
    // цифры, дефис, подчёркивание, пробел, не длиннее 32 символов
    private static string DefaultDeviceName() => DeviceRegistryName(Environment.MachineName);

    private static string DeviceRegistryName(string? raw)
    {
        var allowed = new string((raw ?? "")
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '-')
            .ToArray());
        var normalized = string.Join(' ', allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > 32 ? normalized[..32].Trim() : normalized;
    }
}
