using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Автоправило архивации чатов (план «Архив чатов» v4, шаг 6, флаг chat-auto-archive):
// раз в час убирает в архив чаты без активности дольше порога. Архив ПРЯЧЕТ чат, а не
// удаляет (история и claudeSessionId целы), отбор — SessionManager.GetArchiveRuleCandidates
// (единая точка для тика и превью), потолок 200 за проход и один ArchiveBatchId — откат
// из уведомления возвращает ровно эту пачку.
//
// Накопившиеся старые чаты правило САМО не разгребает (решение человека): пока у владельца
// не нажата кнопка «Применить сейчас» (User.ArchiveRuleFirstRunAt == null), фоновый тик
// его не архивирует вовсе — первый проход запускает только кнопка, дальше правило фоном.
public class ChatArchiveService(
    SessionManager sessions,
    ProjectManager projects,
    UserStore users,
    FeatureFlagService flags,
    NotificationService notifications,
    ILogger<ChatArchiveService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    // Первый тик с задержкой: после старта не дёргаем стор немедленно (продукт
    // перезапускается выкатками по нескольку раз в день), часовой ритм не привязан
    // к моменту подъёма процесса.
    private static readonly TimeSpan FirstTickDelay = TimeSpan.FromMinutes(2);

    // Потолок пачки одного прохода (пре-мортем №2 — «молчаливое исчезновение сотен чатов»)
    public const int MaxBatchSize = 200;

    // Тик и кнопка «Применить сейчас» сериализуются: одновременные проходы дважды писали
    // бы стор и дрались за чаты, уже взятые в пачку.
    private readonly SemaphoreSlim _passLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(FirstTickDelay, ct); }
        catch (OperationCanceledException) { /* остановка приложения */ return; }
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика автоправила архивации чатов"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Один проход по всем владельцам. Публичный для юнит-тестов: nowUtc — параметром,
    // никаких ожиданий на час вперёд (на CI-Linux это мигающий тест). Идемпотентен —
    // заархивированные чаты не подпадают под отбор повторно (IsArchived в предикате).
    public async Task TickAsync(DateTime nowUtc)
    {
        foreach (var owner in users.GetAll())
        {
            try { await RunPassAsync(owner, nowUtc, requireFirstRun: true); }
            catch (Exception ex)
            {
                log.LogError(ex, "Ошибка прохода автоправила архивации у владельца {OwnerId}", owner.Id);
            }
        }
    }

    // Кнопка «Применить сейчас» (POST /api/chats/archive-run): ровно один проход владельца
    // по всем его сферам, включая накопившиеся залежи, — гейт первого прохода не действует,
    // кнопка его и снимает (проставляет ArchiveRuleFirstRunAt). Флаг уже проверен контроллером,
    // здесь защита от вызова без проверки.
    // Возвращает (число убранных чатов, batchId прохода; batchId null — пачка пуста).
    public async Task<(int Archived, string? BatchId)> RunNowAsync(string ownerId, DateTime nowUtc)
    {
        var owner = users.GetById(ownerId)
            ?? throw new InvalidOperationException($"Пользователь не найден: {ownerId}");
        var (archived, batchId) = await RunPassAsync(owner, nowUtc, requireFirstRun: false);
        users.SetArchiveRuleFirstRunAt(ownerId, nowUtc);
        return (archived, batchId);
    }

    // Проход по сферам одного владельца: чаты вне проектов (личный порог) и каждый проект
    // (порог проекта ?? личный — «null = наследовать»). Проект без порога ни прямо, ни
    // по наследию — не трогаем. Флаг — по владельцу (проектной сессии OwnerId нет, владелец
    // резолвится через проект — отбор уже получил проект из списка владельца).
    private async Task<(int Archived, string? BatchId)> RunPassAsync(User owner, DateTime nowUtc, bool requireFirstRun)
    {
        if (!flags.IsEnabled(owner.Id, FeatureFlagKeys.ChatAutoArchive)) return (0, null);
        if (requireFirstRun && owner.ArchiveRuleFirstRunAt is null) return (0, null);

        var candidates = new List<Session>();
        if (owner.ArchiveAfterDays is int personalDays)
            candidates.AddRange(sessions.GetArchiveRuleCandidates(owner.Id, projectId: null, personalDays, nowUtc));
        foreach (var project in projects.GetByOwner(owner.Id))
        {
            if ((project.ArchiveAfterDays ?? owner.ArchiveAfterDays) is not int days) continue;
            candidates.AddRange(sessions.GetArchiveRuleCandidates(owner.Id, project.Id, days, nowUtc));
        }

        // Один batchId на проход, потолок 200, самые старые первыми — пачка
        // детерминирована и справедлива к «самым остывшим».
        var batch = candidates
            .DistinctBy(s => s.Id)
            .OrderBy(s => s.UpdatedAt)
            .Take(MaxBatchSize)
            .Select(s => s.Id)
            .ToList();
        if (batch.Count == 0) return (0, null);

        var batchId = Guid.NewGuid().ToString("N");
        await _passLock.WaitAsync();
        try
        {
            await sessions.ArchiveBatchAsync(batch, batchId);
        }
        finally { _passLock.Release(); }

        log.LogInformation("Автоправило архивации: владелец {OwnerId}, проход {BatchId}, убрано чатов {Count}",
            owner.Id, batchId, batch.Count);
        await NotifyOwnerAsync(owner.Id, batch.Count);
        return (batch.Count, batchId);
    }

    // Одно агрегированное уведомление на проход (пре-мортем №2) со ссылкой в список
    // чатов: отдельного раздела «Архив» больше нет, архив — режим списка (переключатель
    // «Архивные» в тулбаре), поэтому и текст зовёт туда. Откат пачки — по batchId (POST /api/chats/archive-batch/{batchId}/restore);
    // фронт берёт batchId из карточек архивных чатов. Без push: уборка списка — не пожар.
    private async Task NotifyOwnerAsync(string ownerId, int count)
    {
        try
        {
            await notifications.SendAsync(ownerId, new CreateNotificationRequest
            {
                Kind = "info",
                Type = "chat_auto_archive",
                Title = "Автоправило архива чатов",
                Body = $"Без активности дольше порога в архив убрано {PluralChats(count)}. " +
                       "Вернуть их можно в списке чатов — переключатель «Архивные».",
                Url = "#/chats",
                Tag = "Архив",
                Source = "Автоправило",
            }, sendPush: false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Уведомление о проходе автоправила архивации не доставлено ({OwnerId})", ownerId);
        }
    }

    // Русская плюрализация для текста уведомления: 1 чат / 2 чата / 5 чатов
    internal static string PluralChats(int n) =>
        n % 10 == 1 && n % 100 != 11 ? $"{n} чат"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 10 or >= 20 ? $"{n} чата"
        : $"{n} чатов";
}
