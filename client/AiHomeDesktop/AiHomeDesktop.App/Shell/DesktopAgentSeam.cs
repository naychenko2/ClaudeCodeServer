using System.Windows;
using System.Windows.Controls;
using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.App.Shell;

/// <summary>
/// Что оболочка отдаёт сеансу рук: окно, место под его панель и индикатор в трее.
/// Оболочка не знает про сеанс ничего — она держит канал и интерфейс, а очередь заявок,
/// тосты подтверждения и грань исполнения живут по ту сторону шва.
/// </summary>
public interface IShellSurface
{
    /// <summary>Окно клиента: владелец тостов подтверждения и всего, что показывается человеку.</summary>
    Window Window { get; }

    /// <summary>
    /// Место в строке состояния под панель сеанса рук: «Начать сеанс», очередь заявок,
    /// «Стоп». Кнопки старта у оболочки нет намеренно — сеанс стартует только с устройства,
    /// и эта дверь одна.
    /// </summary>
    ContentControl HandsHost { get; }

    /// <summary>Индикатор активного сеанса на иконке в трее.</summary>
    void SetHandsActive(bool active);

    /// <summary>Поднять окно из трея: пришла заявка — человек обязан её увидеть.</summary>
    void ShowWindow();

    /// <summary>Всплывающая подсказка от трея (заявка на сеанс, обрыв, отзыв устройства).</summary>
    void Notify(string title, string text);
}

/// <summary>Всё, что нужно сеансу рук, чтобы жить поверх поднятого канала.</summary>
/// <param name="Api">HTTP-половина канала: результаты вызовов, заявки и сеанс рук.</param>
/// <param name="Journal">Локальный журнал вызовов по callId.</param>
/// <param name="Channel">Донесения в хаб: Ack, Awaiting, Confirm, Decline, Progress.</param>
/// <param name="Credentials">Учётные данные устройства (адрес сервера, имя, отпечаток).</param>
/// <param name="Shell">Поверхность оболочки: окно, место под панель, трей.</param>
public sealed record DesktopAgentContext(
    DeviceApi Api,
    CallJournal Journal,
    IDeviceChannel Channel,
    DeviceCredentials Credentials,
    IShellSurface Shell);

/// <summary>
/// Шов между оболочкой (окно, трей, сопряжение, канал) и сеансом рук (очередь заявок,
/// тосты подтверждения, исполнение вызовов).
///
/// Как подключиться, не трогая файлы оболочки: поставить <see cref="Compose"/> из
/// <c>[ModuleInitializer]</c> своего модуля. Оболочка зовёт его один раз — сразу после
/// того, как канал создан, но ДО его подъёма, и передаёт готовый
/// <see cref="DesktopAgentContext"/>; вернуть надо обработчик команд канала (обычно
/// <c>DesktopCallCoordinator</c> из ядра).
///
/// Шов не зарегистрирован — это рабочее состояние сборки, а не ошибка: канал поднимется,
/// устройство будет видно в /api/devices как на связи, а на команду ответит честным
/// отказом «эта сборка клиента вызовы не исполняет». Молчания в ответ на команду не
/// бывает никогда: сервер ждёт ack две секунды, дальше вызов кончается ошибкой у модели.
/// </summary>
public static class DesktopAgentSeam
{
    /// <summary>Собрать обработчик команд канала. null — сборка без грани исполнения.</summary>
    public static Func<DesktopAgentContext, IDeviceCallHandler>? Compose { get; set; }

    /// <summary>
    /// Оболочка закрывается по-настоящему (выход из трея), повод —
    /// <see cref="DesktopHandsStopReasons.ClientClosed"/>. Жизнь в трее закрытием НЕ
    /// считается и сюда не приходит: сеанс от сворачивания окна не гаснет.
    /// </summary>
    public static Action<string>? ShellClosing { get; set; }
}
