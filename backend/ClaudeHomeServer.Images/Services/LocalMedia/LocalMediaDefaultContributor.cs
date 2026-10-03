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
// Типов модулей ImageEditor и AudioEditor секция не знает (правило границ): флаги модулей и их тумблеры
// запуска агентом читаются по строковым ключам. Внутри варианта текст стабилен — едет хвостом
// хода, но и там незачем гонять разный текст.
public sealed class LocalMediaDefaultContributor(IFeatureFlagGate flags, IConfiguration config) : IPromptSectionContributor
{
    public const string SectionKey = "local-media-default";

    // Тумблер модуля редактора: без него у агента нет image_generate (ImageEditorToolset.AgentLaunchKey)
    private const string EditorAgentLaunchKey = "ImageEditor:AgentLaunch";

    // То же у модуля «Звук»: без него у агента нет audio_generate (AudioEditorToolset.AgentLaunchKey)
    private const string AudioAgentLaunchKey = "AudioEditor:AgentLaunch";

    // И у модуля «Видео»: без него у агента нет video_shoot (VideoEditorToolset.AgentLaunchKey)
    private const string VideoAgentLaunchKey = "VideoEditor:AgentLaunch";

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

    // Звук идёт через модуль «Звук» (ADR-021 §5, вопрос 1), когда его сервер доехал до хода и у агента
    // есть audio_generate; прямые local_* для звука остаются, но только по явной просьбе
    private bool AudioEditorReady(PromptSessionContext sessionContext) =>
        sessionContext.HasAudioEditorMcp
        && sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.AudioEditor)
        && config.GetValue(AudioAgentLaunchKey, true);

    // Видео идёт через модуль «Видео» (ADR-022 §5), когда его сервер доехал до хода и у агента есть video_shoot;
    // прямые local_*_to_video, generate_video Higgsfield и fal остаются, но только по явной просьбе
    private bool VideoEditorReady(PromptSessionContext sessionContext) =>
        sessionContext.HasVideoEditorMcp
        && sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.VideoEditor)
        && config.GetValue(VideoAgentLaunchKey, true);

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var audio = AudioEditorReady(sessionContext);
        var video = VideoEditorReady(sessionContext);
        var text = sessionContext.Session.ProjectId is null
            ? Personal(audio, video)
            : Project(audio, video);
        // Выбор человека виден в «Чем» хвоста «Контекст хода», а не в полосах и старых блоках
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, ForContextRow(text), Title, InTurnTail: true)]));
    }

    // Тексты под строку контекста: ссылки на полосы «Картинки»/«Звук» и на блок «Картинки в этом чате» как
    // источник выбора переписаны на «Чем» контекста хода. 
    public static string ForContextRow(string rule) => rule
        .Replace("Выбор человека в полосе «Картинки», если там указан поставщик (виден в блоке «Картинки в этом чате»).",
            "Выбор человека в строке контекста, если в «Чем» контекста хода указан исполнитель.")
        .Replace(BandProviderRule, BandProviderRuleContextRow)
        .Replace("если в полосе «Звук» указан поставщик", "если в «Чем» контекста хода указан поставщик");

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

    public const string BandProviderRuleContextRow =
        "если в «Чем» контекста хода указан поставщик — используй его (не подменяй)";

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

    private const string ProjectImages = Head
        + "Картинки: если в ходе есть блок «Картинки в этом чате» — image_new → image_generate; "
        + BandProviderRule + "; если стоит «по умолчанию» — " + ProviderLocalException + ". "
        + "Если блока нет — local_generate_image, правка по образцам — local_edit_image.\n";

    // Модуля «Видео» у хода нет — прямые инструменты local-media
    private const string ProjectVideoDirect = "Видео: local_text_to_video, local_image_to_video, local_reference_to_video.\n";

    private const string ProjectImagesAndVideo = ProjectImages + ProjectVideoDirect;

    // Модуль «Видео» доехал: видео — только через video_* (иначе ролик без карточки сцены в ленте), а прямые
    // local_*_to_video, generate_video Higgsfield и fal — только по прямой просьбе (блок «Видео в этом чате»)
    public const string ProjectVideoEditorRule =
        "Видео: video_new → video_scene_set → video_shoot (см. блок «Видео в этом чате»); если в префах «Видео» указан "
        + "поставщик — используй его (не подменяй), если стоит «по умолчанию» — video_shoot передавай с provider local "
        + "(это исключение из правила «не передавай provider»). Прямые local_text_to_video, local_image_to_video, "
        + "local_reference_to_video — только если человек явно попросил сделать напрямую, мимо редактора.\n";

    // Модуля «Звук» у хода нет — прямые инструменты local-media
    public const string ProjectRule = ProjectImagesAndVideo
        + "Звук и музыка: озвучка — local_speech, песни — local_music_generate, правка трека — local_music_edit; "
        + "обработка — local_audio_separate, local_audio_enhance, local_audio_to_midi, local_transcribe, "
        + "local_voice_convert, local_voice_train.\n"
        + NoticeRule;

    // Модуль «Звук» доехал: звук — через audio_*, а local_* — только по прямой просьбе (блок «Звук в этом чате»)
    public const string ProjectAudioEditorRule =
        "Звук и музыка: audio_new → audio_generate (см. блок «Звук в этом чате»); если в полосе «Звук» указан "
        + "поставщик — используй его (не подменяй), если стоит «по умолчанию» — audio_generate передавай с provider "
        + "local (это исключение из правила «не передавай provider»). Прямые local_* для звука — только если "
        + "человек явно попросил сделать напрямую, мимо редактора.";

    public const string ProjectRuleWithAudioEditor = ProjectImagesAndVideo + ProjectAudioEditorRule + "\n" + NoticeRule;

    private const string ProjectAudioDirect =
        "Звук и музыка: озвучка — local_speech, песни — local_music_generate, правка трека — local_music_edit; "
        + "обработка — local_audio_separate, local_audio_enhance, local_audio_to_midi, local_transcribe, "
        + "local_voice_convert, local_voice_train.\n";

    // Любое сочетание модулей «Звук» и «Видео»: без них тексты — прежние константы
    private static string Project(bool audio, bool video) =>
        ProjectImages + (video ? ProjectVideoEditorRule : ProjectVideoDirect)
        + (audio ? ProjectAudioEditorRule + "\n" : ProjectAudioDirect) + NoticeRule;

    // Личный чат: local-media здесь нет, локальная картинка — только драйвер local редактора
    private const string PersonalImages = Head
        + "Картинки: image_new → image_generate; " + BandProviderRule
        + "; если стоит «по умолчанию» или блока нет — " + ProviderLocalException
        + ". local-media в этом чате нет.\n";

    private const string PersonalImagesAndVideo = PersonalImages + PersonalNoVideoRule + "\n";

    // Локального видео в личной области нет и у модуля «Видео»: облако — его video_shoot
    public const string PersonalVideoEditorRule =
        "Видео: локального видео здесь нет — скажи прямо и предложи облако через "
        + "video_new → video_shoot (см. блок «Видео в этом чате»).\n";

    public const string PersonalRule = PersonalImagesAndVideo
        + "Звук и музыка: локальных моделей в этом чате нет — скажи прямо и предложи облако.\n"
        + NoticeRule;

    // Локального звука в личной области нет и у модуля «Звук»: облако — его audio_generate
    public const string PersonalAudioEditorRule =
        "Звук и музыка: локальных моделей в этом чате нет — скажи прямо и предложи облако через "
        + "audio_new → audio_generate (см. блок «Звук в этом чате»).";

    public const string PersonalRuleWithAudioEditor = PersonalImagesAndVideo + PersonalAudioEditorRule + "\n" + NoticeRule;

    private const string PersonalAudioDirect =
        "Звук и музыка: локальных моделей в этом чате нет — скажи прямо и предложи облако.\n";

    private static string Personal(bool audio, bool video) =>
        PersonalImages + (video ? PersonalVideoEditorRule : PersonalNoVideoRule + "\n")
        + (audio ? PersonalAudioEditorRule + "\n" : PersonalAudioDirect) + NoticeRule;
}
