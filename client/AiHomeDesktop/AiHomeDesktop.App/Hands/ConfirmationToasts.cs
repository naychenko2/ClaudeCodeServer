using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Policies;

// WPF и WinForms делят имена типов (иконка трея тянет WinForms): здесь интерфейс
// только на WPF, поэтому имена разводим явно.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Тосты подтверждения на рабочем столе. Один вызов — один тост; текст берётся из
/// <see cref="ConfirmationText"/> ядра, то есть из ФАКТИЧЕСКИХ аргументов вызова:
/// модельного резюме человек не видит никогда.
///
/// Закрытие тоста крестиком — это отказ, а не «спросим потом»: молчание не повод
/// действовать. Истёкшее окно ожидания — другое дело: там ответа не было вовсе, и исход
/// у него свой (<see cref="ConfirmationDecision.NoAnswer"/>).
///
/// Отмена вызова сервером (погас сеанс, нажали «Стоп») закрывает тост снаружи через
/// <see cref="Close"/> — человек не должен подтверждать то, чего уже нет.
/// </summary>
public sealed class ConfirmationToasts(Dispatcher dispatcher) : IConfirmationUi
{
    /// <summary>
    /// Приписка внизу тоста. Обещаний безопасности в ней нет — их и не существует:
    /// единственный предохранитель здесь человек, и после первого одобрения он деградирует.
    /// </summary>
    public const string Footer = "После подтверждения действие выполнится на этом компьютере от вашего имени.";

    private readonly ConcurrentDictionary<string, ConfirmationToastWindow> _open = new(StringComparer.Ordinal);

    /// <summary>Тост живёт под своим callId: по нему же его гасит отмена сервера.</summary>
    public async Task<ConfirmationDecision> AskAsync(
        ConfirmationPrompt prompt, TimeSpan wait, CancellationToken ct)
    {
        var callId = prompt.CallId;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            var toast = new ConfirmationToastWindow(prompt, _open.Count, answer);
            _open[callId] = toast;
            toast.Closed += (_, _) =>
            {
                _open.TryRemove(callId, out _);
                // Окно закрыли, не ответив, — считаем отказом.
                answer.TrySetResult(false);
            };
            toast.Show();
        });

        await using var cancellation = ct.Register(() => Close(callId, "вызов отменён"));

        // Ожидание кончилось — тост гасим сами: висящий вопрос по мёртвому вызову хуже,
        // чем его отсутствие. Это НЕ отказ: человек не отвечал.
        var timeout = wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var completed = await Task.WhenAny(answer.Task, timeout).ConfigureAwait(false);
        if (completed != answer.Task)
        {
            Close(callId, "человек не ответил");
            return ConfirmationDecision.NoAnswer;
        }

        return await answer.Task.ConfigureAwait(false)
            ? ConfirmationDecision.Confirmed
            : ConfirmationDecision.Declined;
    }

    /// <summary>Закрыть висящий тост снаружи: вызова больше нет, подтверждать нечего.</summary>
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

    internal ConfirmationToastWindow(ConfirmationPrompt prompt, int stackIndex, TaskCompletionSource<bool> answer)
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
            Text = prompt.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        foreach (var line in prompt.Lines) body.Children.Add(Line(line, Color.FromRgb(0xE6, 0xE6, 0xE8)));

        // Чат — всегда: тост без чата не отвечает на вопрос «кто просит».
        body.Children.Add(Line(prompt.ChatLine, Color.FromRgb(0xE6, 0xE6, 0xE8)));

        body.Children.Add(new TextBlock
        {
            Text = ConfirmationToasts.Footer,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 14),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
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

    private static TextBlock Line(string text, Color color) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
        Foreground = new SolidColorBrush(color)
    };

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
