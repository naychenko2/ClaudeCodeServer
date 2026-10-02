using ClaudeHomeServer.Services.Media;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Подписка «Видео» на события соседей через хаб IMediaEvents (ADR-014: только событие или явный шов): новая версия
// нити картинок двигает кадры сцен, новая версия нити звука — музыку фильма. Hosted service ради одного: подписка
// обязана жить с первого события, а не с первого резолва служб. Нет хаба (тесты без DI) — молча ничего не делает.
public sealed class FilmMediaSubscriber(
    FilmFrameFollower frames,
    FilmMusicComposer music,
    IMediaEvents? events = null) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (events is null || _subscriptions.Count > 0) return Task.CompletedTask;
        _subscriptions.Add(events.Subscribe<ImageVersionAdded>(frames.OnImageVersionAsync));
        _subscriptions.Add(events.Subscribe<AudioVersionAdded>(music.OnAudioVersionAsync));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }
}
