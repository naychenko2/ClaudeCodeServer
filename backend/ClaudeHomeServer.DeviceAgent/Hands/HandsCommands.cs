namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Справка <c>ai-home-agent hands status</c>: на месте ли мост рук этой копии агента и какой он
/// версии. Ставить и убирать нечего — мост едет в составе агента; <c>enable|disable</c> только
/// объясняют это тем, кто пришёл по старой инструкции.
/// </summary>
internal sealed class HandsCommands(HandsComponent component, TextWriter output, TextWriter error, bool handsSupported)
{
    public const string Usage = "ai-home-agent hands status";

    public const string NoMachineSwitchText =
        "Команды больше нет: мост рук едет в составе агента, а включаются и выключаются руки тумблером в настройках проекта.";

    public int Run(string[] args) => args switch
    {
        ["status"] or [] => Status(),
        ["enable"] or ["disable"] => Fail(NoMachineSwitchText),
        _ => Fail(Usage, 64),
    };

    private int Status()
    {
        if (!handsSupported) return Fail(HandsAttach.UnsupportedText);
        var check = component.Check();
        if (!check.Ready) return Fail(check.Problem ?? HandsComponent.MissingText);
        output.WriteLine($"Мост рук на месте: {component.BridgePath}");
        output.WriteLine($"Версия моста: {check.Version ?? "не указана"}");
        return 0;
    }

    private int Fail(string message, int code = 1)
    {
        error.WriteLine(message);
        return code;
    }
}
