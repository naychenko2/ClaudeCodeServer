using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Controllers;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Регистрация фильма, сборки и подписок на соседей (ADR-022 §2–§4): одна строка в VideoEditorSubsystem. Швы к
// отключаемым вертикалям (IVideoDsp, IImageFrameSource, IAudioTrackSource, IMediaEvents) — необязательные
// параметры конструкторов: нет Images, «Картинок» или «Звука» — отвечает dsp_unavailable / provider_unavailable,
// а не падает.
internal static class FilmRegistration
{
    public static IServiceCollection AddFilms(this IServiceCollection services)
    {
        // Файл .film с атомарной записью под ревизией и состояние вне файла (траты, пометки, музыка в ожидании)
        services.AddSingleton<FilmStore>();
        services.AddSingleton(sp => FilmSideStore.FromConfig(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<FilmService>();
        services.AddSingleton<FilmSceneSaver>();
        // Сборка: реестр заявок — потолок модуля поверх слота общего BuildConcurrencyGate (шов IVideoDsp)
        services.AddSingleton<FilmBuildRegistry>();
        services.AddSingleton<FilmAssembler>();
        // Подписка на новые версии нитей «Картинок» и «Звука» только событием хаба
        services.AddSingleton<FilmFrameFollower>();
        services.AddSingleton<FilmMusicComposer>();
        services.AddHostedService<FilmMediaSubscriber>();
        return services;
    }
}
