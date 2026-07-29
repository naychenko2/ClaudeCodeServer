using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Prompts;

// Тексты режима «Командная реализация» (флаг team-implement-mode): обвязка хода штаба и
// постановка под-задачи исполнителю. Держим их в одном месте — правило «любая работа
// через задачу» звучит и в промпте координатора, и в описании задачи.
// См. docs/features/team-implement-mode.md, разделы «Э3» и «Правило „любая работа — через задачу"».
public static class TeamImplementPrompts
{
    // Гард правила «координатор не пишет код сам»: инструменты правки файлов у чата-штаба
    // отключены поверх прав персоны (--disallowedTools, мимо этого CLI не проведёшь ни в
    // одном режиме прав). Bash/PowerShell в списке НЕТ — проверка сборки и тестов
    // координатору нужна, а закрывать их целиком нельзя. Запись содержимого файла через
    // shell (heredoc, echo/tee/sed -i, PowerShell *-Content) ловит отдельный гейт —
    // CoordinatorWriteGuard в ClaudeSession.DecidePermissionAsync (плюс принудительный
    // режим прав auto/default в SetTeamImplementAsync, иначе гейт не вызывается вовсе).
    // Имена сверены с PersonaAccessPolicy.ReadOnlyDisallowed (неизвестное имя в
    // deny-правиле роняет запуск claude).
    public static readonly string[] CoordinatorDisallowed = ["Edit", "Write", "NotebookEdit"];

    // Обвязка хода штаба: правило «любая работа — через задачу» + текущая стадия + бюджет
    // и протокол эскалации (Э4). Дописывается к тексту для CLI, в историю и UI не попадает
    // (как протокол work-loop).
    public static string CoordinatorTurn(SessionTeamImplement team)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Режим «Командная реализация»] Этот чат — штаб фичи.");
        sb.AppendLine("Ни одна работа не выполняется без задачи: всё, что делает команда, " +
                      "оформляется задачей с исполнителем и критерием готовности.");
        if (team.CoordinatorNoCode)
            sb.AppendLine("Сам файлы не правишь — инструменты правки отключены намеренно. " +
                          "Нужна правка — это под-задача исполнителю.");
        sb.AppendLine(team.Stage switch
        {
            TeamImplementStage.Planning => "Стадия: планирование — разбери вводную и подготовь план.",
            TeamImplementStage.Confirming => "Стадия: план опубликован и ждёт подтверждения человека.",
            TeamImplementStage.Wave => $"Стадия: идёт волна {team.WaveNumber}" +
                                       (team.PlannedWaves > 0 ? $" из {team.PlannedWaves}" : "") +
                                       " — задачи розданы исполнителям, дождись их докладов.",
            TeamImplementStage.AwaitingDecision => "Стадия: практика ждёт решения человека.",
            TeamImplementStage.Checking => "Стадия: проверка результата волн (сборка/тесты).",
            _ => "Стадия: итерация завершена, ждём следующую вводную.",
        });
        // Э5: разбор вводной нужен там, где итерация ещё не идёт — на входе в неё
        // (планирование) и в ожидании следующей. В работающей итерации сообщение человека
        // разбирать нечего: волна уже роздана, а вторую по ходу дела мы не разворачиваем.
        if (team.Stage is TeamImplementStage.Planning or TeamImplementStage.Idle)
            sb.AppendLine(WorkClassificationProtocol);
        sb.AppendLine(BudgetLine(team.Budget));
        sb.Append(EscalationProtocol);
        return sb.ToString();
    }

    // Классификация сообщения человека (Э5) по одному признаку — нужно ли менять файлы.
    // Работа разворачивается в волну маркером, разговор отвечается словами и ничего не стоит.
    // Маркер, а не MCP-инструмент: состав tools/list не должен зависеть от стадии хода
    // (иначе перезапуск CLI со всеми серверами) — тот же приём, что у эскалаций.
    public const string WorkClassificationProtocol = """
        Сообщение человека раздели по одному признаку — НУЖНО ЛИ ДЛЯ НЕГО МЕНЯТЬ ФАЙЛЫ:
        — нужно («добавь экспорт», «почини сортировку», «покрой тестами») — это работа: ответь
          человеку и поставь в конце маркер <team:work>постановка для планировщика в одну-две фразы</team>;
          по нему команда разложит работу на под-задачи и развернёт волну;
        — не нужно («почему выбрали Киру?», «что сейчас в работе?», «покажи план») — просто ответь,
          маркера не ставь: разговор не создаёт задач и не тратит бюджет;
        — сомневаешься — считай разговором и задай уточняющий вопрос: переспросить дешевле,
          чем развернуть волну на пустом месте.
        """;

    // Строка расхода бюджета итерации: координатор должен видеть, сколько ему осталось,
    // иначе он планирует волны, на которые уже нет квоты.
    public static string BudgetLine(TeamImplementBudget b) =>
        $"Бюджет итерации: задачи {b.TasksUsed}/{b.MaxTasks}, волны {b.WavesUsed}/{b.MaxWaves}, " +
        $"запуски {b.RunsUsed}/{b.MaxRuns}, перевыдачи {b.RetriesUsed}/{b.MaxRetries}, " +
        $"срочные вызовы {b.WakeupsUsed}/{b.MaxWakeups}. " +
        "Кончится — практика встанет и позовёт человека.";

    // Протокол эскалации: сначала пробуй справиться сам (перевыдать, уточнить, пересобрать
    // волну) — зови человека, только когда своими силами не выходит. Инструмента для вызова
    // нет намеренно: состав MCP-инструментов не должен зависеть от режима хода, поэтому
    // эскалация — маркер в тексте, как маркер завершения цикла «до готово».
    public const string EscalationProtocol = """
        Если своими силами не справляешься — позови человека маркером в конце ответа:
        <escalate:deviation>суть расхождения с планом</escalate> — нужны файлы вне владения или объём вырос;
        <escalate:check>что именно красное</escalate> — сборка/тесты не проходят после попыток починки;
        <escalate:decision>вопрос и варианты</escalate> — нужно продуктовое решение.
        Маркер публикует карточку с кнопками и будит человека уведомлением, поэтому без нужды его не ставь.
        """;

    // Заголовок карточки остановки — тексты из таблицы «Эскалация и остановки».
    public static string EscalationTitle(TeamEscalationKind kind, string details) => kind switch
    {
        TeamEscalationKind.Blocker => $"Исполнитель застрял: {FirstLine(details)}",
        TeamEscalationKind.TaskFailed => $"Задача «{FirstLine(details)}» не удалась дважды",
        TeamEscalationKind.PlanDeviation => $"Работа выходит за план: {FirstLine(details)}",
        TeamEscalationKind.CheckFailed => $"Сборка/тесты не проходят: {FirstLine(details)}",
        TeamEscalationKind.ProductDecision => "Нужно ваше решение",
        TeamEscalationKind.BudgetExhausted => "Бюджет итерации израсходован",
        TeamEscalationKind.WaveStalled => $"Волна не двигается: {FirstLine(details)}",
        TeamEscalationKind.WaveGate => FirstLine(details),
        TeamEscalationKind.Stopped => "Практика остановлена",
        TeamEscalationKind.WaveAdded => $"Новая вводная в работе: {FirstLine(details)}",
        _ => FirstLine(details),
    };

    // Информационная карточка добавочной волны (Э5): состав виден, но клика не ждёт —
    // при авто-волнах работа уже началась, точкой контроля была сама вводная человека.
    public static string WaveAddedDetails(TeamImplementPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Под-задач: {plan.Subtasks.Count} · волн: {plan.WaveCount} · " +
                      $"исполнителей: {plan.ExecutorCount}.");
        foreach (var s in plan.Subtasks.Take(MaxCardLines))
            sb.AppendLine($"— {s.Title} (волна {s.Wave})");
        if (plan.Subtasks.Count > MaxCardLines)
            sb.AppendLine($"— и ещё {plan.Subtasks.Count - MaxCardLines} — состав целиком в карточке плана.");
        sb.Append("Работа уже идёт: авто-волны включены, и подтверждения она не ждёт. " +
                  "Нажмите «Остановить», если это лишнее.");
        return sb.ToString();
    }

    // Строк состава в карточке: длиннее — читается как простыня, а полный план и так рядом
    private const int MaxCardLines = 6;

    // Молчаливый тупик (Э7-фикс): координатор закончил ход в стадии planning без маркера
    // работы — вводная не превратилась ни в план, ни в отказ. Карточка без кнопок, как
    // «план не построился»: ответ — обычным сообщением в чат.
    public static string SilentPlanningStallDetails(string turnText) =>
        $"Координатор ответил, не выделив маркер работы по вашей вводной: «{FirstLine(turnText)}» " +
        "Уточните задачу сообщением в чат — команда попробует ещё раз.";

    // Планировщик не справился с вводной (Э5): молчать нельзя — иначе человек ждёт волну,
    // которой не будет. Карточка без кнопок: ответ — обычным сообщением в чат.
    public static string PlanFailedDetails(string request, string? reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Вводная «{FirstLine(request)}» не разложилась в план: " +
                      (string.IsNullOrWhiteSpace(reason) ? "планировщик не ответил." : reason.Trim() + "."));
        sb.Append("Уточните задачу сообщением в чат — команда попробует ещё раз.");
        return sb.ToString();
    }

    // Ход координатору с решением человека по карточке: кнопка — ярлык обычного сообщения,
    // особого протокола за ней нет, поэтому и передаём её как текст решения.
    public static string EscalationResolvedTurn(TeamEscalation? escalation, string? actionLabel, string? comment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Режим «Командная реализация»] Человек ответил на карточку остановки" +
                      (escalation is not null ? $" «{escalation.Title}»" : "") + ".");
        if (!string.IsNullOrWhiteSpace(actionLabel)) sb.AppendLine($"Решение: {actionLabel}.");
        if (!string.IsNullOrWhiteSpace(comment)) sb.AppendLine($"Комментарий: {comment.Trim()}");
        sb.Append("Продолжай с этого места по решению человека.");
        return sb.ToString();
    }

    // Ход координатору при закрытии волны: сводка и что делать дальше.
    public static string WaveClosedTurn(int wave, int doneCount, int totalCount, bool nextStarted, int? nextWave)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Режим «Командная реализация»] Волна {wave} закрыта: {doneCount} из {totalCount}.");
        sb.Append(nextStarted
            ? $"Волна {nextWave} уже роздана исполнителям — опубликуй короткую сводку закрытой волны и жди докладов."
            : nextWave is null
                ? "Волн больше нет: проверь результат (сборка/тесты) и подведи итог итерации."
                : $"Волна {nextWave} ждёт решения человека — опубликуй сводку закрытой волны.");
        return sb.ToString();
    }

    // Маркер доклада-блокера в ленте штаба: по нему человек отличает «застрял» от обычного
    // промежуточного отчёта, не читая всю карточку.
    public const string BlockerReportMarker = "⛔ Блокер: ";

    public static string BlockerReportText(string text) => BlockerReportMarker + text.Trim();

    // Ход координатору по докладу-блокеру снизу: доклад уже лёг в ленту гостевой репликой.
    public static string BlockerReactionTurn(string? childChatName) =>
        $"[Режим «Командная реализация»] Исполнитель{(string.IsNullOrWhiteSpace(childChatName) ? "" : $" («{childChatName}»)")} " +
        "доложил о блокере — текст выше в ленте. Разберись: если можешь снять блокер сам " +
        "(уточнить постановку, перевыдать работу, поправить план) — сделай это в пределах бюджета, " +
        "иначе позови человека маркером эскалации.";

    private static string FirstLine(string text)
    {
        var line = (text ?? "").Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    // Описание задачи исполнителю по под-задаче плана: что сделать, файлы во владении,
    // критерий готовности и рабочее дерево (когда штаб работает в worktree — иначе
    // исполнитель уедет править основной репозиторий).
    public static string SubtaskDescription(TeamImplementPlan plan, TeamImplementSubtask subtask,
        string? worktreePath, string? worktreeBranch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Что сделать");
        sb.AppendLine(string.IsNullOrWhiteSpace(subtask.Goal) ? subtask.Title : subtask.Goal.Trim());

        if (!string.IsNullOrWhiteSpace(worktreePath))
        {
            sb.AppendLine();
            sb.AppendLine("## Рабочее дерево");
            var branch = string.IsNullOrWhiteSpace(worktreeBranch) ? "" : $" (ветка `{worktreeBranch}`)";
            sb.AppendLine($"**ВАЖНО: работа идёт в worktree `{worktreePath}`{branch}, НЕ в основном репозитории.** " +
                          "Читай и правь файлы только там.");
        }

        sb.AppendLine();
        sb.AppendLine("## Файлы во владении");
        if (subtask.Files.Count > 0)
        {
            foreach (var f in subtask.Files) sb.AppendLine($"- `{f}`");
            sb.AppendLine();
            sb.AppendLine("Файлы вне этого списка не трогай: их правят другие исполнители волны.");
        }
        else
            sb.AppendLine("Файлы не заданы планом — определи их сам и не выходи за границу своей части.");

        sb.AppendLine();
        sb.AppendLine("## Критерий готовности");
        sb.AppendLine(string.IsNullOrWhiteSpace(subtask.DoneCriteria)
            ? "Работа сделана и проверена фактическим прогоном (сборка/тесты), результат приложен к задаче."
            : subtask.DoneCriteria.Trim());

        sb.AppendLine();
        sb.AppendLine("## Контекст");
        if (!string.IsNullOrWhiteSpace(plan.Summary)) sb.AppendLine($"План команды: {plan.Summary.Trim()}");
        var waves = plan.WaveCount > 0 ? $" из {plan.WaveCount}" : "";
        sb.AppendLine($"Твоя часть идёт в волне {subtask.Wave}{waves}. " +
                      "Параллельно работают другие исполнители — не выходи за свои файлы.");
        return sb.ToString();
    }
}
