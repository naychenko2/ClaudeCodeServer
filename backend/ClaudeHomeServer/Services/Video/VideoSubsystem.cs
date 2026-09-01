using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Video;

// Подсистема раздела «Видео» (эфиры телеканалов СМОТРИМ + лента подписок YouTube).
//
// Известная граница: `Models/User.FavoriteVideoChannels` (избранные каналы пользователя)
// и методы `UserStore.GetFavoriteVideoChannels` / `SetFavoriteVideoChannels` остаются
// в модели пользователя — вынос в собственный стор подсистемы сменил бы формат
// `users.json` и потребовал бы инкремента `BackupSchema.Version`. Это отдельное решение
// (не пилот), здесь зафиксировано как сознательная граница.
//
// ⚠ Инвариант клиентов (см. CLAUDE.md, раздел «Раздел „Видео“»): СМОТРИМ — российский
// сервис, идёт БЕЗ egress-прокси (`WithoutEgressProxy`); YouTube, наоборот, ЧЕРЕЗ
// egress-прокси (без `WithoutEgressProxy`). Перепутать = тихо сломать один из источников.
public sealed class VideoSubsystem : IAppSubsystem
{
    public string Key => "video";

    public string Title => "Видео";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(VideoOptions.FromConfig(config));
        services.AddSingleton<YouTubeOAuthService>();
        services.AddSingleton<IVideoProvider, SmotrimProvider>();
        services.AddSingleton<IVideoProvider, YouTubeProvider>();
        services.AddSingleton<VideoProviderRegistry>();

        // СМОТРИМ — РОССИЙСКИЙ сервис: egress-прокси ему противопоказан. Чужое API лежит
        // штатно — тихий клиент вместо дефолтного, иначе на каждый отказ по карточке канала
        // печатается стектрейс.
        services.AddQuietHttpClient(
            SmotrimProvider.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Video.Smotrim",
                Subject: "сервисом СМОТРИМ",
                Consequence: "Программа передач и признак доступности каналов не обновятся."))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .WithoutEgressProxy();

        // YouTube — за DPI: идёт ЧЕРЕЗ egress-прокси (WithoutEgressProxy тут НЕ звать).
        // Через прокси едут только метаданные — сам видеопоток идёт из браузера.
        services.AddQuietHttpClient(
            YouTubeOAuthService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Video.YouTube",
                Subject: "YouTube Data API",
                Consequence: "Лента подписок не обновится; на эфиры телеканалов это не влияет."))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
    }
}
