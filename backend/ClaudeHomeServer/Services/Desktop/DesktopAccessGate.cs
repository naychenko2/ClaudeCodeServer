using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Исходы гейта исполнения. Отдельно от <see cref="DesktopOutcomes"/>: те описывают, что
/// случилось НА УСТРОЙСТВЕ, а эти — почему вызов до устройства не дошёл. Инструменты
/// desktop_* при любом из них остаются в составе tools/list: отказ — это ответ инструмента,
/// а не изменение сигнатуры запуска CLI (иначе процесс перезапустится со всеми MCP-серверами).
/// </summary>
public static class DesktopGateOutcomes
{
    /// <summary>Чат-вызыватель не найден: удалён или истёк.</summary>
    public const string ChatGone = "chat_gone";

    /// <summary>Грань не выдана: не тот тип чата, выключена в проекте или выключен флаг.</summary>
    public const string FacetOff = "facet_off";

    /// <summary>Сеанса рук нет — заявка поставлена в очередь клиента.</summary>
    public const string NoHandsSession = "no_hands_session";

    /// <summary>В аргументе названо не то устройство, к которому подключены руки чата.</summary>
    public const string DeviceMismatch = "device_mismatch";

    /// <summary>Устройства с таким именем у владельца нет.</summary>
    public const string UnknownDevice = "unknown_device";

    /// <summary>Сеанс уже идёт — на этом устройстве или у этого чата.</summary>
    public const string HandsBusy = "hands_busy";
}

/// <summary>
/// Что гейту нужно знать о чате-вызывателе. Отдельный снимок, а не сама
/// <see cref="Models.Session"/>: гейт спрашивает про конфигурацию грани, а не про чат
/// целиком, и не должен зависеть от полей модели.
/// </summary>
/// <param name="IsDesktopChat">Тип чата «Десктопный» — половина оси выдачи грани.</param>
/// <param name="ProjectFacetEnabled">Тумблер грани в проекте — вторая половина оси.</param>
/// <param name="FlagEnabled">Фич-флаг desktop-agent у владельца.</param>
public sealed record DesktopChatInfo(
    string ChatId,
    string OwnerId,
    string? ProjectId,
    string? ChatName,
    string? ProjectName,
    string? PersonaName,
    bool IsDesktopChat,
    bool ProjectFacetEnabled,
    bool FlagEnabled)
{
    /// <summary>
    /// Почему грань этому чату не выдана, либо null — выдана. Одна формулировка на все
    /// точки: гейт вызова, старт сеанса и сторож проверяют РОВНО одно и то же.
    /// </summary>
    public string? FacetRefusal() =>
        !FlagEnabled ? "Десктопный агент выключен: включите «Десктопный агент» в экспериментальных функциях."
        : !IsDesktopChat ? "Этот чат не десктопный: руки выдаются только чату типа «Десктопный»."
        : ProjectId is null ? "Грань десктопного агента выдаётся только в проекте: у чата вне проектов её нет."
        : !ProjectFacetEnabled ? $"Грань десктопного агента выключена в проекте «{ProjectName ?? ProjectId}»."
        : null;
}

/// <summary>Реестр чатов глазами грани. Реализация — единственное место, где грань знает модель.</summary>
public interface IDesktopChatDirectory
{
    /// <summary>Снимок чата, либо null — чата больше нет (удалён или истёк).</summary>
    DesktopChatInfo? Find(string chatSessionId);
}

/// <summary>Устройство глазами грани: наружу отдаётся ИМЯ, а не GUID.</summary>
public sealed record DesktopDeviceInfo(string Id, string Name, bool Online);

/// <summary>Реестр устройств глазами грани — имена, а не GUID, плюс факт «на связи».</summary>
public interface IDesktopDeviceDirectory
{
    IReadOnlyList<DesktopDeviceInfo> List(string ownerId);
    DesktopDeviceInfo? FindByName(string ownerId, string name);
    DesktopDeviceInfo? FindById(string ownerId, string deviceId);
}

/// <summary>Решение гейта. Отказ несёт исход и текст для человека и модели.</summary>
public sealed record DesktopGateDecision(
    bool Allowed,
    string Outcome,
    string Message,
    DesktopChatInfo? Chat = null,
    DesktopHandsSession? Hands = null,
    DesktopDeviceInfo? Device = null)
{
    public static DesktopGateDecision Refuse(string outcome, string message, DesktopChatInfo? chat = null) =>
        new(false, outcome, message, chat);
}

/// <summary>
/// Гейт исполнения грани десктопа (ADR-008, «Два уровня, которые нельзя смешивать»).
///
/// Состав инструментов фиксирован на момент запуска CLI, а право чата на грань проверяется
/// на КАЖДЫЙ вызов — здесь. Порядок проверок: чат из токена → конфигурация грани (тип чата,
/// тумблер проекта, флаг) → активный сеанс рук этого чата → устройство из аргумента → связь.
///
/// Инвариант авторизации: чат-вызыватель берётся ИЗ capability-токена
/// (<see cref="DesktopCaller"/>). Заголовок X-Caller-Session-Id гейт не читает вообще —
/// он подделывается ходом, и решение на нём строить нельзя.
/// </summary>
public sealed class DesktopAccessGate(
    IDesktopChatDirectory chats,
    IDesktopDeviceDirectory devices,
    DesktopHandsSessionService hands)
{
    /// <summary>
    /// Право на грань БЕЗ сеанса — только для desktop_devices: он рассказывает про
    /// устройства и статус сеанса, в том числе когда сеанса нет (ADR: «Сеанса нет — отказ
    /// со списком устройств», список приходит именно отсюда).
    /// </summary>
    public DesktopGateDecision EvaluateFacet(DesktopCaller caller)
    {
        var chat = chats.Find(caller.SessionId);
        if (chat is null || chat.OwnerId != caller.OwnerId)
            return DesktopGateDecision.Refuse(DesktopGateOutcomes.ChatGone,
                "Чат-вызыватель не найден: возможно, он удалён или истёк.");

        return chat.FacetRefusal() is string refusal
            ? DesktopGateDecision.Refuse(DesktopGateOutcomes.FacetOff, refusal, chat)
            : new DesktopGateDecision(true, DesktopOutcomes.Ok, "", chat);
    }

    /// <summary>
    /// Право на ВЫЗОВ устройства: и на действие, и на чтение — сеанс гейтит оба.
    /// <paramref name="deviceName"/> — человеческое имя из аргумента инструмента; опущено —
    /// берётся устройство сеанса, скрытого «активного устройства» у владельца не существует.
    /// Разрешённый вызов продлевает окно простоя сеанса.
    /// </summary>
    public DesktopGateDecision EvaluateCall(DesktopCaller caller, string? deviceName)
    {
        var facet = EvaluateFacet(caller);
        if (!facet.Allowed) return facet;
        var chat = facet.Chat!;

        var session = hands.ForChat(caller.SessionId);
        if (session is null)
        {
            // Заявка — единственный путь к сеансу: начать его может только человек у машины.
            hands.Enqueue(chat);
            return DesktopGateDecision.Refuse(DesktopGateOutcomes.NoHandsSession,
                $"Сеанс рук не начат. Заявка от чата «{chat.ChatName ?? chat.ChatId}» поставлена в очередь: " +
                $"человек начинает сеанс сам, в приложении AI Home на своём компьютере. {DevicesHint(caller.OwnerId)}",
                chat);
        }

        // Чат из токена совпал с чатом сеанса по построению (сеанс ищется по нему же).
        // Именно поэтому подделанный X-Caller-Session-Id ничего не даёт: он сюда не приходит.
        var device = devices.FindById(caller.OwnerId, session.DeviceId)
                     ?? new DesktopDeviceInfo(session.DeviceId, session.DeviceName, Online: false);

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var asked = devices.FindByName(caller.OwnerId, deviceName);
            if (asked is null)
                return DesktopGateDecision.Refuse(DesktopGateOutcomes.UnknownDevice,
                    $"Устройства «{deviceName.Trim()}» у вас нет. {DevicesHint(caller.OwnerId)}", chat);

            if (asked.Id != session.DeviceId)
                return DesktopGateDecision.Refuse(DesktopGateOutcomes.DeviceMismatch,
                    $"В этом чате руки подключены к {device.Name}, а не к {asked.Name}.", chat);
        }

        if (!device.Online)
            return DesktopGateDecision.Refuse(DesktopOutcomes.DeviceOffline,
                $"Устройство {device.Name} офлайн; {OnlineHint(caller.OwnerId, device.Id)}", chat);

        hands.Touch(session.ChatSessionId);
        return new DesktopGateDecision(true, DesktopOutcomes.Ok, "", chat, session, device);
    }

    // «Ваши устройства: home, work» — имена, а не GUID: их же принимает параметр device.
    private string DevicesHint(string ownerId)
    {
        var all = devices.List(ownerId);
        if (all.Count == 0) return "Устройств у вас пока нет: добавьте компьютер в разделе «Устройства».";
        var online = all.Where(d => d.Online).Select(d => d.Name).ToList();
        return online.Count > 0
            ? $"Устройства онлайн: {string.Join(", ", online)}."
            : $"Все ваши устройства офлайн: {string.Join(", ", all.Select(d => d.Name))}.";
    }

    private string OnlineHint(string ownerId, string exceptDeviceId)
    {
        var online = devices.List(ownerId).Where(d => d.Online && d.Id != exceptDeviceId).Select(d => d.Name).ToList();
        return online.Count > 0 ? $"онлайн: {string.Join(", ", online)}." : "других устройств онлайн нет.";
    }
}

/// <summary>
/// Боевой реестр чатов: единственное место грани, знающее поля модели чата и проекта.
/// Владелец проектного чата резолвится через проект (у Session.OwnerId он null).
/// </summary>
public sealed class DesktopChatDirectory(
    SessionManager sessions,
    ProjectManager projects,
    FeatureFlagService flags,
    PersonaManager? personas = null) : IDesktopChatDirectory
{
    public DesktopChatInfo? Find(string chatSessionId)
    {
        if (string.IsNullOrEmpty(chatSessionId)) return null;
        var chat = sessions.GetById(chatSessionId);
        if (chat is null) return null;

        var project = chat.ProjectId is string pid ? projects.GetById(pid) : null;
        var ownerId = chat.OwnerId ?? project?.OwnerId;
        if (string.IsNullOrEmpty(ownerId)) return null;

        var persona = chat.PersonaId is string personaId ? personas?.Get(personaId, ownerId) : null;

        return new DesktopChatInfo(
            chat.Id,
            ownerId,
            project?.Id,
            chat.Name,
            project?.Name,
            persona?.Name,
            chat.DesktopChat,
            project?.DesktopAgentEnabled ?? false,
            flags.IsEnabled(ownerId, Models.FeatureFlagKeys.DesktopAgent));
    }
}

/// <summary>
/// Боевой реестр устройств: метаданные из стора, «на связи» — из живых соединений канала.
/// Отозванные устройства грани не видны вовсе.
/// </summary>
public sealed class DesktopDeviceDirectory(DeviceRegistry registry, DesktopCallRouter router) : IDesktopDeviceDirectory
{
    public IReadOnlyList<DesktopDeviceInfo> List(string ownerId) =>
        registry.GetByOwner(ownerId).Select(d => Map(ownerId, d.Id, d.Name)).ToList();

    public DesktopDeviceInfo? FindByName(string ownerId, string name) =>
        registry.FindByName(ownerId, name) is { } d ? Map(ownerId, d.Id, d.Name) : null;

    public DesktopDeviceInfo? FindById(string ownerId, string deviceId) =>
        registry.Get(ownerId, deviceId) is { } d ? Map(ownerId, d.Id, d.Name) : null;

    private DesktopDeviceInfo Map(string ownerId, string id, string name) =>
        new(id, name, router.IsOnline(ownerId, id));
}
