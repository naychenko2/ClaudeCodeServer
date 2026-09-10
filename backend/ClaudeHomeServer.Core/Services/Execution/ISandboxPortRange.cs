namespace ClaudeHomeServer.Services.Execution;

// Пул preview-портов docker-песочницы: DevServerService выбирает свободный порт
// для опубликованного сервиса из диапазона, выделенного в секции Sandbox:PortRangeStart/Size.
// Конкретные значения настраиваются в `Sandbox:PortRangeStart`/`Sandbox:PortRangeSize`
// (см. `SandboxOptions.FromConfig`), читает их `SandboxManager` в Main, а вертикали
// получают только узкий диапазон через этот Core-интерфейс.
//
// Шов для ProjectServices (Этап 5, волна C, шаг 2): прежде DevServerService брал
// `Execution.SandboxManager` напрямую из Main, и ProjectServices физически не мог
// выехать в отдельный .csproj (полная зависимость от песочницы, при том что
// сама песочница ещё не в Core). Вынесено в Core, реализация — адаптер
// `Services/Execution/SandboxPortRangeAdapter.cs` в Main.
public interface ISandboxPortRange
{
    int PortRangeStart { get; }
    int PortRangeSize { get; }
}
