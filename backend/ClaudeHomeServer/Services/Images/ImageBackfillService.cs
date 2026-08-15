using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Images;

/// <summary>Итог прогона очереди: по владельцу или по всему инстансу.</summary>
public sealed record ImageBackfillSummary(int Generated, int Skipped, int Failed)
{
    public int Total => Generated + Skipped + Failed;

    public static ImageBackfillSummary operator +(ImageBackfillSummary a, ImageBackfillSummary b) =>
        new(a.Generated + b.Generated, a.Skipped + b.Skipped, a.Failed + b.Failed);

    public static readonly ImageBackfillSummary Empty = new(0, 0, 0);
}

// Картинка догнала сущность — карточка проекта/персоны обновляется без перезагрузки.
// Kind — ImageBackfillKinds.*, EntityId — id проекта или персоны.
public record ImageBackfilledMessage(string Kind, string EntityId)
    : ServerMessage("image_backfilled");

/// <summary>
/// Догоняющая генерация картинок: проект или персона, созданные при выключенном (или
/// упавшем) генераторе, получают иконку/аватар позже сами. Образец — ProjectBackgroundBackfill.
///
/// Идемпотентность держится на самой очереди: заявка снимается, как только у сущности
/// появилась картинка — неважно, сгенерировали её мы, выбрал человек из кандидатов или
/// загрузил свою. Поэтому повторный прогон — no-op, генератор не дёргается.
/// </summary>
public sealed class ImageBackfillService(
    ImageBackfillStore store, ImageGenerationService images, ProjectManager projects,
    PersonaManager personas, IHubContext<SessionHub> hub, IHostApplicationLifetime lifetime,
    ILogger<ImageBackfillService> log)
{
    // Не больше 2 генераций одновременно на инстанс: залп заявок упрётся в rate limit
    // провайдера, и половина сущностей получит отказ по вине очереди, а не генератора
    private readonly SemaphoreSlim _slots = new(2, 2);

    // Прогоны владельцев, идущие прямо сейчас. Один владелец = один прогон, а внутри него
    // заявки идут последовательно — это и есть потолок «не больше 1 генерации на владельца»
    private readonly ConcurrentDictionary<string, byte> _running = new();

    // Повторяем только транзиентные отказы: провайдер не отдал картинку (отвалился ключ,
    // таймаут, rate limit), сеть или не записался файл. Всё остальное (сущность чужая,
    // неизвестный вид заявки) повтором не лечится — заявка снимается.
    private static readonly string[] TransientReasons = ["no-image", "network", "io"];
    private const int MaxAttempts = 2;
    // Пожизненный потолок попыток на заявку. Внутрипрогонный MaxAttempts гасит случайный
    // таймаут здесь и сейчас; этот — не даёт мёртвому провайдеру долбиться каждый тик.
    private const int MaxTotalAttempts = 5;

    /// <summary>Пауза перед повторной попыткой; в тестах ужимается.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Поставить сущность в очередь на догоняющую генерацию. Зовут контроллеры, когда
    /// картинку сделать не удалось (генератор выключен или вернул отказ). Идемпотентно.
    /// </summary>
    public void Enqueue(string kind, string entityId, string ownerId, string? prompt = null)
    {
        if (store.Enqueue(kind, entityId, ownerId, prompt))
            log.LogInformation("Картинка догонит позже: {Kind} {Entity} (владелец {Owner})",
                kind, entityId, ownerId);
    }

    /// <summary>
    /// Запустить прогон в фоне и сразу вернуть управление: триггер — HTTP-запрос
    /// (в настройках появился или сменился провайдер), а генерация в него не укладывается.
    /// ownerId пуст — прогон по всем владельцам с заявками.
    /// </summary>
    public void KickOff(string? ownerId = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ownerId)) await RunAllAsync(lifetime.ApplicationStopping);
                else await RunAsync(ownerId, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) { /* остановка приложения */ }
            catch (Exception ex) { log.LogError(ex, "Догоняющая генерация картинок сорвалась"); }
        });
    }

    /// <summary>Прогон по всем владельцам, у которых есть заявки (старт сервера и тик).</summary>
    public async Task<ImageBackfillSummary> RunAllAsync(CancellationToken ct = default)
    {
        var owners = store.Owners;
        if (owners.Count == 0) return ImageBackfillSummary.Empty;

        // Владельцы идут параллельно, но общий потолок держит _slots
        var results = await Task.WhenAll(owners.Select(id => RunAsync(id, ct)));
        return results.Aggregate(ImageBackfillSummary.Empty, (a, b) => a + b);
    }

    public async Task<ImageBackfillSummary> RunAsync(string ownerId, CancellationToken ct = default)
    {
        if (!_running.TryAdd(ownerId, 0))
        {
            log.LogInformation("Догоняющая генерация ({Owner}) уже идёт — повторный запуск пропущен", ownerId);
            return ImageBackfillSummary.Empty;
        }

        try
        {
            var pending = store.ByOwner(ownerId);
            if (pending.Count == 0) return ImageBackfillSummary.Empty;

            // Провайдера нет — заявки НЕ трогаем вовсе: сжечь потолок попыток на выключенном
            // генераторе значит потерять картинку навсегда, а очередь заведена ровно ради
            // этого случая. Прогон повторится, когда провайдер появится (KickOff/тик).
            if (!images.AnyEnabled)
            {
                log.LogInformation(
                    "Догоняющая генерация ({Owner}): заявок {Count}, но генератор картинок выключен — ждём",
                    ownerId, pending.Count);
                return ImageBackfillSummary.Empty;
            }

            log.LogInformation("Догоняющая генерация ({Owner}): заявок {Count}", ownerId, pending.Count);

            var summary = ImageBackfillSummary.Empty;
            foreach (var request in pending)
            {
                ct.ThrowIfCancellationRequested();
                // Вид заявки — это и есть место генерации (ImageBackfillKinds.* = ImagePlaces.*),
                // а у места свой провайдер: он мог оказаться выключенным, пока соседнее место
                // работает. Тогда заявку тоже не трогаем — ждём, как и при полном отсутствии
                // провайдеров.
                if (!images.EnabledFor(request.Kind))
                {
                    log.LogInformation(
                        "Догоняющая генерация ({Owner}): у места {Place} нет включённого провайдера — заявка ждёт",
                        ownerId, request.Kind);
                    continue;
                }
                summary += await ProcessAsync(request, ct);
            }

            if (summary.Total > 0)
                log.LogInformation(
                    "Догоняющая генерация ({Owner}) завершена: сгенерировано {Generated}, пропущено {Skipped}, упало {Failed}",
                    ownerId, summary.Generated, summary.Skipped, summary.Failed);
            return summary;
        }
        finally
        {
            _running.TryRemove(ownerId, out _);
        }
    }

    // Одна заявка: снять, если картинка уже не нужна, иначе попытки с повтором транзиентного отказа
    private async Task<ImageBackfillSummary> ProcessAsync(ImageBackfillRequest request, CancellationToken ct)
    {
        var target = Resolve(request);
        if (target is null)
        {
            // Сущность удалена (или сменила владельца) — догонять нечего
            store.Remove(request.Kind, request.EntityId);
            log.LogInformation("Заявка снята: {Kind} {Entity} — сущность не найдена",
                request.Kind, request.EntityId);
            return new ImageBackfillSummary(0, 1, 0);
        }

        if (target.HasImage)
        {
            // Картинка появилась мимо очереди: человек выбрал кандидата или загрузил свою
            store.Remove(request.Kind, request.EntityId);
            log.LogInformation("Заявка снята: у {Kind} {Entity} картинка уже есть",
                request.Kind, request.EntityId);
            return new ImageBackfillSummary(0, 1, 0);
        }

        if (request.Attempts >= MaxTotalAttempts)
        {
            store.Remove(request.Kind, request.EntityId);
            log.LogWarning("Заявка снята: {Kind} {Entity} — потолок попыток исчерпан ({Reason})",
                request.Kind, request.EntityId, request.LastFailReason);
            return new ImageBackfillSummary(0, 1, 0);
        }

        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? target.DefaultPrompt : request.Prompt!;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var reason = await TryGenerateAsync(request, prompt, ct);
            if (reason is null)
            {
                store.Remove(request.Kind, request.EntityId);
                log.LogInformation("Картинка догнала {Kind} {Entity} «{Name}»",
                    request.Kind, request.EntityId, target.Name);
                await NotifyAsync(request, ct);
                return new ImageBackfillSummary(1, 0, 0);
            }

            if (!TransientReasons.Contains(reason))
            {
                store.Remove(request.Kind, request.EntityId);
                log.LogWarning("Заявка снята: {Kind} {Entity} — {Reason}, повтор не поможет",
                    request.Kind, request.EntityId, reason);
                return new ImageBackfillSummary(0, 0, 1);
            }

            var attempts = store.MarkFailed(request.Kind, request.EntityId, reason);
            if (attempt == MaxAttempts || attempts >= MaxTotalAttempts) break;

            log.LogInformation("Картинка для {Kind} {Entity}: попытка {Attempt} не удалась ({Reason}), повторяем",
                request.Kind, request.EntityId, attempt, reason);
            await Task.Delay(RetryDelay, ct);
        }

        return new ImageBackfillSummary(0, 0, 1);
    }

    // Одна попытка: null — успех, иначе причина отказа
    private async Task<string?> TryGenerateAsync(ImageBackfillRequest request, string prompt, CancellationToken ct)
    {
        GeneratedImage? image;
        await _slots.WaitAsync(ct);
        try
        {
            image = await images.GenerateAsync(request.Kind, prompt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Драйверы отдают отказ пустым списком; исключение сюда доходит только от
            // сорвавшегося транспорта — это транзиентно
            log.LogWarning(ex, "Генерация картинки для {Kind} {Entity} сорвалась",
                request.Kind, request.EntityId);
            return "network";
        }
        finally
        {
            _slots.Release();
        }

        if (image is null) return "no-image";

        try
        {
            await SaveAsync(request, image);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Не удалось записать картинку для {Kind} {Entity}",
                request.Kind, request.EntityId);
            return "io";
        }
        catch (KeyNotFoundException)
        {
            // Сущность удалили, пока шла генерация — повторять нечего
            return "entity-gone";
        }
    }

    // Сохранение — тем же путём, что и ручной выбор кандидата: файл в каталоге ассетов
    // сущности + перевод иконки/аватара в режим картинки
    private async Task SaveAsync(ImageBackfillRequest request, GeneratedImage image)
    {
        var ext = ImageAssetHelper.ExtFor(image.ContentType);
        if (request.Kind == ImageBackfillKinds.ProjectIcon)
        {
            var dir = Path.Combine(projects.IconsDir, request.EntityId);
            Directory.CreateDirectory(dir);
            var name = $"icon-{Guid.NewGuid():N}{ext}";   // cache-busting, как в SelectIcon
            await File.WriteAllBytesAsync(Path.Combine(dir, name), image.Bytes);
            projects.SetIconImage(request.EntityId, name);
        }
        else
        {
            var dir = Path.Combine(personas.AssetsDir, request.EntityId);
            Directory.CreateDirectory(dir);
            var name = $"avatar-{Guid.NewGuid():N}{ext}";
            await File.WriteAllBytesAsync(Path.Combine(dir, name), image.Bytes);
            personas.SetAvatarImage(request.EntityId, request.OwnerId, name);
        }
    }

    // Персонам вдобавок шлём штатное personas_changed: раздел «Персоны» уже слушает его
    // и перечитывает список — отдельной обработки нового события там не нужно
    private async Task NotifyAsync(ImageBackfillRequest request, CancellationToken ct)
    {
        try
        {
            var clients = hub.Clients.Group("user_" + request.OwnerId);
            await clients.SendAsync("message",
                new ImageBackfilledMessage(request.Kind, request.EntityId), ct);
            if (request.Kind == ImageBackfillKinds.PersonaAvatar)
                await clients.SendAsync("message",
                    new PersonasChangedMessage("updated", request.EntityId), ct);
        }
        catch (Exception ex)
        {
            // Картинка уже сохранена — не доехавшее событие лечится перезагрузкой страницы
            log.LogWarning(ex, "Не удалось разослать событие о догнавшей картинке {Kind} {Entity}",
                request.Kind, request.EntityId);
        }
    }

    // Сущность заявки: есть ли она, стоит ли уже картинка и чем подписать промпт по умолчанию
    private Target? Resolve(ImageBackfillRequest request)
    {
        if (request.Kind == ImageBackfillKinds.ProjectIcon)
        {
            var project = projects.GetById(request.EntityId);
            if (project is null || project.OwnerId != request.OwnerId) return null;
            return new Target(
                project.Name,
                project.Icon.Kind == Models.ProjectIconKind.Image
                    && !string.IsNullOrEmpty(project.Icon.ImageFile),
                DefaultIconPrompt(project.Name));
        }

        if (request.Kind == ImageBackfillKinds.PersonaAvatar)
        {
            var persona = personas.GetByIdInternal(request.EntityId);
            if (persona is null || persona.OwnerId != request.OwnerId) return null;
            return new Target(
                persona.Name,
                persona.Avatar.Kind == Models.PersonaAvatarKind.Image
                    && !string.IsNullOrEmpty(persona.Avatar.ImageFile),
                DefaultAvatarPrompt(persona.Name, persona.Description));
        }

        return null;
    }

    private sealed record Target(string Name, bool HasImage, string DefaultPrompt);

    // Промпты по умолчанию — на случай, если заявка пришла без своего (напр. пережила
    // рестарт со старым форматом). Держим в одной формулировке с ручной генерацией
    // (ProjectsController.BuildIconPrompt / PersonasController.BuildAvatarPrompt).
    private static string DefaultIconPrompt(string name)
    {
        var subject = string.IsNullOrWhiteSpace(name)
            ? "an abstract project emblem"
            : $"a project named '{name.Trim()}'";
        return $"Flat minimalist 2D vector emblem representing {subject}. " +
            "A single bold symbol that fills the entire square canvas edge to edge (full-bleed), " +
            "simple flat shapes, solid flat background color, high contrast, centered composition. " +
            "No rounded-rectangle app-icon frame, no border, no drop shadow, no 3D, no gloss, " +
            "no small padding around the symbol, no text, no letters.";
    }

    private static string DefaultAvatarPrompt(string name, string? description)
    {
        var who = string.IsNullOrWhiteSpace(description) ? name : $"{name}, {description}";
        return $"Photorealistic portrait photo of {who}. Head and shoulders, looking at camera, " +
               "clean solid background, soft studio lighting, natural skin, friendly expression, " +
               "high detail, sharp focus, square crop.";
    }
}
