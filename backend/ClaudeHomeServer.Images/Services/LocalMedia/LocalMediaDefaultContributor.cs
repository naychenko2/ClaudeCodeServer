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
            ? PersonalEditorReady(ownerId, sessionContext)
            : sessionContext.HasLocalMediaMcp);

    // Личный вариант зовёт image_new/image_generate: сервер редактора обязан доехать до хода (как
    // local-media у проектного). Флаг читается живьём — контекст MCP собран при создании сессии и
    // выключение флага посреди чата не увидит; без AgentLaunch сервер едет, но без image_generate
    private bool PersonalEditorReady(string ownerId, PromptSessionContext sessionContext) =>
        sessionContext.HasImageEditorMcp
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor)
        && config.GetValue(EditorAgentLaunchKey, true);

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
        + "1. " + CurrentRequestRule + "\n"
        + "2. Выбор человека в полосе «Картинки», если там указан поставщик (виден в блоке «Картинки в этом чате»).\n"
        + "3. Иначе — локальная модель на своей видеокарте. glif, fal-ai и higgsfield — только если локальная "
        + "недоступна и человек согласился на облако.\n";

    // Защита от «прилипания» (QA-дефект 1e099019): смотреть на текущую просьбу, а не на прошлые вызовы
    public const string CurrentRequestRule =
        "Сервис, названный в ТЕКУЩЕЙ просьбе («через fal», «в higgsfield», «локально») — он, даже если "
        + "предыдущие картинки в этом чате рисовались локально; прошлые вызовы правилом не считаются.";

    // Согласование с ChoiceRule блока «Картинки» («не передавай provider без просьбы»)
    public const string BandProviderRule =
        "если в блоке «Картинки в этом чате» указан поставщик — используй его (не подменяй)";

    public const string ProviderLocalException =
        "image_generate передавай с provider local (это исключение из правила «не передавай provider»)";

    // Пометка человеку и отказ без подмены — общие для обоих вариантов
    public const string NoticeRule =
        "Запуская локальную, скажи человеку: «рисую локально (бесплатно), ≈N с; нужно облако — скажи». "
        + "N — expectedSeconds из котировки image_generate или eta_seconds из ответа local_*; "
        + NoNumberRule + "\n"
        + "Локальная недоступна или отказала (provider_unavailable, «очередь занята» и т.п.) — одним ответом скажи "
        + "«локальная сейчас недоступна» с причиной и предложи облако с ценой (у image_generate — retryQuote). "
        + CloudConsentRule;

    public const string NoNumberRule =
        "если числа нет — не называй его, скажи просто «рисую локально (бесплатно)».";

    public const string CloudConsentRule =
        "Облако запускай только после согласия человека, а согласие на одно облачное действие "
        + "не распространяется на следующие просьбы.";

    public const string PersonalNoVideoRule =
        "Видео: локального видео здесь нет — скажи прямо и предложи облако.";

    public const string ProjectRule = Head
        + "Картинки: если в ходе есть блок «Картинки в этом чате» — image_new → image_generate; "
        + BandProviderRule + "; если стоит «по умолчанию» — " + ProviderLocalException + ". "
        + "Если блока нет — local_generate_image, правка по образцам — local_edit_image.\n"
        + "Видео: local_text_to_video, local_image_to_video, local_reference_to_video.\n"
        + "Звук и музыка: озвучка — local_speech, песни — local_music_generate, правка трека — local_music_edit; "
        + "обработка — local_audio_separate, local_audio_enhance, local_audio_to_midi, local_transcribe, "
        + "local_voice_convert, local_voice_train.\n"
        + NoticeRule;

    // Личный чат: local-media здесь нет, локальная картинка — только драйвер local редактора
    public const string PersonalRule = Head
        + "Картинки: image_new → image_generate; " + BandProviderRule
        + "; если стоит «по умолчанию» или блока нет — " + ProviderLocalException
        + ". local-media в этом чате нет.\n"
        + PersonalNoVideoRule + "\n"
        + "Звук и музыка: локальных моделей в этом чате нет — скажи прямо и предложи облако.\n"
        + NoticeRule;
}
