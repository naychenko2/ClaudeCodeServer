using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Tasks;

// Чистые правила шаблона процесса фичи (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §1):
// ни хранилища, ни HTTP, ни состояния — тестируются unit-тестами без стенда.
// Вызывающая сторона (стор B1b, REST B1c) берёт список ошибок и отдаёт 400.
public static class WorkflowRules
{
    // Роли этапа: ключи TaskRoles без feature/rework/consult — шаблон не вводит новых видов
    // работ (разрез §0.3); rework берёт этап step, эпик и consult вне маршрута.
    public static readonly IReadOnlyList<string> StageRoles =
        [TaskRoles.Cut, TaskRoles.Design, TaskRoles.Plan, TaskRoles.Step,
         TaskRoles.Review, TaskRoles.Qa, TaskRoles.Final, TaskRoles.Merge];

    // Несъёмные роли (разрез §1): step — сама работа, merge — закрытие фичи, final — единственная
    // проверка решения целиком, а его скрытый пропуск — уже названный риск эпика.
    // final несъёмный ВРЕМЕННО — открытый вопрос 1 Андрею; его ответ правит эту строку,
    // а не логику валидатора.
    public static readonly IReadOnlyList<string> NonSkippable = [TaskRoles.Step, TaskRoles.Merge, TaskRoles.Final];

    // Роли, которых в маршруте не больше одного (разрез 2 §1.6): cut, final и merge — по одному
    // на фичу. Повторяться могут design, plan, step, review и qa — у каждого свой id.
    public static readonly IReadOnlyList<string> SingleRoles = [TaskRoles.Cut, TaskRoles.Final, TaskRoles.Merge];

    // Лимит текста промпта этапа (разрез §1).
    public const int PromptLimit = 4000;

    // Лимит имени этапа (G7, решение контракта a5d0c76e): 1..40 после обрезки.
    public const int TitleLimit = 40;

    // Id этапа — slug (разрез 2 §1.1).
    private static readonly Regex StageIdPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    // Проверить шаблон целиком: пустой список — валиден. Собираем ВСЕ ошибки, не первую:
    // редактор шаблонов (C1) показывает картину за один проход, правка по одной ошибке
    // на сохранение здесь не в помощь. Тот же контракт — против null-входа: null-шаблон,
    // null-список этапов и null-элемент — ошибка в списке, а не исключение (R-B1a, п.2:
    // стор отдаёт список как 400, исключение уронило бы ручку 500-м).
    public static List<string> Validate(WorkflowTemplate? template)
    {
        var errors = new List<string>();
        if (template is null)
        {
            errors.Add("шаблон не задан.");
            return errors;
        }
        if (template.Stages is not { } stages)
        {
            errors.Add("шаблон: список этапов не задан.");
            return errors;
        }

        if (stages.Count > WorkflowGraph.MaxStages)
            errors.Add($"в шаблоне {stages.Count} этапов, предел — {WorkflowGraph.MaxStages}.");

        var nodes = WorkflowGraph.Normalize(stages);
        // Id этапов, уже встреченных выше по списку: After смотрит только в них (ацикличность
        // по построению). У пустой записи id пуст — он в наборе тоже, чтобы ссылка следующего
        // этапа по умолчанию не давала вторую ошибку к уже названной «пустая запись».
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var singleRoleSeen = new HashSet<string>(StringComparer.Ordinal);
        var laneSeen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < stages.Count; i++)
        {
            // Метка в тексте ошибки: id этапа (у линейных он равен роли), если роль известна;
            // иначе порядковый номер — «неизвестная роль» именем этапа не назвать.
            var labelUnknown = $"этап #{i + 1}";
            if (stages[i] is not { } stage)
            {
                errors.Add($"{labelUnknown}: пустая запись");
                seenIds.Add(nodes[i].Id);
                continue;
            }

            var node = nodes[i];
            var role = node.Role;
            // Presence и StallPolicy триммятся так же, как роль (R-B1a, п.3): строка из UI
            // с крающими пробелами — та же запись, а не «неизвестное значение».
            var presence = node.Presence;
            var stallPolicy = stage.StallPolicy?.Trim() ?? "";
            var known = StageRoles.Contains(role);
            var label = known ? $"этап «{node.Id}»" : labelUnknown;

            if (!known)
            {
                errors.Add($"{label}: неизвестная роль этапа «{role}». Допустимы: {string.Join(", ", StageRoles)}.");
            }
            else if (SingleRoles.Contains(role) && !singleRoleSeen.Add(role))
            {
                errors.Add($"{label}: роль «{role}» одиночная — в маршруте она встречается дважды.");
            }

            // Id: slug и уникален. При неизвестной роли без явного id молчим — id совпал бы
            // с ролью, а ошибка про роль уже названа.
            if ((known || !string.IsNullOrWhiteSpace(stage.Id)) && !StageIdPattern.IsMatch(node.Id))
                errors.Add($"{label}: id этапа «{node.Id}» не slug [a-z0-9-]{{1,32}}.");
            var duplicateId = !seenIds.Add(node.Id);
            if (duplicateId)
                errors.Add($"{label}: id этапа «{node.Id}» встречается в маршруте дважды.");

            // After — только на этапы выше по списку: себя и вперёд нельзя. У дубля id «после»
            // не разбираем: ссылка по умолчанию указала бы на тот же id — шум поверх названной причины.
            foreach (var parent in duplicateId ? [] : node.After)
            {
                if (parent == node.Id)
                    errors.Add($"{label}: этап не может идти после самого себя.");
                else if (!seenIds.Contains(parent))
                    errors.Add($"{label}: «после» ссылается на «{parent}» — такого этапа выше по списку нет.");
            }

            // Линия — только у step, из белого списка, одна на этап.
            if (node.Lane is { } lane)
            {
                if (role != TaskRoles.Step)
                    errors.Add($"{label}: линия задаётся только у этапа step.");
                else if (!WorkflowGraph.Lanes.Contains(lane))
                    errors.Add($"{label}: неизвестная линия «{lane}». Допустимы: {string.Join(", ", WorkflowGraph.Lanes)}.");
                else if (!laneSeen.Add(lane))
                    errors.Add($"{label}: два этапа step с линией «{lane}».");
            }

            if (!WorkflowStagePresence.All.Contains(presence))
            {
                errors.Add($"{label}: неизвестное Presence «{presence}». Допустимы: {string.Join(", ", WorkflowStagePresence.All)}.");
            }
            else if (NonSkippable.Contains(role) &&
                     presence is WorkflowStagePresence.Off or WorkflowStagePresence.Optional)
            {
                // Несъёмную роль нельзя ни снять, ни сделать опциональной: пропуск должен
                // быть виден валидатору, а не исчезать тихо (риск эпика «скрытый пропуск финала»).
                errors.Add($"{label}: роль несъёмная, Presence «{presence}» запрещён.");
            }

            // Кого звать — одно: специальность ИЛИ конкретная персона (разрез §1).
            // Specialty=None — «не задана», не выбор, с PersonaId не конфликтует.
            if (stage.Specialty is { } specialty && specialty != PersonaSpecialty.None &&
                !string.IsNullOrWhiteSpace(stage.PersonaId))
            {
                errors.Add($"{label}: Specialty и PersonaId взаимоисключающи: укажите что-то одно.");
            }

            if (stage.Prompt?.Length > PromptLimit)
            {
                errors.Add($"{label}: текст промпта этапа длиннее {PromptLimit} символов");
            }

            // Имя этапа (G7): длина проверяется по каждому; требования группы (обязательность
            // и различность) по одному этапу ответить нельзя — ValidateTitleGroups.
            if (stage.Title?.Trim() is { Length: > TitleLimit })
            {
                errors.Add($"{label}: имя этапа длиннее {TitleLimit} символов");
            }

            // Ячейки матрицы — той же проверкой, что ячейки специальности (разрез §3:
            // «ячейку этапа валидировать как ячейку специальности»), не копией: общий
            // метод живёт в спине ModelCellRules (Core) — Tasks не зависит от Llm (R-B1a, п.1).
            foreach (var cell in new[] { stage.TierStrong, stage.TierMedium, stage.TierWeak })
                if (ModelCellRules.ValidateModelCell(cell, label) is { } cellError)
                    errors.Add(cellError);

            if (!string.IsNullOrWhiteSpace(stallPolicy) && !WorkflowStallPolicies.All.Contains(stallPolicy))
            {
                errors.Add($"{label}: неизвестная политика простоя «{stallPolicy}». Допустимы: {string.Join(", ", WorkflowStallPolicies.All)}.");
            }
        }

        // Графовые и именные правила идут только по структурно целым данным: на дубле id,
        // неизвестной роли или пустой записи «висящие ветки», Resolve и группы имён дали бы шум
        // поверх уже названной причины. Порядок: сначала граф (он не читает имена), затем имена
        // — обе группы ошибок доезжают до редактора за один проход.
        if (errors.Count == 0)
        {
            ValidateGraph(nodes, errors);
            ValidateTitleGroups(nodes, errors);
        }

        return errors;
    }

    // Имена этапов одной роли и линии (G7, решение контракта a5d0c76e по возврату R-C3a):
    // этапы с общей парой (Role, Lane) граф показывает одинаково подписанными узлами
    // (qa-plan и qa — две неотличимые «Проверки»). У таких этапов Title обязателен у каждого
    // и различен в группе без учёта регистра; вне группы Title необязателен. «У каждого» —
    // бэк не знает фронтовой таблицы имён ролей и не может проверить, что Title не совпадёт
    // с подписью по умолчанию (a5d0c76e). Группа строится по нормализованным Role/Lane:
    // линия есть только у step, поэтому группы других ролей — по одной роли.
    private static void ValidateTitleGroups(List<WorkflowGraph.Node> nodes, List<string> errors)
    {
        var groups = new Dictionary<string, List<WorkflowGraph.Node>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            // Роль вне белого списка уже названа ошибкой в основном проходе — группу по ней
            // не собираем.
            if (!StageRoles.Contains(node.Role)) continue;
            var key = node.Role + " " + (node.Lane ?? "");
            if (!groups.TryGetValue(key, out var members)) groups[key] = [node];
            else members.Add(node);
        }

        foreach (var members in groups.Values)
        {
            if (members.Count < 2) continue;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                var label = $"этап «{member.Id}»";
                if (member.Title is null)
                {
                    errors.Add($"{label}: этап делит роль и линию с другим этапом — имя (Title) обязательно.");
                }
                else if (!taken.Add(member.Title))
                {
                    errors.Add($"{label}: имя «{member.Title}» повторяет имя другого этапа той же роли и линии.");
                }
            }
        }
    }

    // Нет висящих веток: каждый этап, кроме последнего, стоит в «после» кого-то из более поздних,
    // то есть все ветки сходятся в один сток. Затем та же проверка для маршрута после Resolve при
    // любом наборе снятых optional (до 2^16 вариантов — потолок этапов): иначе снятие необязательного
    // этапа оставляло бы ветку без стока уже в подтверждённом маршруте.
    private static void ValidateGraph(List<WorkflowGraph.Node> nodes, List<string> errors)
    {
        // Сам шаблон целиком, ничего не снимая (в том числе off-этапы).
        var full = WorkflowGraph.Resolve(
            nodes.Select(n => n with { Presence = WorkflowStagePresence.On }).ToList(), []);
        foreach (var id in WorkflowGraph.DanglingIds(full))
            errors.Add($"этап «{id}»: на него никто не ссылается — ветка не сходится в общий сток.");
        if (errors.Count > 0 || nodes.Count > WorkflowGraph.MaxStages) return;

        var optional = nodes.Where(n => n.Presence == WorkflowStagePresence.Optional).Select(n => n.Id).ToList();
        for (var mask = 0; mask < 1 << optional.Count; mask++)
        {
            var skipped = new HashSet<string>(
                optional.Where((_, bit) => (mask >> bit & 1) == 1), StringComparer.Ordinal);
            var dangling = WorkflowGraph.DanglingIds(WorkflowGraph.Resolve(nodes, skipped));
            if (dangling.Count == 0) continue;
            errors.Add(skipped.Count == 0
                ? $"после снятия этапов off ветка не сходится в сток: висят {string.Join(", ", dangling)}."
                : $"при снятии необязательных этапов {string.Join(", ", skipped)} ветка не сходится в сток: висят {string.Join(", ", dangling)}.");
            return;
        }
    }

    // Порог молчания этапа (И3 «таймаут нельзя обойти»): null/0/отрицательное — не «бесконечно»,
    // а дефолт конфига (Tasks:ExecutorStaleMinutes, его берёт вызывающая сторона); 1..4 — ниже
    // пола, поднимаем до 5; больше 240 — клампим. Валидатор на диапазон не ругается:
    // кламп — часть контракта, а не ошибка.
    public static int? NormalizeStall(int? minutes) =>
        minutes is null or <= 0 ? null
        : minutes < 5 ? 5
        : minutes > 240 ? 240
        : minutes;
}
