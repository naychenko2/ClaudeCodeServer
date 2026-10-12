using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Фоновая разметка фото персонажа (ADR-027 §9): флаг, повторы, human, отказы, последовательность, сжатие
public class CharacterPhotoLabelingTests : IDisposable
{
    private const string Owner = "u1";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ccs_label_" + Guid.NewGuid().ToString("N"));
    private readonly FakeLabeler _labeler = new();
    private readonly FakeMedia _media = new();
    private readonly FakeRaster _raster = new();
    private readonly FakeFlags _flags = new();
    private readonly CharacterLoraRuns _locks = new(NullLogger<CharacterLoraRuns>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private CharacterPhotoLabeling Service(TimeProvider? time = null) =>
        new(_locks, NullLogger<CharacterPhotoLabeling>.Instance, _labeler, _flags, _media, _raster, time);

    private string Root(string name = "p")
    {
        var root = Path.Combine(_base, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static CharacterManifest MakeCharacter(string root, string name = "Anya", int photos = 3)
    {
        var r = CharacterStore.Create(root,
            new CharacterDraft(name, null, [.. Enumerable.Range(1, photos).Select(i => new CharacterPhotoUpload(CharacterStoreTests.Jpeg((byte)i), null))]), DateTime.UtcNow);
        r.Error.Should().BeNull();
        return r.Value!.Manifest;
    }

    private static CharacterPhoto Photo(string root, string slug, int i) => CharacterStore.Get(root, slug)!.Photos[i];

    private static CharacterPhotoLabeling.Job JobOf(string root, string slug) => new(Owner, root, slug);

    // Перезаписывает character.json в старом формате (до a65a51e39): LabelSource на всё фото,
    // без AngleSource/EmotionSource. Значения меток задаются явно (null — поле не пишется).
    private static void WriteOldFormat(string root, string slug, string labelSource, string? angle, string? pitch, string? emotion)
    {
        var manifest = CharacterStore.Get(root, slug)!;
        for (var i = 0; i < manifest.Photos.Count; i++)
        {
            var p = manifest.Photos[i];
            manifest.Photos[i] = p with
            {
                Angle = angle, Pitch = pitch, Emotion = emotion,
                LabelSource = labelSource, AngleSource = null, EmotionSource = null,
            };
        }
        var dir = Path.Combine(root, "characters", slug);
        File.WriteAllText(Path.Combine(dir, "character.json"), manifest.Serialize());
    }

    [Fact]
    public async Task Флаг_выключен_ни_одного_вызова()
    {
        var root = Root();
        var m = MakeCharacter(root);
        await Service().ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(0);
        _media.Calls.Should().Be(0);
        _raster.Applied.Should().BeEmpty();
        Photo(root, m.Slug, 0).Angle.Should().BeNull();
    }

    [Fact]
    public void Флаг_выключен_повод_ничего_не_ставит()
    {
        var svc = Service();
        svc.Enqueue(Owner, Root(), "x");
        // Включаем флаг и запускаем: очередь должна быть пуста, значит вызовов нет
        _flags.On = true;
        svc.Invoking(s => s.ProcessAsync(JobOf(Root(), "нет"), default).GetAwaiter().GetResult()).Should().NotThrow();
        _labeler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ракурс_face_pose_пишется_а_эмоция_идёт_через_vision()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "```json\n[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"бред\"}]\n```");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Select(p => p.Pitch).Should().Equal("level", "unknown", "unknown");
        photos.Select(p => p.Emotion).Should().Equal("smile", "laugh", "unknown");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        _media.Calls.Should().Be(1);
        _labeler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Размеченное_и_unknown_повторно_не_уходят()
    {
        _flags.On = true;
        _media.Poses = [null, null, null];
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Refused);
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "unknown" && p.Emotion == "unknown");
        var calls = (_media.Calls, _labeler.Calls);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        (_media.Calls, _labeler.Calls).Should().Be(calls);
    }

    [Fact]
    public async Task Human_не_трогается_и_не_уходит_в_модель()
    {
        _flags.On = true;
        var root = Root();
        var m = MakeCharacter(root);
        CharacterStore.Update(root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels> { [m.Photos[0].File] = new("profile") })).Should().NotBeNull();
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(0, 0, 0), new(0, 0, 0)];
        _labeler.Answer = _ => ImageLabelResult.Ok("[{\"emotion\":\"sad\"},{\"emotion\":\"sad\"}]");

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _media.LastCount.Should().Be(2);
        var first = Photo(root, m.Slug, 0);
        first.LabelSource.Should().Be("human");
        first.Angle.Should().Be("profile");
        first.Emotion.Should().BeNull();
        _raster.Applied.Should().HaveCount(2);
    }

    [Fact]
    public async Task Отказ_модели_даёт_unknown_без_исключения()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.NoVision);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().Invoking(s => s.ProcessAsync(JobOf(root, m.Slug), default)).Should().NotThrowAsync();

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "auto" && p.EmotionSource == "auto");
    }

    // Поведение осознанное, не «улучшать» без ревью: при глобальном сбое провайдера «провалились» все фото пачки,
    // поэтому после трёх Failed подряд unknown получают все десять; битое фото в нормальном ответе
    // получает unknown отдельно и без счётчика
    [Fact]
    public async Task Failed_трижды_на_пачку_из_десяти_даёт_unknown_всем()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        var root = Root();
        var m = MakeCharacter(root, photos: CharacterPhotoLabeling.BatchSize);
        var svc = Service();

        for (var round = 0; round < CharacterPhotoLabeling.MaxFailures; round++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Should().HaveCount(10);
        photos.Should().OnlyContain(p => p.Angle == "unknown" && p.Pitch == "unknown"
            && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
    }

    [Fact]
    public async Task Счётчики_Failed_удалённого_фото_выбрасываются()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        var root = Root();
        var m = MakeCharacter(root, photos: 4);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        var before = svc.FailureCount;
        before.Should().BeGreaterThan(0);

        CharacterStore.Update(root, Owner, m.Slug,
            new CharacterPatch(null, null, [], [m.Photos[3].File], null)).Should().NotBeNull();
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        // Остались 3 фото с третьей неудачей → unknown и ключи сняты; ключ удалённого фото не должен висеть
        svc.FailureCount.Should().Be(0);
    }

    [Theory]
    [InlineData(ImageLabelFailure.Unavailable)]
    [InlineData(ImageLabelFailure.Timeout)]
    public async Task Временная_недоступность_ничего_не_пишет(ImageLabelFailure failure)
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(failure);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == null && p.Emotion == null && p.LabelSource == null);
    }

    [Fact]
    public async Task Failed_третий_раз_подряд_даёт_unknown_и_больше_не_уходит()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        for (var round = 1; round <= 2; round++)
        {
            await svc.ProcessAsync(JobOf(root, m.Slug), default);
            CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
                p => p.Angle == null && p.Emotion == null && p.LabelSource == null);
        }
        _labeler.Calls.Should().Be(4);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
        _labeler.Calls.Should().Be(6);

        // Пауза noroute истекла: noroute повторно уходит (маршрут Vision по-прежнему есть,
        // Failed дошёл до порога, счётчик сбрасывался) — ещё один Failed, вызовов больше
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(8);
    }

    [Theory]
    [InlineData(ImageLabelFailure.Unavailable)]
    [InlineData(ImageLabelFailure.Timeout)]
    public async Task Временная_недоступность_не_увеличивает_счётчик_Failed(ImageLabelFailure temporary)
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        // Две Failed, затем пять временных отказов, затем снова Failed: всего три Failed, но unknown ещё не должно быть
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Answer = _ => ImageLabelResult.Fail(temporary);
        for (var i = 0; i < 5; i++) await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Emotion == null);

        // Третья Failed — счёт дошёл ровно до порога, временные его не сдвинули
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "unknown" && p.Emotion == "unknown");
    }

    [Fact]
    public async Task Успешный_ответ_сбрасывает_счётчик_Failed()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"},{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"},{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"}]");
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "front" && p.Emotion == "smile");

        // Человек сбросил метки — фото снова в разметке; две новые неудачи — меньше порога, если счёт был сброшен успехом
        var reset = m.Photos.ToDictionary(p => p.File, _ => new CharacterPhotoLabels());
        CharacterStore.Update(root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null, reset)).Should().NotBeNull();
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Emotion == null);
    }

    [Fact]
    public async Task Busy_face_pose_ничего_не_пишет()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Busy = true;
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Timeout);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _media.Calls.Should().Be(1);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Pitch == null);
    }

    // Нет маршрута (модель разметки не настроена): сразу unknown + noroute, без трёх попыток и без вызовов.
    // noroute (а не auto) — чтобы фото размечалось, когда маршрут появится
    [Fact]
    public async Task Нет_маршрута_сразу_unknown_без_повторов()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
        // Без маршрута ни vision, ни face-pose не вызываются
        _labeler.Calls.Should().Be(0);
        _media.Calls.Should().Be(0);

        // Повторный заход: noroute мгновенно снова noroute (маршрута по-прежнему нет), без вызова модели
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(0);
        _media.Calls.Should().Be(0);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.AngleSource == "noroute" && p.EmotionSource == "noroute");
    }

    // Итог «не размечено» доходит до DTO непустым (labelSource=auto + unknown), иначе фронт
    // считает фото «Размечаем…»: все четыре поля (labelSource, angle, pitch, emotion) пустые
    [Fact]
    public async Task Нет_маршрута_итог_доходит_до_DTO_непустым()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        var dto = CharacterDto.From(CharacterStore.Get(root, m.Slug)!, withLabels: true);
        // Не «размечается»: labelSource непустой, и хотя бы одна метка (unknown) дошла
        dto.Photos.Should().OnlyContain(p =>
            p.LabelSource != null && (p.Angle != null || p.Pitch != null || p.Emotion != null));
    }

    // Главный сценарий: сначала маршрута нет → фото noroute/unknown; затем маршрут появился
    // (админ назначил модель) и пришёл повод очереди → фото размечены (auto, реальные значения).
    // Повтор запускается на ленивом поводе (здесь — прямой заход ProcessAsync), не по таймеру.
    // Пауза noroute: между фазами 1 и 2 должно пройти больше NoRouteCooldownMinutes.
    [Fact]
    public async Task Маршрут_появился_после_noroute_фото_размечаются()
    {
        _flags.On = true;
        // Фаза 1: маршрута нет — и для ракурса, и для эмоции
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
        var callsAfterNoRoute = _labeler.Calls; // 0: без маршрута модель не вызывается

        // Пауза noroute истекла: фото снова в Candidates
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));

        // Фаза 2: админ назначил модель — маршрут появился (face-pose для ракурса, vision для эмоции)
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");

        // Повод очереди (здесь — прямой заход): noroute-фото снова в Candidates и размечаются
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Select(p => p.Emotion).Should().Equal("smile", "laugh", "neutral");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        // Модель вызвана (эмоция через vision), face-pose тоже; без маршрута вызовов не было
        callsAfterNoRoute.Should().Be(0);
        _media.Calls.Should().Be(1);
        _labeler.Calls.Should().Be(1);
    }

    // ComfyUI лежит → три неудачи → noroute; ComfyUI поднялся и пришёл повод → фото размечаются.
    // В отличие от «модель ответила, но не определила» (auto), noroute «лечится» восстановлением.
    // Пауза noroute: между фазами должно пройти больше NoRouteCooldownMinutes.
    [Fact]
    public async Task ComfyUI_поднялся_после_noroute_фото_размечаются()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        _media.Available = false; // настроен, но ComfyUI сейчас лежит
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        for (var round = 0; round < CharacterPhotoLabeling.MaxFailures; round++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");

        // Пауза noroute истекла: фото снова в Candidates
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));

        // ComfyUI поднялся, пришёл повод: noroute-фото снова в Candidates и размечаются.
        // Эмоция тоже через vision (реальный ответ), чтобы её запись не понизила LabelSource до noroute
        _media.Available = true;
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        _media.Calls.Should().Be(1);
    }

    // Модель ответила, но определить не смогла (NoVision) → auto/unknown: повторно НЕ размечаем.
    // Это противоположность noroute: auto «лечится» только загрузкой нового фото, а не появлением маршрута.
    [Fact]
    public async Task Модель_не_определила_auto_unknown_повторно_не_уходит()
    {
        _flags.On = true;
        // Оба места через vision: и ракурс, и эмоция «ответили» NoVision → оба auto/unknown
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.NoVision);
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "auto" && p.EmotionSource == "auto");
        var calls = _labeler.Calls; // 2: ракурс и эмоция, оба «ответили» NoVision

        // Повторный заход: auto/unknown повторно не уходит, вызовов нет
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(calls);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "auto" && p.EmotionSource == "auto");
    }

    // Смешанный случай (а): ракурс — noroute (local-media не заведён), эмоция — auto (модель ответила).
    // Общий LabelSource это путал: эмоция затирает noroute, ракурс навсегда unknown. По месту — нет:
    // после появления маршрута ракурс размечается (auto), а эмоция повторно в модель не уходит.
    [Fact]
    public async Task Смешанный_а_ракурс_noroute_эмоция_auto_после_маршрута_ракурс_размечается()
    {
        _flags.On = true;
        // Фаза 1: ракурс — face-pose, но local-media не заведён → noroute; эмоция — vision, ответила → auto
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Configured = false; // local-media не заведён → ракурс noroute
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        // Ракурс: unknown + noroute; эмоция: размечена + auto
        photos.Should().OnlyContain(p =>
            p.Angle == "unknown" && p.Pitch == "unknown" && p.AngleSource == "noroute"
            && p.EmotionSource == "auto" && p.Emotion != null);
        var emotionCalls = _labeler.Calls; // 1: только эмоция (ракурс — face-pose, в модель не шёл)

        // Пауза noroute истекла, маршрут ракурса появился (local-media заведён)
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));
        _media.Configured = true;
        _media.Available = true;
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];

        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        photos = CharacterStore.Get(root, m.Slug)!.Photos;
        // Ракурс размечен (auto), эмоция не затёрта (auto) и повторно в модель не ушла
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        _labeler.Calls.Should().Be(emotionCalls, "эмоция повторно в vision не ушла");
    }

    // Смешанный случай (б): эмоция — auto (сначала), ракурс — noroute (после, ComfyUI лежит).
    // Общий LabelSource это путал: noroute ракурса «поднимал» фото, и после паузы эмоцию повторно
    // шли в модель. По месту — нет: повтор по noroute идёт только для места, у которого noroute.
    [Fact]
    public async Task Смешанный_б_эмоция_auto_ракурс_noroute_эмоция_повторно_не_уходит()
    {
        _flags.On = true;
        // Фаза 1: эмоция — vision, ответила → auto; ракурс — face-pose, ComfyUI лежит → Failed × 3 → noroute
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Available = false; // ComfyUI лежит
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        for (var round = 0; round < CharacterPhotoLabeling.MaxFailures; round++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);
        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        // Эмоция: размечена + auto; ракурс: unknown + noroute
        photos.Should().OnlyContain(p =>
            p.Angle == "unknown" && p.Pitch == "unknown" && p.AngleSource == "noroute"
            && p.EmotionSource == "auto" && p.Emotion != null);
        var emotionCalls = _labeler.Calls; // 1: эмоция размечена один раз (ракурс — face-pose)

        // Пауза noroute истекла, ComfyUI по-прежнему лежит: повтор только по месту ракурса
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Should().OnlyContain(p =>
            p.Angle == "unknown" && p.Pitch == "unknown" && p.AngleSource == "noroute"
            && p.EmotionSource == "auto" && p.Emotion != null);
        _labeler.Calls.Should().Be(emotionCalls, "эмоция повторно в vision не ушла");
    }

    // human не трогается ни в одном сценарии: через цикл «нет маршрута → маршрут появился»
    // ручная метка остаётся на месте, авторазметка её не затирает.
    // Пауза noroute: между фазами должно пройти больше NoRouteCooldownMinutes.
    [Fact]
    public async Task Human_не_трогается_через_noroute_и_появление_маршрута()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);
        // Ручная метка на первом фото
        CharacterStore.Update(root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels> { [m.Photos[0].File] = new("profile") })).Should().NotBeNull();
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        // Фаза 1: нет маршрута → остальные фото noroute, human не тронут
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        var first = Photo(root, m.Slug, 0);
        first.LabelSource.Should().Be("human");
        first.Angle.Should().Be("profile");
        CharacterStore.Get(root, m.Slug)!.Photos.Where(p => p.LabelSource != "human")
            .Should().OnlyContain(p => p.AngleSource == "noroute" && p.EmotionSource == "noroute");

        // Пауза noroute истекла: фото снова в Candidates
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));

        // Фаза 2: маршрут появился → human по-прежнему не тронут, остальные размечены
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), new(0, 0, 0)];
        _labeler.Answer = _ => ImageLabelResult.Ok("[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var afterFirst = Photo(root, m.Slug, 0);
        afterFirst.LabelSource.Should().Be("human");
        afterFirst.Angle.Should().Be("profile");
        CharacterStore.Get(root, m.Slug)!.Photos.Where(p => p.LabelSource != "human")
            .Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
    }

    // Face-pose: ComfyUI настроен, но лежит — временный сбой, до трёх попыток, потом unknown + noroute.
    // Эмоция — None (маршрута нет): тоже noroute, чтобы тест был про место ракурса, а не смешанный случай.
    [Fact]
    public async Task FacePose_ComfyUI_лежит_после_трёх_попыток_unknown()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        _media.Available = false; // настроен, но ComfyUI сейчас лежит
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        for (var round = 0; round < CharacterPhotoLabeling.MaxFailures; round++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
        // ComfyUI лежит — FacePoseAsync не вызывался, только счётчик Failed
        _media.Calls.Should().Be(0);
    }

    // Face-pose: подсистема не настроена (тумблер выключен) — нет маршрута, сразу unknown + noroute
    [Fact]
    public async Task FacePose_не_настроен_сразу_unknown()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        _media.Configured = false; // тумблер local-media выключен
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
        _media.Calls.Should().Be(0);
    }

    // Пауза noroute: 10 поводов подряд при route=None → запись noroute не больше одного раза.
    // Первый заход: фото null → берём → noroute. Повторные: noroute в паузе → не берём.
    [Fact]
    public async Task Noroute_пауза_не_больше_одной_записи_за_пять_минут()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        for (var i = 0; i < 10; i++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);

        // 2 = один цикл × два места (angle + emotion); повторные циклы в паузе не пишут
        svc.NoRouteWriteCount.Should().Be(2, "первый цикл пишет noroute для angle и emotion, остальные в паузе");
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown"
                && p.AngleSource == "noroute" && p.EmotionSource == "noroute");
    }

    // Пауза noroute при лежащем ComfyUI: 10 поводов, а обращений к модели (localMedia) нет вовсе.
    // ComfyUI лежит — FacePoseAsync short-circuit'ит на !Available до localMedia.FacePoseAsync;
    // ракурс копит счётчик Failed (3 раунда → noroute), эмоция (route=None) — noroute сразу.
    // Повторные заходы: noroute в паузе → не берём → обращений и записей нет.
    [Fact]
    public async Task Noroute_пауза_ComfyUI_лежит_обращений_к_модели_нет()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        _media.Available = false; // ComfyUI лежит
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        for (var i = 0; i < 10; i++)
            await svc.ProcessAsync(JobOf(root, m.Slug), default);

        // ComfyUI лежит — FacePoseAsync short-circuit'ит на !Available, localMedia.FacePoseAsync не вызывается
        _media.Calls.Should().Be(0, "ComfyUI лежит — FacePoseAsync не доходит до localMedia");
        // 2 = один цикл × два места (angle + emotion): ракурс — после трёх Failed, эмоция — сразу;
        // повторные циклы в паузе не пишут
        svc.NoRouteWriteCount.Should().Be(2, "цикл доводит оба места до noroute, остальные заходы в паузе");
    }

    // Пауза прошла, маршрут появился → фото размечены (главный сценарий жив).
    // Отличается от Маршрут_появился_после_noroute: здесь ComfyUI лежит (не route=None),
    // и мы проверяем, что именно истечение паузы (а не смена маршрута) возвращает фото в Candidates.
    [Fact]
    public async Task Noroute_пауза_прошла_маршрут_появился_фото_размечаются()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.None, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.None, false, null);
        var root = Root();
        var m = MakeCharacter(root);
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        // Запись noroute: 2 = один цикл × два места (angle + emotion)
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        svc.NoRouteWriteCount.Should().Be(2);

        // Пауза ещё не прошла: повторный заход не пишет
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        svc.NoRouteWriteCount.Should().Be(2, "в пределах паузы noroute не повторяется");

        // Пауза истекла, маршрут появился
        time.Advance(TimeSpan.FromMinutes(CharacterPhotoLabeling.NoRouteCooldownMinutes + 1));
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");

        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Select(p => p.Emotion).Should().Equal("smile", "laugh", "neutral");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
    }

    [Fact]
    public async Task Сжатие_до_512_перед_отправкой_в_облако()
    {
        _flags.On = true;
        _raster.Width = 3000;
        _raster.Height = 2000;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = r => ImageLabelResult.Ok("[{\"angle\":\"front\",\"pitch\":\"level\"}]");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _raster.Applied.Should().NotBeEmpty();
        _raster.Applied.Select(o => o.Single()).Should().OnlyContain(o => IsWidth512(o));
        _labeler.Requests.Should().OnlyContain(r => r.Images.All(i => i.Bytes.SequenceEqual(FakeRaster.Output)));
        _labeler.Requests[0].Prompt.Should().Contain("Не опознавай");
    }

    [Fact]
    public async Task Мелкое_фото_не_увеличивается()
    {
        _flags.On = true;
        _raster.Width = 300;
        _raster.Height = 400;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _raster.Applied.Should().OnlyContain(o => o.Count == 0);
    }

    [Fact]
    public async Task Два_персонажа_строго_последовательно()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)];
        _media.Delay = TimeSpan.FromMilliseconds(100);
        _labeler.Answer = _ => ImageLabelResult.Ok("[{\"emotion\":\"neutral\"},{\"emotion\":\"neutral\"},{\"emotion\":\"neutral\"}]");
        _labeler.Delay = TimeSpan.FromMilliseconds(50);
        var root = Root();
        var a = MakeCharacter(root, "Anya");
        var b = MakeCharacter(root, "Boris");
        var svc = Service();
        await svc.StartAsync(default);
        try
        {
            svc.Enqueue(Owner, root, a.Slug);
            svc.Enqueue(Owner, root, b.Slug);
            svc.Enqueue(Owner, root, b.Slug);
            await WaitAsync(() => CharacterStore.Get(root, b.Slug)!.Photos.All(p => p.Emotion != null));
        }
        finally { await svc.StopAsync(default); }

        Concurrency.Max.Should().Be(1);
        _media.Calls.Should().Be(2);
        CharacterStore.Get(root, a.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "front" && p.Emotion == "neutral");
    }

    // Старый формат (до a65a51e39): LabelSource = noroute на всё фото, без AngleSource/EmotionSource.
    // Без миграции фото не попадало в Candidates (Angle=unknown, AngleSource=null) и оставалось unknown
    // навсегда. С миграцией: AngleSource/EmotionSource = noroute → после паузы фото размечается заново.
    [Fact]
    public async Task Старый_манифест_noroute_размечается_когда_маршрут_появится()
    {
        _flags.On = true;
        var root = Root();
        var m = MakeCharacter(root);
        // Старый формат: noroute на всё фото, без по-местных источников
        WriteOldFormat(root, m.Slug, "noroute", "unknown", "unknown", "unknown");
        var time = new ManualTime(DateTime.UtcNow);
        var svc = Service(time);

        // Маршрут настроен (face-pose для ракурса, vision для эмоции)
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"}]");

        // Пауза noroute: _lastNoRouteWriteUtc пуст (старый код не шёл через WriteAsync), поэтому inCooldown=false
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Select(p => p.Emotion).Should().Equal("smile", "laugh", "neutral");
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        _media.Calls.Should().Be(1);
        _labeler.Calls.Should().Be(1);
    }

    // Старый формат: LabelSource = auto, значения заполнены. Миграция раскладывает в AngleSource/EmotionSource = auto.
    // Повторной разметки нет: needs=false (значения заполнены), placeSource=auto ≠ noroute.
    [Fact]
    public async Task Старый_auto_повторно_не_размечается()
    {
        _flags.On = true;
        var root = Root();
        var m = MakeCharacter(root);
        WriteOldFormat(root, m.Slug, "auto", "front", "level", "smile");
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Routes[LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m");
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"emotion\":\"laugh\"},{\"emotion\":\"neutral\"},{\"emotion\":\"sad\"}]");

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        // Значения не изменились, источники auto, повторных вызовов модели нет
        photos.Should().OnlyContain(p => p.AngleSource == "auto" && p.EmotionSource == "auto");
        photos[0].Angle.Should().Be("front");
        photos[0].Emotion.Should().Be("smile");
        _media.Calls.Should().Be(0, "повторной разметки нет");
        _labeler.Calls.Should().Be(0, "повторной разметки нет");
    }

    // Старый формат: LabelSource = human. Миграция не трогает (human ≠ auto/noroute).
    [Fact]
    public void Старый_human_не_трогается()
    {
        var root = Root();
        var m = MakeCharacter(root);
        WriteOldFormat(root, m.Slug, "human", "profile", "level", "smile");

        var photo = CharacterStore.Get(root, m.Slug)!.Photos[0];
        photo.LabelSource.Should().Be("human");
        photo.Angle.Should().Be("profile");
        photo.AngleSource.Should().BeNull();
        photo.EmotionSource.Should().BeNull();
    }

    private static bool IsWidth512(ImageTransformOp op) => op is ResizeOp { Width: 512, Height: null };

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        done().Should().BeTrue();
    }

    private static class Concurrency
    {
        private static int _now;
        public static int Max { get; private set; }
        public static void Enter() { var n = Interlocked.Increment(ref _now); lock (typeof(Concurrency)) Max = Math.Max(Max, n); }
        public static void Exit() => Interlocked.Decrement(ref _now);
    }

    private sealed class FakeFlags : IFeatureFlagGate
    {
        public bool On { get; set; }
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.CharacterSmartPhotos;
    }

    private sealed class FakeLabeler : IImageLabeler
    {
        public Dictionary<string, ImageLabelRoute> Routes { get; } = new()
        {
            [LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null),
            [LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m"),
        };
        public Func<ImageLabelRequest, ImageLabelResult> Answer { get; set; } = _ => ImageLabelResult.Ok("[]");
        public TimeSpan Delay { get; set; }
        public List<ImageLabelRequest> Requests { get; } = [];
        public int Calls => Requests.Count;

        public ImageLabelRoute RouteOf(string actionKey, string ownerId) => Routes[actionKey];

        public async Task<ImageLabelResult> LabelAsync(ImageLabelRequest request, CancellationToken ct)
        {
            Concurrency.Enter();
            try
            {
                Requests.Add(request);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                return Answer(request);
            }
            finally { Concurrency.Exit(); }
        }
    }

    private sealed class FakeMedia : ILocalImageMedia
    {
        public bool Available { get; set; } = true;
        public bool Configured { get; set; } = true;
        public bool Busy { get; set; }
        public TimeSpan Delay { get; set; }
        public IReadOnlyList<LocalFacePose?> Poses { get; set; } = [];
        public int Calls { get; private set; }
        public int LastCount { get; private set; }

        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult<int?>(0);
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => null;
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => Task.FromResult(false);

        public async Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct)
        {
            Concurrency.Enter();
            try
            {
                Calls++;
                LastCount = images.Count;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                if (Busy) return LocalFacePoseResult.Fail("занято", busy: true);
                return new LocalFacePoseResult([.. Poses.Take(images.Count)], null);
            }
            finally { Concurrency.Exit(); }
        }
    }

    private sealed class FakeRaster : IImageRaster
    {
        public static readonly byte[] Output = [1, 2, 3, 4];
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 600;
        public List<IReadOnlyList<ImageTransformOp>> Applied { get; } = [];

        public RasterProbe? Probe(byte[] data) => new(ImageEncodeFormat.Jpeg, "jpeg", Width, Height, 1, data.Length);

        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null)
        {
            Applied.Add(ops);
            return new RasterOutcome(new RasterImage(Output, ImageEncodeFormat.Jpeg, 1, 1), RasterError.None, null);
        }

        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => throw new NotSupportedException();
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => throw new NotSupportedException();
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => throw new NotSupportedException();
    }

    // Часы с ручным ходом: тесты паузы noroute двигают время, не ждя реальных пяти минут
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public void Advance(TimeSpan delta) => now += delta;
        public override DateTimeOffset GetUtcNow() => now;
    }
}
