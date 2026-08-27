using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

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
/// Панель сеанса в строке состояния окна: очередь заявок, «Начать сеанс» и «Стоп».
///
/// Кнопка старта живёт ТОЛЬКО здесь (ADR-008): веб-морда может лишь попросить, и просьба
/// приходит сюда заявкой с именем чата, проекта и персоны. Безымянной кнопки «начать» не
/// бывает — человек всегда выбирает конкретный чат.
///
/// «Стоп» идёт мимо канала агента: он зовёт эндпоинт сеанса, а не просит модель
/// остановиться. Собрана панель кодом, без XAML: одна форма и три состояния.
/// </summary>
internal sealed class HandsPanel : UserControl
{
    private readonly HandsSessionManager _session;
    private readonly StackPanel _root = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _clock;

    internal HandsPanel(HandsSessionManager session, HandsActivityFeed feed)
    {
        _session = session;
        Content = _root;

        session.Changed += () => Dispatcher.BeginInvoke(Redraw);
        // Лента меняется чаще панели, но подсказка «последнее, что ушло в модель» живёт здесь:
        // отдельного окна ленты в этой версии нет.
        feed.Changed += () => Dispatcher.BeginInvoke(() => ToolTip = LastLine(feed));

        // Отсчёт до предела идёт по серверному expiresAt — тикаем раз в секунду, чтобы
        // человек видел, сколько осталось, а не узнавал о погасании постфактум.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Redraw(), Dispatcher);
        _clock.Start();

        Redraw();
    }

    private void Redraw()
    {
        _root.Children.Clear();

        if (_session.Session is { } active)
        {
            _root.Children.Add(Text($"Руки у чата «{active.ChatTitle}»", bold: true));
            _root.Children.Add(Text(_session.CountdownText(), muted: true));
            _root.Children.Add(Action("Стоп", async () => await _session.StopAsync()));
            return;
        }

        var requests = _session.Requests;
        if (requests.Count == 0)
        {
            _root.Children.Add(Text("Сеанса нет: чат попросит руки — заявка появится здесь", muted: true));
            return;
        }

        var first = requests[0];
        var subtitle = string.IsNullOrWhiteSpace(first.Subtitle) ? "" : $" · {first.Subtitle}";
        _root.Children.Add(Text($"Заявка: «{first.ChatTitle}»{subtitle}", bold: true));
        if (requests.Count > 1) _root.Children.Add(Text($"и ещё {requests.Count - 1}", muted: true));
        _root.Children.Add(Action("Начать сеанс", async () =>
        {
            var outcome = await _session.StartAsync(first.ChatSessionId);
            // Отказ сервера показываем как есть: причин ровно столько, сколько он назвал.
            if (!outcome.Started) ToolTip = outcome.Message;
        }));
    }

    private static string? LastLine(HandsActivityFeed feed) =>
        feed.Entries.Count == 0 ? null : $"{feed.Entries[0].ChatTitle}: {feed.Entries[0].Text}";

    private static TextBlock Text(string text, bool bold = false, bool muted = false) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 13,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Opacity = muted ? 0.65 : 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 360
    };

    private static Button Action(string caption, Func<Task> handler)
    {
        var button = new Button
        {
            Content = caption,
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await handler(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}
