using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Проактивность персон (флаг persona-proactive): «пишет первой» по расписанию.
// Фоновый тик раз в 30 с (по образцу TaskSchedulerService): по всем пользователям
// с включёнными флагами personas + persona-proactive перебираем персоны с
// Proactive.Enabled, и когда наступает момент расписания в таймзоне владельца —
// отправляем в закреплённый чат персоны триггер-сообщение с её инструкцией.
// Идемпотентность — LastFiredAt на самой персоне (переживает рестарт); отметка
// ставится ДО запуска (одна попытка на срабатывание). Уведомление о готовом
// ответе — по result её сессии (подписка на SessionManager.OnSessionMessage).
public sealed class PersonaProactiveService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    // Срабатываем только по «свежим» моментам расписания: защита от лавины
    // после долгого простоя сервера или включения флага
    internal static readonly TimeSpan FireWindow = TimeSpan.FromHours(24);

    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly FeatureFlagService _flags;
    private readonly SessionManager _sessions;
    private readonly PushService _push;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<PersonaProactiveService> _log;

    // Сессии, в которых сейчас идёт проактивный ход: sessionId → personaId
    // (по result шлём уведомление «персона написала вам»)
    private readonly ConcurrentDictionary<string, string> _inflight = new();

    public PersonaProactiveService(UserStore users, PersonaManager personas,
        FeatureFlagService flags, SessionManager sessions, PushService push,
        IHubContext<SessionHub> hub, ILogger<PersonaProactiveService> log)
    {
        _users = users;
        _personas = personas;
        _flags = flags;
        _sessions = sessions;
        _push = push;
        _hub = hub;
        _log = log;
        _sessions.OnSessionMessage += OnSessionMessageAsync;
    }

    public override void Dispose()
    {
        _sessions.OnSessionMessage -= OnSessionMessageAsync;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { _log.LogError(ex, "Ошибка тика проактивности персон"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Публичный для юнит-тестов: один проход по всем пользователям
    public Task TickAsync(DateTime nowUtc)
    {
        foreach (var user in _users.GetAll())
        {
            if (!_flags.IsEnabled(user.Id, FeatureFlagKeys.Personas) ||
                !_flags.IsEnabled(user.Id, FeatureFlagKeys.PersonaProactive))
                continue;

            var tz = TaskDueCalculator.ResolveTimeZone(user.TimeZone);
            foreach (var persona in _personas.GetByOwner(user.Id))
            {
                if (persona.Proactive is not { Enabled: true } cfg) continue;
                if (!ShouldFire(cfg, tz, nowUtc)) continue;

                // Помечаем СРАЗУ — не даём дублей между тиками; тяжёлая работа в фоне,
                // чтобы не блокировать тик остальных пользователей
                _personas.MarkProactiveFired(persona.Id, nowUtc);
                var ownerId = user.Id;
                _ = Task.Run(async () =>
                {
                    try { await FireAsync(ownerId, persona.Id, tz, nowUtc); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Проактивное срабатывание персоны {PersonaId} не удалось", persona.Id);
                    }
                });
            }
        }
        return Task.CompletedTask;
    }

    // Отправка триггера: найти/создать закреплённый чат персоны и написать в него
    private async Task FireAsync(string ownerId, string personaId, TimeZoneInfo tz, DateTime nowUtc)
    {
        var persona = _personas.Get(personaId, ownerId);
        if (persona?.Proactive is not { } cfg) return;

        // Закреплённый чат: жив и всё ещё принадлежит этой персоне — иначе новый
        var chat = cfg.SessionId is not null ? _sessions.GetById(cfg.SessionId) : null;
        if (chat is null || chat.PersonaId != personaId)
        {
            var title = string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : persona.Role;
            chat = await _sessions.CreatePersonaChatAsync(ownerId, personaId, ClaudeMode.Auto,
                name: $"{title}: по расписанию");
            _personas.SetProactiveSession(personaId, chat.Id);
        }

        _inflight[chat.Id] = personaId;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        await _sessions.SendMessageAsync(chat.Id, BuildTriggerPrompt(persona, localNow), []);
        _log.LogInformation("Проактивный триггер персоны {PersonaId} отправлен в чат {ChatId}", personaId, chat.Id);
    }

    // По завершении хода проактивной сессии — уведомление «персона написала вам»
    private async Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage) return;
        if (!_inflight.TryRemove(session.Id, out var personaId)) return;

        var persona = _personas.GetByIdInternal(personaId);
        if (persona is null) return;

        var label = string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : $"{persona.Role} ({persona.Name})";
        var notification = new NotificationMessage(
            Title: $"{label} написала вам",
            Body: "Есть новое сообщение по расписанию — открой чат",
            Url: $"/#/chats/{session.Id}",
            Kind: "claude");
        await _hub.Clients.Group("user_" + persona.OwnerId).SendAsync("message", notification);
        await _push.SendToUserAsync(persona.OwnerId, notification);
    }

    // ─── Чистые предикаты расписания (юнит-тесты) ────────────────────────────

    // Последний момент расписания ≤ now в таймзоне юзера (UTC); null — момента нет
    // (невалидное время / у weekly не выбраны дни). Перебор ≤ 7 дней назад.
    internal static DateTime? LastOccurrenceUtc(PersonaProactiveConfig cfg, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (!TimeOnly.TryParseExact(cfg.Time, "HH:mm", out var time)) return null;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        for (var back = 0; back <= 7; back++)
        {
            var day = localNow.Date.AddDays(-back);
            if (!DayMatches(cfg, day.DayOfWeek)) continue;
            var localAt = day + time.ToTimeSpan();
            if (localAt > localNow) continue;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localAt, DateTimeKind.Unspecified), tz);
        }
        return null;
    }

    // Пора ли срабатывать: включено, инструкция непуста, момент наступил,
    // не старше FireWindow и по нему ещё не срабатывали
    internal static bool ShouldFire(PersonaProactiveConfig cfg, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Instruction)) return false;
        var occurrence = LastOccurrenceUtc(cfg, tz, nowUtc);
        if (occurrence is null) return false;
        if (nowUtc - occurrence > FireWindow) return false;
        return cfg.LastFiredAt is null || cfg.LastFiredAt < occurrence;
    }

    // Триггер-сообщение персоне: явный маркер расписания + её инструкция
    internal static string BuildTriggerPrompt(Persona persona, DateTime localNow)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"⏰ Сработал триггер по расписанию ({localNow:dd.MM.yyyy HH:mm}, местное время пользователя).");
        sb.AppendLine("Выполни свою инструкцию и напиши пользователю результат от своего лица. " +
                      "Пользователь сейчас не в чате — сообщение должно быть самодостаточным.");
        sb.AppendLine();
        sb.AppendLine($"Инструкция: {persona.Proactive?.Instruction?.Trim()}");
        return sb.ToString();
    }

    // День недели подходит под расписание (Weekly — по списку ISO-дней 1=Пн … 7=Вс)
    private static bool DayMatches(PersonaProactiveConfig cfg, DayOfWeek dow) => cfg.Type switch
    {
        PersonaScheduleType.Daily => true,
        PersonaScheduleType.Weekdays => dow is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        PersonaScheduleType.Weekly => cfg.Weekdays is { Count: > 0 } days && days.Contains(IsoDay(dow)),
        _ => false,
    };

    private static int IsoDay(DayOfWeek dow) => dow == DayOfWeek.Sunday ? 7 : (int)dow;
}
