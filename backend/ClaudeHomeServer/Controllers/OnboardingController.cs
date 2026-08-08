using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Personas;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

// Онбординги (фича default-personas-onboarding): обязательные чат-интервью первого входа
// (личная дефолт-персона) и создания проекта (персона-руководитель). Онбординг — обычная
// сессия существующего рантайма со спец-промптом (Session.OnboardingKind → врезка в
// SessionManager.BuildPersonaLayer); финализация — make-default из этой сессии
// (PersonasController.MakeDefault → FinalizeOnboardingAsync).
[ApiController]
[Authorize]
[Route("api/onboarding")]
public class OnboardingController(SessionManager sessions, UserStore users,
    ProjectManager projects, PersonaManager personas,
    PersonaDraftService drafts, ICheapTextRunner cheap, IConfiguration config,
    FalImageService falImage, IHubContext<SessionHub> hub, ILogger<OnboardingController> log)
    : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Идемпотентность double-start (две вкладки, повторный логин): создание сессии и запись
    // OnboardingSessionId — атомарно под per-ключевым семафором, иначе двойной start породил
    // бы две сессии и перезапись id. Статический реестр: контроллер per-request, а гонка
    // межзапросная. Семафоры копеечные и не чистятся — ключей столько же, сколько
    // пользователей и проектов.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static SemaphoreSlim LockFor(string key) =>
        Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    // Старт (или резюм) онбординга пользователя: чат вне проекта с OnboardingKind="user",
    // ведёт системный «Мастер настройки» (персоны у сессии нет). Живая сессия из
    // User.OnboardingSessionId возвращается как есть; удалённая — заменяется новой.
    [HttpPost("user/start")]
    public async Task<IActionResult> StartUser()
    {
        var gate = LockFor("user:" + UserId);
        await gate.WaitAsync(HttpContext.RequestAborted);
        Session? chat = null;
        var created = false;
        IActionResult? earlyReturn = null;
        try
        {
            var me = users.GetById(UserId);
            if (me is null)
                earlyReturn = Unauthorized();
            // Резюм: живая сессия возвращается как есть — kickoff не запускаем (история
            // уже есть, повторная затравка задублировала бы первую реплику мастера)
            else if (me.OnboardingSessionId is { } sid && sessions.GetOwned(sid, UserId) is { } existing)
                chat = existing;
            else
            {
                chat = await sessions.CreateChatAsync(UserId, ClaudeMode.Auto,
                    name: "Настройка системы", onboardingKind: OnboardingKinds.User);
                users.SetOnboardingSession(UserId, chat.Id);
                created = true;
            }
        }
        catch (InvalidOperationException ex) { earlyReturn = BadRequest(new { error = ex.Message }); }
        finally { gate.Release(); }

        if (earlyReturn is not null) return earlyReturn;
        if (created)
        {
            ServerMetrics.RecordIntroStarted();
            await KickoffFirstTurnAsync(chat!.Id);
        }
        return Ok(chat);
    }

    // Старт (или резюм) онбординга проекта: проектная сессия с личной дефолт-персоной
    // владельца и OnboardingKind="project". Без личного дефолта — 400 (сначала онбординг
    // пользователя). Живая сессия из Project.OnboardingSessionId возвращается как есть.
    [HttpPost("project/{projectId}/start")]
    public async Task<IActionResult> StartProject(string projectId)
    {
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != UserId) return NotFound();

        var gate = LockFor("project:" + projectId);
        await gate.WaitAsync(HttpContext.RequestAborted);
        Session? session = null;
        var created = false;
        IActionResult? earlyReturn = null;
        try
        {
            // Перечитываем проект под локом: параллельный start мог уже записать сессию
            project = projects.GetById(projectId);
            if (project is null)
                earlyReturn = NotFound();
            // Резюм: живая сессия возвращается как есть — kickoff не запускаем
            else if (project.OnboardingSessionId is { } sid && sessions.GetOwned(sid, UserId) is { } existing)
                session = existing;
            else
            {
                // Ведёт личная дефолт-персона; сирота (персона удалена) — как отсутствие дефолта
                var defaultId = users.GetById(UserId)?.DefaultPersonaId;
                var defaultPersona = defaultId is null ? null : personas.Get(defaultId, UserId);
                if (defaultPersona is null)
                    earlyReturn = BadRequest(new { error = "Сначала пройдите личный онбординг: у вас ещё нет дефолт-персоны" });
                else
                {
                    session = await sessions.CreateAsync(projectId, ClaudeMode.Auto,
                        name: "Знакомство с проектом", personaId: defaultPersona.Id,
                        onboardingKind: OnboardingKinds.Project);
                    projects.SetOnboardingSession(projectId, session.Id);
                    created = true;
                }
            }
        }
        catch (InvalidOperationException ex) { earlyReturn = BadRequest(new { error = ex.Message }); }
        finally { gate.Release(); }

        if (earlyReturn is not null) return earlyReturn;
        if (created)
        {
            ServerMetrics.RecordIntroStarted();
            await KickoffFirstTurnAsync(session!.Id);
        }
        return Ok(session);
    }

    // Первый ход собеседника (Мастер настройки / дефолт-персона) для СВЕЖЕСОЗДАННОЙ
    // онбординг-сессии — иначе гейт открывается «немым». Серверная директива (не баллон
    // пользователя): ответ стримится в ленту. Вызывается ВНЕ per-ключевого семафора —
    // повторный start (вторая вкладка) не ждёт старта процесса собеседника, а сразу
    // получает уже созданную сессию. Сбой kickoff не должен рушить start: сессия уже
    // создана и зафиксирована, при повторном открытии гейта вернётся как резюм.
    private async Task KickoffFirstTurnAsync(string sessionId)
    {
        try
        {
            await sessions.SendMessageAsync(sessionId, OnboardingPrompts.KickoffDirective, [],
                systemDirective: true);
        }
        catch (Exception ex)
        {
            // Сбой kickoff не должен рушить start (сессия уже создана — резюм при повторном
            // входе), но молчание недопустимо: в проде без лога причину не найти
            log.LogError(ex, "Сбой kickoff онбординг-сессии {SessionId}", sessionId);
        }
    }

    // Страховка «Применить итоги разговора» (план 3.2): если модель не довела интервью до
    // personas_set_default, транскрипт живой онбординг-сессии → черновик через
    // PersonaDraftService → применение к резолвленной заготовке-ассистенту → аватар → та же
    // финализация, что у make-default из онбординг-сессии (PersonasController.FinalizeOnboardingAsync,
    // user-ветка). Второй персоны НЕ плодим — правим заготовку (план §3в, вопрос 3).
    //
    // Собственный семафор (apply:{user}, НЕ семафор провижна и НЕ start): здесь удерживается
    // внешний вызов fal на 5-20с, и он не должен блокировать ни провижн ассистента, ни старт.
    // Контракт: 404 — нет живой сессии или живой заготовки; 502 — черновик не получился;
    // 200 — заготовка обновлена, IntroCompletedAt проставлен, AssistantPersonaId обнулён.
    [HttpPost("user/apply-transcript")]
    public async Task<IActionResult> ApplyTranscript()
    {
        var me = users.GetById(UserId);
        // Живая онбординг-сессия пользователя (404, если её нет): OnboardingSessionId указывает
        // на удалённую сессию → GetOwned по мёртвому id даёт null → 404.
        if (me?.OnboardingSessionId is not { } sid || sessions.GetOwned(sid, UserId) is not { } onboarding)
            return NotFound(new { error = "Нет живой сессии знакомства" });

        var gate = LockFor("apply:" + UserId);
        await gate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            // Перечитываем под локом: конкурентный вызов мог превратить/удалить заготовку.
            me = users.GetById(UserId);
            if (me is null) return NotFound();
            // Резолвленная заготовка-ассистент (404, если её нет). Резолв, а не проверка на
            // null — мёртвый AssistantPersonaId всюду резолвится (план §2, вопрос 3).
            var assistant = me.AssistantPersonaId is { } aid ? personas.Get(aid, UserId) : null;
            if (assistant is null)
                return NotFound(new { error = "Нет заготовки-ассистента для применения итогов" });

            // 1. Транскрипт сессии → текстовый промпт (перенос OnboardingPage.transcriptToPrompt,
            //    OnboardingPage.tsx:30-43; цель всегда личная — endpoint только пользовательский).
            var history = await sessions.GetHistoryAsync(onboarding.Id);
            var transcript = BuildTranscriptPrompt(history);

            // 2. Черновик всех полей одним one-shot вызовом (тот же путь, что ai/quick-create),
            //    с одним повтором при сбое/некорректном JSON.
            var (draft, draftError) = await GenerateDraftAsync(transcript);
            if (draft is null || string.IsNullOrWhiteSpace(draft.Name))
                return StatusCode(502, new { error = draftError });

            // 3. Применение черновика к заготовке (новую не создаём): имя/роль/описание/
            //    характер-контракт/приветствие/цвет. Прочие поля (scope/access/specialty/модель)
            //    не трогаем — null в partial-update значит «без изменений».
            var contract = new PersonaContract
            {
                Character = draft.Character,
                Tone = draft.Tone,
                MustDo = draft.MustDo,
                MustNot = draft.MustNot,
                OutputFormat = draft.OutputFormat,
                SpeechExamples = draft.SpeechExamples,
            };
            var color = ValidColor(draft.Color) ? draft.Color : "orange";
            var persona = personas.Update(assistant.Id, UserId,
                draft.Name, draft.Role, draft.Description,
                systemPrompt: null, model: null, effort: null, scope: null, projectId: null,
                color, draft.Greeting, memoryEnabled: null, contract: contract);

            // 4. Фото-аватар — автоматически, best-effort (при сбое остаются инициалы).
            //    Зеркалит PersonasController.TryAutoGenerateAvatarAsync.
            persona = await TryAutoGenerateAvatarAsync(persona, draft.AvatarPrompt);

            // 5. Финализация — та же, что у make-default из онбординг-сессии (user-ветка):
            //    «просыпание» персоны в этом же чате, фиксация IntroCompletedAt, снятие статуса
            //    заготовки, очистка OnboardingSessionId, событие onboarding_completed. Досев
            //    профиля здесь не нужен — заготовка получила его при провижне (идемпотентен).
            sessions.SetPersona(onboarding.Id, UserId, persona.Id);
            users.SetIntroCompleted(UserId, DateTime.UtcNow);
            users.SetAssistantPersona(UserId, null);
            users.SetOnboardingSession(UserId, null);
            await sessions.BroadcastSessionMessageAsync(onboarding.Id,
                new OnboardingCompletedMessage(OnboardingKinds.User, persona.Id, onboarding.ProjectId));
            ServerMetrics.RecordIntroCompleted();

            // Стор персон обновляет карточку по «updated», метка/гейт гасятся по концу хода.
            await hub.Clients.Group("user_" + UserId)
                .SendAsync("message", new PersonasChangedMessage("updated", persona.Id));
            return Ok(persona);
        }
        catch (OperationCanceledException) { return BadRequest(new { error = "Операция отменена" }); }
        finally { gate.Release(); }
    }

    // Черновик персоны одним one-shot вызовом с одним повтором. Возвращает (draft, error):
    // draft — распознанный черновик (null, если оба попытки провалились), error — текст для 502.
    private async Task<(DraftRaw? Draft, string? Error)> GenerateDraftAsync(string prompt)
    {
        var model = config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string raw;
            try
            {
                raw = await cheap.RunAsync(LocalActionCatalog.PersonaQuickCreate,
                    drafts.BuildDraftPrompt(prompt), model, UserId,
                    jsonFormat: "json", ct: HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "apply-transcript: one-shot упал (попытка {Attempt})", attempt);
                if (attempt == 2) return (null, $"Не удалось сгенерировать черновик: {ex.Message}");
                continue;
            }
            var draft = drafts.ParseDraft(raw);
            if (draft is not null && !string.IsNullOrWhiteSpace(draft.Name))
                return (draft, null);
            log.LogWarning("apply-transcript: черновик не распознан (попытка {Attempt}); сырой ответ: {Raw}",
                attempt, raw.Length > 600 ? raw[..600] + "…" : raw);
        }
        return (null, "Модель не вернула корректный черновик — попробуйте ещё раз");
    }

    // Транскрипт онбординг-сессии → промпт для черновика. Перенос OnboardingPage.transcriptToPrompt
    // (OnboardingPage.tsx:30-43): реплики человека (user_message, кроме служебных директив) и
    // интервьюера (text), склейка через перевод строки, потолок 12_000 символов, как на фронте.
    private static string BuildTranscriptPrompt(IReadOnlyList<StoredMessage> history)
    {
        var lines = new List<string>();
        foreach (var m in history)
        {
            if (m is StoredUserMessage u && u.SystemDirective is not true)
                lines.Add($"Пользователь: {u.Text}");
            else if (m is StoredTextMessage t)
                lines.Add($"Интервьюер: {t.Text}");
        }
        var talk = string.Join('\n', lines);
        if (talk.Length > 12_000) talk = talk[..12_000];
        var goal = "личного ассистента-координатора пользователя (его персону по умолчанию)";
        return $"По этому разговору-интервью придумай {goal}: имя, роль, характер, приветствие.\n\n{talk}";
    }

    // Фото-промпт аватара по умолчанию — из имени и описания персоны.
    // Зеркалит PersonasController.BuildAvatarPrompt.
    private static string BuildAvatarPrompt(Persona persona)
    {
        var who = string.IsNullOrWhiteSpace(persona.Description)
            ? persona.Name
            : $"{persona.Name}, {persona.Description}";
        return $"Photorealistic portrait photo of {who}. Head and shoulders, looking at camera, " +
               "clean solid background, soft studio lighting, natural skin, friendly expression, " +
               "high detail, sharp focus, square crop.";
    }

    // Фото-аватар автоматически, best-effort (не критично: при сбое или невключённом fal
    // персона остаётся с инициалами). Зеркалит PersonasController.TryAutoGenerateAvatarAsync.
    private async Task<Persona> TryAutoGenerateAvatarAsync(Persona persona, string? avatarPrompt)
    {
        if (!falImage.Enabled) return persona;
        try
        {
            var prompt = string.IsNullOrWhiteSpace(avatarPrompt)
                ? BuildAvatarPrompt(persona)
                : $"Photorealistic portrait photo. {avatarPrompt.Trim()}";
            var images = await falImage.GenerateManyAsync(prompt, 1);
            if (images.Count == 0) return persona;
            var dir = Path.Combine(personas.AssetsDir, persona.Id);
            Directory.CreateDirectory(dir);
            var fileName = $"avatar-{Guid.NewGuid():N}{ImageAssetHelper.ExtFor(images[0].ContentType)}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), images[0].Bytes);
            return personas.SetAvatarImage(persona.Id, persona.OwnerId, fileName);
        }
        catch
        {
            return persona;
        }
    }

    private static bool ValidColor(string? c) =>
        c is "yellow" or "orange" or "blue" or "green" or "purple" or "red" or "brown" or "cyan" or "pink";
}
