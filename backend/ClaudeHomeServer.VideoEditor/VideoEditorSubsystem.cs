using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.VideoEditor;

// Модуль «Видео» — динамический модуль (ADR-022 §1): грузится ModuleLoader'ом по записи
// DynamicModules[videoeditor], Main его типов не видит. Выключается двумя способами:
// DynamicModules[videoeditor].Enabled=false (dll не грузится) или
// Subsystems:VideoEditor:Enabled=false (Register не вызывается). В обоих случаях ручек нет — 404.
//
// Модуль ссылается только на Core: всё внешнее — швы оттуда, реализации регистрируют другие сборки.
public sealed class VideoEditorSubsystem : IAppSubsystem
{
    public string Key => "videoeditor";

    public string Title => "Видео";

    public string Description => "Сцены между двумя кадрами и фильм из них: съёмка, версии клипов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
    }
}
