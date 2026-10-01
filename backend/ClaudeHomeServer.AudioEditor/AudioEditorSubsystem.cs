using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.AudioEditor;

// Модуль «Звук» — динамический модуль (ADR-021 §1): грузится ModuleLoader'ом по записи
// DynamicModules[audioeditor], Main его типов не видит. Выключается двумя способами:
// DynamicModules[audioeditor].Enabled=false (dll не грузится) или
// Subsystems:AudioEditor:Enabled=false (Register не вызывается). В обоих случаях ручек нет — 404.
//
// Модуль ссылается только на Core: всё внешнее — швы оттуда, реализации регистрируют другие сборки.
// Пока это скелет: сервисов и ручек нет, регистрируется только сама подсистема.
public sealed class AudioEditorSubsystem : IAppSubsystem
{
    public string Key => "audioeditor";

    public string Title => "Звук";

    public string Description => "Озвучка, музыка и правка звука проекта, версии файлов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
    }
}
