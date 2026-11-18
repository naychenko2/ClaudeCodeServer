using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Team;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

/// <summary>Живой маршрут предмета для перезакрепления его чатов: репетитор, поставщик (ключ записи пула или провайдера) и модель.</summary>
public sealed record ChildRepinTarget(string PersonaId, string ProviderKey, string Model);

// Смена провайдера у чата (S3-3, docs/research/session-manager-split-2026-11.md §1):
// единственная точка — и карточка «Продолжить на …» при исчерпанном лимите, и обычная смена
// модели в настройках (UpdateAsync → ChatSettings). Guard смены провайдера у начатого чата
// живёт здесь (перенос транскрипта, отказы на текущем ключе/несовместимой модели/пути вне
// монтирований) — в §1 он отдельно от маршрута именно поэтому: в CliRoute он стал бы
// одиннадцатой зависимостью.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// сам конструктор internal: Lazy<ITeamNotifier> — internal-тип (ITeamNotifier не публичный),
// а публичная конструктор-сигнатура с ним краснеет по той же CS0051. Обход фасада закрывает
// сторож формы ядра (правило 4).
// Зависимости (5): store, route, providers, pool, шов Lazy<ITeamNotifier>. В таблице S3-0 их
// шесть (с history) — в перенесённых телах история не встречается, лишней зависимости нет.
public sealed class ProviderMigration
{
    private readonly SessionStore _store;
    private readonly CliRoute _route;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    // Шов «миграция → штаб»: при смене провайдера посреди план-фазы без поддержки «План»
    // штаб возвращает чат из стадии. Рекурсия через фасад (ITeamNotifier реализует
    // SessionManager) — названа ленивым швом (S3-0 §3, S3-6 переклюет на владельца).
    private readonly Lazy<ITeamNotifier> _teamNotifier;
    private readonly ILogger<ProviderMigration> _log;

    internal ProviderMigration(
        SessionStore store,
        CliRoute route,
        LlmProviderRegistry llmProviders,
        ClaudeSubscriptionPool subscriptionPool,
        Lazy<ITeamNotifier> teamNotifier,
        ILogger<ProviderMigration> log)
    {
        _store = store;
        _route = route;
        _llmProviders = llmProviders;
        _subscriptionPool = subscriptionPool;
        _teamNotifier = teamNotifier;
        _log = log;
    }

    // «Переезжать некуда»: по СЫРЫМ полям чата (Info.Model → Info.Provider) цель совпала с
    // текущим провайдером. Для эндпоинта migrate-provider это отказ (просили перенести — не
    // перенесли, 400), для UpdateAsync — штатный случай: он сравнивает провайдеров по
    // ЭФФЕКТИВНЫМ моделям, и после переставленного назначения места две картины расходятся.
    // Отдельный ТИП, а не сравнение текста исключения: текст пишется человеку и меняется, а
    // ловля по нему молча пропускала бы наружу любую другую форму «переезжать не нужно»
    // ложным 400 на весь PATCH.
    internal sealed class ProviderUnchangedException()
        : InvalidOperationException("Чат уже на этом провайдере");

    // Карточка «Продолжить на …»: чат родного Claude упёрся в лимит, внутри пула
    // автофейловер (TryPoolFailover) не переключил. Предлагаем варианты: здоровые
    // аккаунты ТОГО ЖЕ пула (пользователь выбирает сам — TryPoolFailover либо уже
    // проверил их все на исчерпание, либо переключаться было некуда) и настроенные
    // сторонние провайдеры. Эфемерно (в history не пишется): после сброса окна
    // предложение неактуально. internal — тестируется без розыгрыша целого хода
    // (см. OfferProviderFallbackAsync_*Tests).
    internal async Task OfferProviderFallbackAsync(string sessionId, string? resetsAt)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        if (_llmProviders.ResolveByModel(entry.Info.Model) is not null) return; // уже сторонний
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_subscriptionPool.IsExhausted(current)) return;

        var subscriptionOptions = _subscriptionPool.All
            .Where(s => s.Key != current && !_subscriptionPool.IsExhausted(s.Key)
                && _subscriptionPool.SupportsModel(s.Key, entry.Info.Model))
            .Select(s => new ProviderFallbackOption(
                s.Key,
                string.IsNullOrWhiteSpace(s.DisplayName) ? s.Key : s.DisplayName,
                entry.Info.Model ?? "",
                Kind: "subscription",
                TierLabel: _subscriptionPool.TierLabel(s.Key),
                Utilization: _subscriptionPool.EffectiveUtilization(s.Key)))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var providerOptions = _llmProviders.Enabled
            .Select(p => new ProviderFallbackOption(p.Key,
                string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
                p.Models.FirstOrDefault()?.Id ?? ""))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var options = subscriptionOptions.Concat(providerOptions).ToList();
        if (options.Count > 0)
            await _store.BroadcastAsync(sessionId, new ProviderLimitMessage(resetsAt, options));
    }

    // Единственная точка смены провайдера у чата: и кнопка «Продолжить на …» (исчерпан
    // лимит), и обычная смена модели в настройках (UpdateAsync). Транскрипт CLI локальный —
    // переносим его в профиль целевого провайдера и продолжаем разговор через --resume без
    // потери контекста.
    // subscriptionKey — явный выбор аккаунта ТОГО ЖЕ пула подписок (кнопка карточки с
    // Kind="subscription"): вместо автовыбора Pick пользователь указывает конкретный ключ.
    // model = null — «По умолчанию» из настроек чата, когда назначение места модель не даёт:
    // переезжаем на родной Claude, ничего не закрепляя. Эндпоинт migrate-provider пустую
    // модель по-прежнему не принимает — проверка живёт у него.
    public async Task<Session> MigrateProviderAsync(string sessionId, string ownerId, string? model,
        string? subscriptionKey = null)
    {
        if (_store.GetOwned(sessionId, ownerId) is null || !_store.Sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        var newModel = _llmProviders.CanonicalizeModel(string.IsNullOrWhiteSpace(model) ? null : model.Trim());

        var target = _llmProviders.ResolveByModel(newModel);
        if (target is { Enabled: false })
            throw new InvalidOperationException(
                $"Провайдер «{target.DisplayName}» не настроен: задай LlmProviders:{target.Key}:ApiKey");

        var currentKey = _llmProviders.ResolveByModel(entry.Info.Model)?.Key
            ?? entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;

        ClaudeSubscriptionConfig? pickedSub = null;
        string targetKey;
        if (!string.IsNullOrWhiteSpace(subscriptionKey))
        {
            if (target is not null)
                throw new InvalidOperationException("Ключ подписки задан вместе со сторонним провайдером");
            var sub = _subscriptionPool.All.FirstOrDefault(s => s.Key == subscriptionKey);
            if (sub is null)
                throw new InvalidOperationException($"Подписка «{subscriptionKey}» не настроена");
            if (!_subscriptionPool.SupportsModel(sub.Key, newModel))
                throw new InvalidOperationException($"Подписка «{sub.Key}» не поддерживает модель «{newModel}»");
            pickedSub = sub;
            targetKey = sub.Key;
        }
        else
        {
            // Цель: сторонний провайдер — его ключ; родной Claude — доступный аккаунт пула.
            if (target is null)
            {
                // null от ResolveByModel = родной Claude (подписка, без env-оверрайдов). Но тот же
                // null получается и для неизвестной модели: её id не нашёлся ни в Models, ни по
                // префиксу ни одного провайдера — фронт и реестр рассинхронизированы (каталог
                // /api/models отдал id, которого текущий LlmProviderRegistry не знает). Молчаливый
                // фолбэк на Pick давал ложное «Чат уже на этом провайдере» (Pick выбирал тот же
                // аккаунт пула, что текущий), поэтому неизвестную модель называем вслух.
                // Два случая различает ФОРМА id (IsNativeClaudeModel), а не сравнение с
                // Info.Model: у чата на «По умолчанию» она null, и по ней opus при живом пуле
                // выглядел как неизвестная модель — PATCH настроек падал с ложным «модель не
                // найдена» (дефект 3-й итерации). Проверка безусловная: форма id от наличия
                // пула подписок не зависит, а на коробке БЕЗ ClaudeSubscriptions мусорный id
                // иначе доезжал бы до Pick → PrimaryKey → «уже на этом провайдере», молча
                // ложился в Info.Model и валил каждый следующий ход.
                // Родная модель идёт дальше в Pick: тот вернёт либо текущий аккаунт (переезд
                // вырождается в «просто закрепить модель» — ProviderUnchangedException ниже),
                // либо здоровый другой — это штатная ротация пула кнопкой «Продолжить на …».
                if (!LlmProviderRegistry.IsNativeClaudeModel(newModel))
                    throw new InvalidOperationException(
                        $"Модель «{newModel}» не найдена среди настроенных провайдеров");
                targetKey = _subscriptionPool.Pick(newModel);
            }
            else
            {
                targetKey = target.Key;
            }
        }

        if (string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase))
            throw new ProviderUnchangedException();

        // Разделитель «Продолжено на …» ставим только по факту переноса: в чате, где
        // переносить было нечего, продолжать тоже нечего — карточка врала бы.
        var transcriptMoved = MoveTranscript(sessionId, ownerId, entry, currentKey, targetKey);

        entry.Info.Model = newModel;
        entry.Info.Provider = targetKey;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        // Адаптер собран под прежнего провайдера (env и сигнатура хода) — убираем лениво,
        // как при смене собеседника: мгновенный dispose рвал бы доживающих агентов
        if (entry.Process is not null) entry.AdapterStale = true;
        // Minor (волна 3): миграция посреди план-фазы (интервью/планирование) на провайдера
        // без поддержки «План» — раньше SavedMode оставался висеть навсегда: селектор режима
        // заблокирован (SetMode отказывает при SavedMode != null), а ход уже идёт не в
        // план-режиме (новый провайдер --permission-mode plan не понимает). Та же деградация
        // «молча остаёмся в прежнем режиме», что и EnterPlanPhaseMode на входе в план-фазу —
        // только тут применяется задним числом, когда план-фаза уже шла на другом провайдере.
        if (entry.Info.TeamImplement is { SavedMode: not null }
            && !_llmProviders.CapabilitiesFor(newModel).SupportsPlanMode)
            _teamNotifier.Value.RestoreUserMode(sessionId);
        _store.SaveSessions();

        // Явно выбранный аккаунт пула — подпись «на подписке», а не безликое «на AI».
        // Label = null — сообщение уходит без разделителя: висящую карточку «Продолжить на …»
        // оно всё равно гасит (chatReducer), а ленту не засоряет. Разделителя нет в двух
        // случаях: переносить было нечего (продолжать нечего — карточка врала бы) и автовыбор
        // ДРУГОГО аккаунта того же пула Claude (ротация подписок по договорённости тихая: тип
        // поставщика не менялся, а «Продолжено на AI» читается как уход к другому вендору).
        // Явный тык в карточку подписки (pickedSub) подпись сохраняет — это выбор человека.
        string? switchLabel = null;
        if (transcriptMoved)
        {
            if (pickedSub is not null)
                switchLabel = "Продолжено на подписке "
                    + $"«{(string.IsNullOrWhiteSpace(pickedSub.DisplayName) ? pickedSub.Key : pickedSub.DisplayName)}»";
            else if (target is not null)
                switchLabel = "Продолжено на "
                    + (string.IsNullOrWhiteSpace(target.DisplayName) ? target.Key : target.DisplayName);
            // Возврат СО стороннего провайдера на родной Claude — смена типа поставщика, подпись нужна
            else if (_llmProviders.GetByKey(currentKey) is not null)
                switchLabel = "Продолжено на AI";
        }
        await _store.BroadcastAsync(sessionId, new ProviderSwitchedMessage(targetKey, newModel, switchLabel));
        Console.WriteLine($"[SessionManager] Чат {sessionId} мигрирован: {currentKey} → {targetKey} "
            + $"({newModel ?? "по умолчанию"}, транскрипт: {(transcriptMoved ? "перенесён" : "нечего переносить")})");
        return entry.Info;
    }

    // Одновременное перезакрепление чатов детских предметов — строго по одному: два PUT подряд
    // не копируют один транскрипт параллельно и не пишут поля вперемешку (RepinCrossProvider_TwoPuts_
    // SingleFinalRoute). Статический: ProviderMigration — синглтон, но сторож не должен зависеть от DI.
    private static readonly SemaphoreSlim ChildRepinGate = new(1, 1);

    // Перезакрепление маршрута предмета (Т2e, решение Андрея 18.11) и перенос чатов при смене
    // поставщика (Т2e2): каждый чат этого ребёнка и предмета получает персону, поставщика и модель
    // из ЖИВОГО маршрута предмета. liveTarget зовётся на каждый чат внутри очереди: два PUT подряд
    // оставляют чаты при итоговом маршруте, а не при значении старого запроса; null — маршрут
    // не принят ChildRoute или предмета нет (чат не трогаем, ход ему и так откажет).
    // Цель — РОВНО ProviderKey предмета (И2′): _subscriptionPool.Pick здесь не зовётся никогда,
    // подмена аккаунта пула у ребёнка недопустима. Сменился поставщик — транскрипт копируется в
    // профиль нового (MoveTranscript, то же правило, что у MigrateProviderAsync): иначе --resume
    // не найдёт его после рестарта. Идущий ход доживает: при СМЕНЕ ПОСТАВЩИКА чат с ходом в полёте
    // пропускается — копия неполного транскрипта потеряла бы хвост ответа, который доживающий
    // процесс допишет в старый профиль (так же отказывает ChatSettings.UpdateAsync на идущем ходе);
    // чат остаётся на прежних полях до следующего PUT. Смена модели у того же поставщика
    // переписывает поля сразу, идущий ход доживает со старыми. Сбой переноса одного
    // чата — лог, чат остаётся на прежних полях, остальные переносятся. UpdatedAt не двигается:
    // маршрут — настройка чата, а не активность. Возвращает число перезакреплённых чатов.
    public async Task<int> RepinChildSubjectAsync(string ownerId, string subjectId, Func<ChildRepinTarget?> liveTarget)
    {
        var candidates = _store.Sessions.Values.Select(e => e.Info)
            .Where(i => i.ChildSpace is not null
                && string.Equals(i.ChildSubject, subjectId, StringComparison.OrdinalIgnoreCase)
                && _store.ResolveOwnerId(i) == ownerId)
            .Select(i => i.Id).ToList();
        var changed = 0;
        foreach (var sessionId in candidates)
        {
            await ChildRepinGate.WaitAsync();
            try
            {
                if (await RepinOneChildChatAsync(sessionId, ownerId, liveTarget)) changed++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Детский режим: чат {Session} предмета {Subject} не перенесён на новый маршрут (ребёнок {Owner})",
                    sessionId, subjectId, ownerId);
            }
            finally { ChildRepinGate.Release(); }
        }
        return changed;
    }

    private async Task<bool> RepinOneChildChatAsync(string sessionId, string ownerId, Func<ChildRepinTarget?> liveTarget)
    {
        if (liveTarget() is not { } target) return false;
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        var info = entry.Info;
        if (info.PersonaId == target.PersonaId && info.Provider == target.ProviderKey && info.Model == target.Model)
            return false;

        var currentKey = _llmProviders.ResolveByModel(info.Model)?.Key ?? info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        var providerChanged = !string.Equals(currentKey, target.ProviderKey, StringComparison.OrdinalIgnoreCase);
        // Пропуск только при смене поставщика: копия неполного транскрипта потеряла бы хвост ответа.
        // Смена модели у того же поставщика переписывает поля сразу — идущий ход доживает со старыми
        if (providerChanged && SessionStore.HasTurnInFlight(entry))
        {
            _log.LogInformation("Детский режим: чат {Session} идёт ходом — перенос на нового поставщика отложен до следующего PUT", sessionId);
            return false;
        }
        var transcriptMoved = providerChanged && MoveTranscript(sessionId, ownerId, entry, currentKey, target.ProviderKey);

        lock (_store.SaveLock)
        {
            info.PersonaId = target.PersonaId;
            info.Provider = target.ProviderKey;
            info.Model = target.Model;
            // Адаптер собран под прежний маршрут (env и сигнатура хода) — гасим лениво до следующего хода
            if (providerChanged && entry.Process is not null) entry.AdapterStale = true;
            _store.SaveSessions();
        }
        if (providerChanged)
        {
            await _store.BroadcastAsync(sessionId, new ProviderSwitchedMessage(target.ProviderKey, target.Model, null));
            _log.LogInformation("Детский режим: чат {Session} перенесён {From} → {To} (транскрипт: {Moved})",
                sessionId, currentKey, target.ProviderKey, transcriptMoved ? "перенесён" : "нечего переносить");
        }
        return true;
    }

    // Перенос транскрипта CLI из профиля текущей пары в профиль целевой. true — разговор
    // продолжится с контекстом (перенесён или живёт на устройстве), false — переносить нечего.
    // Сорвавшийся перенос найденного файла — исключение. Общий код MigrateProviderAsync и
    // RepinChildSubjectAsync: детский путь не должен расходиться с обычным по правилам переноса.
    private bool MoveTranscript(string sessionId, string ownerId, SessionEntry entry, string currentKey, string targetKey)
    {
        var transcriptMoved = false;
        // Локальный проект: транскрипт живёт на устройстве в единственном профиле CLI,
        // провайдера выбирает шлюз — разговор продолжится без переноса
        if (entry.Info.ClaudeSessionId is not null && !_route.TranscriptOnServer(entry.Info))
            transcriptMoved = true;
        else if (entry.Info.ClaudeSessionId is not null)
        {
            var hostCwd = _route.TryResolveCwd(entry.Info)
                ?? throw new InvalidOperationException("Не удалось определить рабочую папку чата");
            // У container-пользователя и корни (песочные профили), и cwd (контейнерный путь)
            // другие. Исключение ToRuntime (путь вне монтирований) намеренно не глушим:
            // операция явная, пользователь должен увидеть причину отказа (400)
            var cwd = _route.CwdForOwner(ownerId, hostCwd);
            // Корни поиска: профиль текущей пары (currentKey) и — когда Info.Provider является
            // ключом пула — собственный профиль подписки сессии. Для класса «ключ пула + модель
            // стороннего провайдера» (доработка ревью ccb3d43c) эти два корня расходятся: до SP1
            // подписочный env перетирал провайдерский и транскрипт писался в профиль sub-{key},
            // после — в профиль провайдера. Поиск по одному корню пропускает один из двух классов.
            var dstRoot = _route.ConfigRootFor(ownerId, targetKey);
            var srcRoots = new List<string> { _route.ConfigRootFor(ownerId, currentKey) };
            if (entry.Info.Provider is { Length: > 0 } provKey
                && _llmProviders.GetByKey(provKey) is null
                && _subscriptionPool.All.Any(s => s.Key == provKey && s.Enabled))
            {
                var subRoot = _route.ConfigRootFor(ownerId, provKey);
                if (!SameDir(subRoot, srcRoots[0])) srcRoots.Add(subRoot);
            }
            var srcRoot = srcRoots.FirstOrDefault(r =>
                TranscriptMigrator.FindTranscript(r, cwd, entry.Info.ClaudeSessionId) is not null);
            // Различаем «переносить нечего» и «перенос сорвался» — TryMigrate отдаёт false в
            // обоих случаях, а последствия разные. Транскрипта может не быть вовсе:
            // ClaudeSessionId выставляется на ЛЮБОМ первом ходе (включая служебный kickoff
            // онбординга), а файл к этому моменту мог не появиться, чат мог быть создан из
            // чужого resumeSessionId, либо его убрала плановая уборка CLI. Терять нечего —
            // меняем провайдера и пишем строку в лог. Сорвавшийся же перенос НАЙДЕННОГО файла
            // остаётся жёстким отказом: TryMigrate возвращает false и по таймауту копирования
            // живого файла (CopyFileShared, дедлайн 8 с), а это молчаливая потеря контекста.
            if (srcRoot is null)
                Console.Error.WriteLine(
                    $"[SessionManager] Чат {sessionId}: транскрипт {entry.Info.ClaudeSessionId} "
                    + $"не найден в {string.Join(" / ", srcRoots)} — переносить нечего, меняем провайдера");
            else if (SameDir(srcRoot, dstRoot))
                // Транскрипт уже в приёмнике (цель совпала с профилем, где он лежит): копия файла
                // в себя же ушла бы в ретраи CopyFileShared до дедлайна — тот же guard, что у
                // FallbackLlmSessionAdapter.TryMigrateTranscript. Переносить нечего, разговор
                // продолжится там же.
                Console.Error.WriteLine(
                    $"[SessionManager] Чат {sessionId}: транскрипт {entry.Info.ClaudeSessionId} "
                    + $"уже в {dstRoot} — копировать нечего, меняем провайдера");
            else if (!TranscriptMigrator.TryMigrate(srcRoot, dstRoot,
                         cwd, entry.Info.ClaudeSessionId, out var error))
                throw new InvalidOperationException($"Не удалось перенести транскрипт: {error}");
            else
                transcriptMoved = true;
        }
        return transcriptMoved;
    }

    // Сравнение корней профиля «одна папка или нет»: корни собирает разный код (ConfigRootFor,
    // песочная перепись раннера), хвостовые разделители различаются.
    private static bool SameDir(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
