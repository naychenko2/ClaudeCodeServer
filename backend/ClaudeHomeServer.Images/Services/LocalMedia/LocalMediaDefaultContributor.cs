using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Правило «локальная модель по умолчанию» для картинок и видео (флаг local-media-default,
// план 876d395f, разрез 7b663adb). Показ зависит только от РЕГИСТРАЦИИ локальных моделей, а не
// от живости ComfyUI: Available мигает и менял бы текст хвоста от хода к ходу; о недоступности
// агент узнаёт из ответа инструмента. Ходы без человека (исполнители, автоматика, делегированные)
// правило не получают — иначе ночные исполнители займут общую GPU.
//
// Типов модуля ImageEditor секция не знает (правило границ): флаг редактора и его тумблер
// запуска агентом читаются по строковым ключам. Внутри варианта текст стабилен — едет хвостом
// хода, но и там незачем гонять разный текст.
public sealed class LocalMediaDefaultContributor(IFeatureFlagGate flags, IConfiguration config) : IPromptSectionContributor
{
    public const string SectionKey = "local-media-default";

    // Тумблер модуля редактора: без него у агента нет image_generate (ImageEditorToolset.AgentLaunchKey)
    private const string EditorAgentLaunchKey = "ImageEditor:AgentLaunch";

    public string Key => SectionKey;
    public string Title => "Локальная модель по умолчанию";
    // Сразу после блока «Картинки в этом чате» (700): правило на него ссылается
    public int Order => 710;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.LocalMediaDefault)
        && !sessionContext.Unattended
        && LocalMediaOptions.IsEnabled(config)
        && SubsystemGate.IsEnabled(config, "images")
        && (sessionContext.Session.ProjectId is null
            ? flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor) && config.GetValue(EditorAgentLaunchKey, true)
            : sessionContext.HasLocalMediaMcp);

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var text = sessionContext.Session.ProjectId is null ? PersonalRule : ProjectRule;
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, text, Title, InTurnTail: true)]));
    }

    private const string Head =
        "## Картинки и видео: локальная модель по умолчанию\n"
        + "Это правило важнее общего правила о glif/fal-ai и оговорок «локально — только по явной просьбе».\n"
        + "Какой сервис брать, по порядку:\n"
        + "1. Сервис, прямо названный в текущей просьбе («через fal», «в higgsfield», «локально») — он.\n"
        + "2. Выбор человека в полосе «Картинки», если там указан поставщик (виден в блоке «Картинки в этом чате»).\n"
        + "3. Иначе — локальная модель на своей видеокарте. glif, fal-ai и higgsfield — только если локальная "
        + "недоступна и человек согласился на облако.\n";

    // Пометка человеку и отказ без подмены — общие для обоих вариантов
    public const string NoticeRule =
        "Запуская локальную, скажи человеку: «рисую локально (бесплатно), ≈N с/мин; нужно облако — скажи». "
        + "N — expectedSeconds из котировки image_generate или eta_seconds из ответа local_*.\n"
        + "Локальная недоступна или отказала (provider_unavailable, «очередь занята» и т.п.) — одним ответом скажи "
        + "«локальная сейчас недоступна» с причиной и предложи облако с ценой (у image_generate — retryQuote). "
        + "Сам в облако не запускай: только после согласия человека.";

    public const string PersonalNoVideoRule =
        "Видео: локального видео здесь нет — скажи прямо и предложи облако.";

    public const string ProjectRule = Head
        + "Картинки: если в ходе есть блок «Картинки в этом чате» — image_new → image_generate с provider local; "
        + "если блока нет — local_generate_image, правка по образцам — local_edit_image.\n"
        + "Видео: local_text_to_video, local_image_to_video, local_reference_to_video.\n"
        + "Звук и музыка — как раньше: локальных моделей для них нет.\n"
        + NoticeRule;

    // Личный чат: local-media здесь нет, локальная картинка — только драйвер local редактора
    public const string PersonalRule = Head
        + "Картинки: image_new → image_generate с provider local (local-media в этом чате нет).\n"
        + PersonalNoVideoRule + "\n"
        + "Звук и музыка — как раньше: локальных моделей для них нет.\n"
        + NoticeRule;
}
