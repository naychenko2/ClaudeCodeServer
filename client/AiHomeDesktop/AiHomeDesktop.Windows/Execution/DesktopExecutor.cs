using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Windows.Execution;

/// <summary>
/// Грань исполнения этой версии клиента — РОВНО ДВА инструмента: кадр (desktop_screen) и
/// открытие цели из allow-list (desktop_open).
///
/// desktop_ui, desktop_act и desktop_run сюда не приходят исполненными: <see cref="Supports"/>
/// отвечает «нет», и координатор возвращает модели честный исход вместо молчания. Состав
/// tools/list при этом НЕ меняется — он входит в сигнатуру запуска CLI, и его зависимость от
/// возможностей устройства перезапускала бы процесс со всеми MCP-серверами. Что умеет клиент,
/// сервер узнаёт из версии протокола в Hello.
/// </summary>
public sealed class DesktopExecutor(
    Func<OpenAllowList> allowList,
    FrameBudget? budget = null) : IDesktopExecutor
{
    private readonly ScreenCall _screen = new(budget ?? FrameBudget.Default);
    private readonly OpenCall _open = new(allowList);

    public bool Supports(string kind) => DesktopCallKinds.IsSupportedByClient(kind);

    public Task<DeviceCallResultBody> ExecuteAsync(
        DesktopCallCommand command, IProgress<int>? progress, CancellationToken ct)
    {
        if (!Supports(command.Kind))
            return Task.FromResult(DeviceCallResultBody.Refused(
                DesktopOutcomes.ProtocolError, DesktopClientOutcomeText.NotSupported(command.Kind)));

        // GDI и оболочка Windows синхронны: уводим их с потока канала, иначе очередь
        // сообщений хаба ждала бы съёмку кадра.
        return Task.Run(() =>
        {
            var result = command.Kind switch
            {
                DesktopCallKinds.Screen => _screen.Execute(command.Args, ct),
                DesktopCallKinds.Open => _open.Execute(command.Args, ct),
                _ => DeviceCallResultBody.Refused(
                    DesktopOutcomes.ProtocolError, DesktopClientOutcomeText.NotSupported(command.Kind))
            };

            // Донесение о применённом шаге: при обрыве или дедлайне сервер иначе не узнает,
            // что на машине уже что-то произошло.
            if (result.LastAppliedStep > 0) progress?.Report(result.LastAppliedStep);
            return result;
        }, ct);
    }
}
