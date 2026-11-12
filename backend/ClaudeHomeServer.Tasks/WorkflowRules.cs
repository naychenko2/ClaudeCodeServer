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

    // Лимит текста промпта этапа (разрез §1).
    public const int PromptLimit = 4000;

    // Проверить шаблон целиком: пустой список — валиден. Собираем ВСЕ ошибки, не первую:
    // редактор шаблонов (C1) показывает картину за один проход, правка по одной ошибке
    // на сохранение здесь не в помощь.
    public static List<string> Validate(WorkflowTemplate template)
    {
        var errors = new List<string>();
        var seenRoles = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < template.Stages.Count; i++)
        {
            var stage = template.Stages[i];
            var role = stage.Role?.Trim() ?? "";
            // Метка в тексте ошибки: роль, если она известна; иначе порядковый номер —
            // «неизвестная роль» именем этапа не назвать.
            var known = StageRoles.Contains(role);
            var label = known ? $"этап «{role}»" : $"этап #{i + 1}";

            if (!known)
            {
                errors.Add($"{label}: неизвестная роль этапа «{role}». Допустимы: {string.Join(", ", StageRoles)}.");
            }
            else if (!seenRoles.Add(role))
            {
                errors.Add($"{label}: роль встречается в маршруте дважды.");
            }

            if (!WorkflowStagePresence.All.Contains(stage.Presence))
            {
                errors.Add($"{label}: неизвестное Presence «{stage.Presence}». Допустимы: {string.Join(", ", WorkflowStagePresence.All)}.");
            }
            else if (NonSkippable.Contains(role) &&
                     stage.Presence is WorkflowStagePresence.Off or WorkflowStagePresence.Optional)
            {
                // Несъёмную роль нельзя ни снять, ни сделать опциональной: пропуск должен
                // быть виден валидатору, а не исчезать тихо (риск эпика «скрытый пропуск финала»).
                errors.Add($"{label}: роль несъёмная, Presence «{stage.Presence}» запрещён.");
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

            // Ячейки матрицы — той же проверкой, что ячейки специальности (разрез §3:
            // «ячейку этапа валидировать как ячейку специальности»), не копией:
            foreach (var cell in new[] { stage.TierStrong, stage.TierMedium, stage.TierWeak })
                if (SpecialtySettingsStore.ValidateModelCell(cell, label) is { } cellError)
                    errors.Add(cellError);

            if (!string.IsNullOrWhiteSpace(stage.StallPolicy) && !WorkflowStallPolicies.All.Contains(stage.StallPolicy))
            {
                errors.Add($"{label}: неизвестная политика простоя «{stage.StallPolicy}». Допустимы: {string.Join(", ", WorkflowStallPolicies.All)}.");
            }
        }

        return errors;
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
