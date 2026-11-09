using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Выбор генератора картинок инстанса ПО МЕСТАМ: «Иконка проекта» и «Аватар персоны»
// настраиваются отдельно — режим auto|fal|glif и модель на каждого провайдера. Логики
// генерации здесь нет — только чтение и запись настройки, всё остальное делает
// ImageGenerationService.
//
// Настройка общая для всех (как ClaudeBilling в /api/settings), поэтому запись — админам.
// Чтение открыто любому авторизованному: диалоги иконки и аватара подписывают, кто и какой
// моделью сгенерирует картинку, — без GET подпись не отрисовать.
[ApiController]
[Authorize]
[Route("api/image-generation")]
[SubsystemUsage("images")]
public class ImageGenerationController(
    // `= null` — обязательность для DI: без значения по умолчанию активация контроллера
    // на выключенной вертикали падала (500), а не отвечала гейтом (та же форма, что TtsController)
    ImageGenerationService? images = null) : ControllerBase
{
    // Гейт выключенной вертикали (ADR-014, S2b-1): null-зависимость = подсистема выключена
    // (её сервисы регистрируются только при открытом гейте). Отказ — до любой работы.
    private ActionResult ImagesDisabled() => StatusCode(503, new
    {
        error = "Подсистема генерации изображений выключена (Subsystems:images:Enabled=false)",
    });

    // Патч-семантика PUT /api/settings: null — поле не прислали (оставить как есть),
    // "" — сброс к слою ниже (конфиг Images → дефолт драйвера). Места, которых нет в
    // Places, не трогаются вовсе.
    public record PlacePatch(string? Provider, Dictionary<string, string?>? Models);
    public record UpdateRequest(Dictionary<string, PlacePatch>? Places);

    [HttpGet]
    public IActionResult Get()
    {
        if (images is null) return ImagesDisabled();
        return Ok(Describe(images));
    }

    [HttpPut]
    [Authorize(Roles = "admin")]
    public IActionResult Update([FromBody] UpdateRequest req)
    {
        if (images is null) return ImagesDisabled();
        if (req.Places is null || req.Places.Count == 0)
            return BadRequest(new { error = "Не указано ни одного места генерации" });

        // Сначала валидируем ВСЕ места, потом применяем: иначе запрос с одной опечаткой
        // сохранился бы наполовину.
        foreach (var (place, patch) in req.Places)
            if (images.Validate(place, patch?.Provider, patch?.Models) is { } invalid)
                return BadRequest(new { error = invalid });

        foreach (var (place, patch) in req.Places)
            images.UpdateSettings(place, patch?.Provider, patch?.Models);

        return Ok(Describe(images));
    }

    // Форма настройки целиком: каталог провайдеров и по строке на каждое место.
    // Один и тот же ответ у GET и PUT — фронту не нужно перечитывать после записи.
    // Сервис — параметр: вызывающие уже прошли гейт `images is null`, и здесь он живой.
    private object Describe(ImageGenerationService images) => new
    {
        providers = images.Providers.Select(g => new
        {
            key = g.Key,
            displayName = g.DisplayName,
            // Настроен (есть ключ/токен) и может генерировать
            enabled = g.Enabled,
            models = g.Models,
        }),
        places = ImagePlaces.All.Select(place =>
        {
            var active = images.ActiveProviderFor(place);
            return new
            {
                key = place,
                title = ImagePlaces.TitleOf(place),
                // Режим места: auto | ключ провайдера
                provider = images.ModeFor(place),
                // Кто пойдёт следующим запросом; null — генерация в этом месте недоступна
                activeProvider = active?.Key,
                enabled = active is not null,
                // Эффективная модель активного провайдера (настройка → конфиг → дефолт драйвера)
                model = active is null ? null : images.ModelFor(place, active.Key),
                // Эффективные модели по каждому провайдеру — для пикера
                models = images.Providers.ToDictionary(g => g.Key, g => images.ModelFor(place, g.Key)),
            };
        }),
    };
}
