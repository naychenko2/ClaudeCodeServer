using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Тосты подтверждения на рабочем столе. Один вызов — один тост; текст берётся из
/// <see cref="ConfirmationText"/>, то есть из фактических аргументов вызова.
///
/// Закрытие тоста крестиком — это отказ, а не «спросим потом»: молчание не повод действовать.
/// Отмена вызова сервером (истекло ожидание, погас сеанс, нажали «Стоп») закрывает тост
/// снаружи через <see cref="Close"/> — человек не должен подтверждать то, чего уже нет.
/// </summary>
public sealed class ConfirmationToasts(Dispatcher dispatcher) : ICallConfirmation
{
    private readonly ConcurrentDictionary<string, ConfirmationToastWindow> _open = new(StringComparer.Ordinal);

    public async Task<bool> AskAsync(ConfirmationRequest request, CancellationToken ct = default)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            var toast = new ConfirmationToastWindow(request, _open.Count, answer);
            _open[request.CallId] = toast;
            toast.Closed += (_, _) =>
            {
                _open.TryRemove(request.CallId, out _);
                // Окно закрыли, не ответив, — считаем отказом.
                answer.TrySetResult(false);
            };
            toast.Show();
        });

        // CancellationTokenRegistration сама по себе IAsyncDisposable — обёртки не нужно.
        await using var cancellation = ct.Register(() => Close(request.CallId, "вызов отменён"));
        return await answer.Task;
    }

    public void Close(string callId, string reason)
    {
        if (!_open.TryRemove(callId, out var toast)) return;
        dispatcher.InvokeAsync(() => toast.CloseWithReason(reason));
    }
}

/// <summary>
/// Окно тоста. Собрано кодом, без XAML: у него одна форма, и лишний ресурсный файл ей не нужен.
/// </summary>
internal sealed class ConfirmationToastWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer;

    internal ConfirmationToastWindow(ConfirmationRequest request, int stackIndex, TaskCompletionSource<bool> answer)
    {
        _answer = answer;

        Title = "AI Home — подтверждение";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        Foreground = Brushes.White;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        foreach (var line in request.Lines)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{line.Label}: {line.Value}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE8))
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = ConfirmationText.Footer,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 14),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0))
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeButton("Отклонить", false, new Thickness(0, 0, 8, 0)));
        buttons.Children.Add(MakeButton("Подтвердить", true, new Thickness(0)));
        body.Children.Add(buttons);

        Content = body;
        Loaded += (_, _) => PlaceBottomRight(stackIndex);
    }

    /// <summary>Закрыть тост снаружи: вызова больше нет, подтверждать нечего.</summary>
    internal void CloseWithReason(string reason)
    {
        _ = reason; // повод показывать негде — он уже ушёл в ленту клиента
        _answer.TrySetResult(false);
        Close();
    }

    private Button MakeButton(string caption, bool confirm, Thickness margin)
    {
        var button = new Button
        {
            Content = caption,
            MinWidth = 118,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = margin,
            IsDefault = confirm,
            IsCancel = !confirm
        };
        button.Click += (_, _) =>
        {
            _answer.TrySetResult(confirm);
            Close();
        };
        return button;
    }

    // Тосты копятся снизу вверх у правого края рабочей области — так их видно все сразу.
    private void PlaceBottomRight(int stackIndex)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = Math.Max(area.Top + 24, area.Bottom - ActualHeight - 24 - stackIndex * (ActualHeight + 12));
    }
}

