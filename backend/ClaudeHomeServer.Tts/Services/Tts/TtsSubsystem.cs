using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Tts;

// Подсистема озвучки голосового режима чата: синтез речи через Яндекс SpeechKit v3
// (`YandexTtsService`) и ЕДИНСТВЕННАЯ точка склейки голоса персоны/инстанса
// (`VoiceResolver`). Контракт фолбэка на голос браузера живёт в контроллере
// (`TtsController` отвечает 503 `not_configured` / 502 `upstream` — там и только там).
//
// Известные границы (сознательные обратные стрелки «спина → каталог вертикали»,
// аналог `FavoriteVideoChannels` у Video, НЕ чинить в этой задаче):
// - `TtsVoiceCatalog` — статический класс, его используют СНАРУЖИ вертикали:
//   `PersonaManager:~668` (`Canonical` при загрузке персон) и `PersonasController`
//   (валидация голоса, место `persona-voice`). Сознательная обратная стрелка:
//   каталог голосов — это БЕЛЫЙ СПИСОК, разделяемый между подсистемой озвучки и
//   формой выбора голоса в персоне; вынос в отдельную вертикаль не нужен и
//   только размножит зависимости.
// - `VoiceResolver` зависит от `PersonaManager` (Services/ корень) — это связь
//   «вертикаль → спинка», легально.
//
// Запись `chat-voice` в `LocalActionCatalog` (голосовой режим чата, локальный
// исполнитель) — это LLM-вертикаль (цикл hands-free и локальные one-shot ходы),
// к Tts-вертикали не относится.
//
// ⚠ Инвариант клиента: SpeechKit — внешний сервис за DPI, идёт ЧЕРЕЗ egress-прокси.
// `WithoutEgressProxy()` тут НЕ звать — без прокси API просто не отвечает
// (как у `fal-ai`, `glif`, биллинга Яндекса).
public sealed class TtsSubsystem : IAppSubsystem
{
    public string Key => "tts";

    public string Title => "Озвучка (Яндекс SpeechKit)";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<YandexTtsService>();

        // Единственная точка склейки голоса (персона → конфиг). Singleton: дефолты
        // инстанса читаются один раз, поэтому предупреждение об опечатке в голосе
        // не сыплется на каждую фразу.
        services.AddSingleton<VoiceResolver>();

        // Внешний сервис (Яндекс SpeechKit) — через egress-прокси, WithoutEgressProxy НЕ звать.
        services.AddQuietHttpClient(
            YandexTtsService.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Tts.Yandex",
                Subject: "синтезом речи Yandex SpeechKit",
                Consequence: "Озвучка ответов переключится на голос браузера."));
    }
}
