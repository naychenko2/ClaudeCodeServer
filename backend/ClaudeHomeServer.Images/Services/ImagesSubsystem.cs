using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images.Editing;
using ClaudeHomeServer.Services.Images.LocalMedia;

namespace ClaudeHomeServer.Services.Images;

// Подсистема раздела «Генерация картинок» (иконка проекта, аватар персоны,
// фон проекта): драйверы fal/glif, настройка по местам, роутер и догоняющая генерация.
//
// Известные границы (сознательно НЕ переезжают в подсистему):
// - HTTP-клиенты "fal"/"glif" живут в Program.cs: это ОБЩИЕ клиенты с биллинг-сервисами
//   `FalAccountService` / `FalCostService` / `GlifAccountService` (срез «деньги», а не
//   вертикаль генерации). Перенос клиентов сюда отрезал бы биллинг от их настроек.
// - `Services/FalImageService.cs` лежит в корне Services/ (namespace `ClaudeHomeServer.Services`),
//   а не в `Services/Images/`. Перенос файла сменил бы namespace у всех вызывающих — это
//   отдельное решение, чтобы дифф оставался маленьким.
// - `Services/ImageAssetHelper.cs` (тоже корень Services/) — общая инфраструктура для работы
//   с ассетами картинок, используется и другими разделами; в подсистему не переезжает.
//
// Свой Testing-гейт внутри `ImageBackfillHostedService.cs:20-24` оставлен как есть:
// замена на `AddGatedHostedService` буквально идентична по поведению (Testing без флага
// → hosted-сервис не выполняет работу), но отличается по DI-графу — `AddGatedHostedService`
// не регистрирует сервис вовсе, а существующий вариант регистрирует и сразу выходит из
// `ExecuteAsync`. Менять означает трогать тесты, которые могут резолвить hosted-сервис
// или проверять сам факт регистрации — не стоит расширять дифф заявленной границы.
public sealed class ImagesSubsystem : IAppSubsystem
{
    public string Key => "images";

    public string Title => "Генерация картинок";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // `FalImageService` регистрируется внутри `AddImageGeneration` как драйвер
        // (`AddImageDriver<FalImageService>()`) — отдельный AddSingleton дал бы второй
        // экземпляр того же типа.
        services.AddImageGeneration();
        // Редактор картинок (ADR-017): драйверы правки, исполнитель задач, сохранение, траты
        services.AddImageEditor();
        // Локальная генерация через ComfyUI (MCP-сервер local-media): тумблер LocalMedia:Enabled
        services.AddLocalMedia(config);
    }
}
