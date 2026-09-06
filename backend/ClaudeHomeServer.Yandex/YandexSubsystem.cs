using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Yandex;

// Подсистема раздела «Яндекс (биллинг)»: остаток на биллинг-аккаунте Yandex Cloud
// (Billing API v1) и обмен авторизованного ключа сервисного аккаунта на IAM-токен
// (Billing API принимает только IAM-токен, а живёт он 12 часов).
//
// Известные границы:
// - `Services/Tts/YandexTtsService` — ДРУГАЯ вертикаль (Tts), делит только бренд
//   (SpeechKit ходит по Api-Key, а не по IAM-токену, и стартует из Tts-подсистемы).
// - `Controllers/YandexController` дополнительно читает `SpendAnalyticsService` (Spend)
//   — это связь контроллера, не подсистемы: на уровне DI контроллер сам знает оба
//   сервиса, границу между Yandex и Spend он не нарушает.
//
// ⚠ Инвариант клиента: Яндекс-биллинг — внешний сервис за DPI, идёт ЧЕРЕЗ egress-прокси.
// `WithoutEgressProxy()` тут НЕ звать — без прокси Billing API просто не отвечает.
public sealed class YandexSubsystem : IAppSubsystem
{
    public string Key => "yandex";

    public string Title => "Яндекс (биллинг)";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<YandexIamTokenProvider>();
        services.AddSingleton<YandexAccountService>();

        // Внешний сервис (Яндекс Cloud) — через egress-прокси, WithoutEgressProxy НЕ звать.
        services.AddQuietHttpClient(
            YandexIamTokenProvider.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Billing.Yandex",
                Subject: "биллингом Yandex Cloud",
                Consequence: "Остаток на счёте не показывается; на озвучку и её учёт это не влияет."));
    }
}