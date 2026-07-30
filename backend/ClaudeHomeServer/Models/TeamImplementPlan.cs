namespace ClaudeHomeServer.Models;

// План командной реализации (Э2): результат работы планировщика — структура, а не текст.
// Публикуется карточкой в ленту штаба, человек правит исполнителей и подтверждает запуск
// (единственное согласование итерации). Раздача под-задач и волны — Э3.
// См. docs/architecture/team-implement-mode.md, раздел «Этапы → Э2».
public class TeamImplementPlan
{
    // Id карточки плана: попадает в Session.TeamImplement.PlanCardId и в ответ хаба
    public string Id { get; init; } = Guid.NewGuid().ToString();
    // Исходная вводная человека, по которой построен план
    public string Request { get; init; } = "";
    // Одна строка «что делаем» от планировщика — подзаголовок карточки
    public string Summary { get; set; } = "";
    // Под-задачи в порядке публикации; волна задаётся полем Wave каждой
    public List<TeamImplementSubtask> Subtasks { get; set; } = [];
    // Персона, построившая план (планировщик). Для карточки и трассировки
    public string? PlannerPersonaId { get; set; }
    // Решение человека по карточке: null — ждёт, см. TeamPlanDecision
    public bool? Approved { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // Число волн = максимальный номер волны среди под-задач (0 — план пуст)
    public int WaveCount => Subtasks.Count == 0 ? 0 : Subtasks.Max(s => s.Wave);
    // Сколько разных исполнителей задействовано — для подзаголовка карточки
    public int ExecutorCount => Subtasks
        .Where(s => !string.IsNullOrWhiteSpace(s.ExecutorPersonaId))
        .Select(s => s.ExecutorPersonaId).Distinct().Count();
}

// Под-задача плана: единица раздачи. В Э3 из неё создаётся задача (personaId = исполнитель)
// и дочерний чат исполнения под штабом.
public class TeamImplementSubtask
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    // Название под-задачи — строка карточки
    public string Title { get; set; } = "";
    // Что сделать: уходит в описание задачи исполнителя (Э3)
    public string Goal { get; set; } = "";
    // Исполнитель — id персоны из состава кандидатов. Пользователь может сменить
    // до запуска (RespondTeamPlan с Reassign), поэтому set, а не init.
    public string? ExecutorPersonaId { get; set; }
    // Одна строка «почему именно он» от планировщика — главное требование Э2:
    // видно, что бэкендовая часть ушла бэкендеру, фронтовая — фронтендеру.
    // При ручной смене исполнителя перезаписывается пометкой пользователя.
    public string ExecutorRationale { get; set; } = "";
    // Файлы/папки во владении под-задачи — граница, по которой куски не конфликтуют
    public List<string> Files { get; set; } = [];
    // Номер волны (с 1): под-задачи одной волны идут параллельно, следующая ждёт предыдущую
    public int Wave { get; set; } = 1;
    // Критерий готовности — уходит в описание задачи исполнителя (Э3)
    public string DoneCriteria { get; set; } = "";
    // Задача трекера, созданная по под-задаче при запуске волны (Э3). null — ещё не роздана;
    // по этому полю волна и понимает, что уже запущено, а что ждёт своей очереди.
    public string? TaskId { get; set; }
    // Сколько раз исполнителя по этой под-задаче запускали (Э4). Первый провал даёт одну
    // перевыдачу, второй — эскалацию: считаем попытки здесь, а не по задаче трекера,
    // потому что перевыдача может уйти другому исполнителю той же под-задачи.
    public int Attempts { get; set; }
}

// Карточка кандидата в промпт планировщика: по ней он подбирает исполнителя под каждую
// под-задачу. Состав — явно выбранные персоны либо вся команда проекта (GetForContext).
public sealed record TeamCandidateCard(
    string PersonaId,
    string Handle,
    string Name,
    string? Role,
    string? Description,
    // Специальность (PersonaSpecialty) человекочитаемо — «Планировщик», «Исполнитель»…
    string? Specialty,
    // Привязки к папкам проекта и базам знаний — «за что отвечает» в терминах кода
    IReadOnlyList<string> Bindings);

// Решение человека по карточке плана.
// Run — запустить (единственное согласование итерации; раздача — Э3);
// Reassign — сменить исполнителя под-задачи, карточка остаётся открытой;
// Cancel — отменить план, режим возвращается в стадию планирования.
public enum TeamPlanDecision { Run, Reassign, Cancel }
