using System.Reflection;

namespace AiHomeDesktop.App.Shell;

/// <summary>Состояние связи с сервером — то, что человек видит в строке состояния.</summary>
public enum ChannelState
{
    /// <summary>Устройство ещё не сопряжено: канала нет и быть не может.</summary>
    NotPaired,

    /// <summary>Канал поднимается или переподключается. Это ШТАТНОЕ состояние, а не авария.</summary>
    Connecting,

    /// <summary>Канал на связи: команды дойдут.</summary>
    Connected,

    /// <summary>Сервер не признаёт токен устройства — его отозвали. Нужно сопрягаться заново.</summary>
    Revoked
}

/// <summary>
/// Снимок состояния связи. Текст живёт рядом с состоянием: строка состояния — это весь
/// ответ человеку на вопрос «почему руки не работают», и разъехаться им нельзя.
/// </summary>
/// <param name="State">Машинное состояние — по нему решают, что показывать.</param>
/// <param name="Text">Короткая подпись индикатора.</param>
/// <param name="Detail">Пояснение: что это значит и что делать.</param>
public sealed record ChannelStatus(ChannelState State, string Text, string? Detail = null)
{
    public static readonly ChannelStatus NotPaired = new(
        ChannelState.NotPaired, "Не сопряжено",
        "Клиент не связан с сервером AI Home: пройдите сопряжение кодом.");

    public static readonly ChannelStatus Connecting = new(
        ChannelState.Connecting, "Переподключаемся",
        "Связь с сервером потеряна. Клиент восстанавливает её сам — это штатное состояние, " +
        "команды подождут.");

    public static ChannelStatus Connected(string deviceName) => new(
        ChannelState.Connected, "На связи",
        $"Сервер видит это устройство как «{deviceName}».");

    public static readonly ChannelStatus Revoked = new(
        ChannelState.Revoked, "Устройство отозвано",
        "Сервер больше не признаёт токен этого устройства. Выпустите новый код сопряжения " +
        "в веб-морде и сопрягите клиент заново.");
}

/// <summary>Кто мы для сервера: версия уезжает в Hello и в карточку устройства.</summary>
public static class ClientInfo
{
    /// <summary>Версия клиента. Пусто быть не может — сервер пишет её в реестр устройств.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
}
