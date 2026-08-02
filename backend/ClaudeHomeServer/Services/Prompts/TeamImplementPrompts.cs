using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Prompts;

// Тексты режима «Командная реализация» (флаг team-implement-mode): обвязка хода штаба и
// постановка под-задачи исполнителю. Держим их в одном месте — правило «любая работа
// через задачу» звучит и в промпте координатора, и в описании задачи.
// См. docs/architecture/team-implement-mode.md, разделы «Э3» и «Правило „любая работа — через задачу"».
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

    // Запрет ExitPlanMode координатору (Э8). На стадиях интервью и планирования штаб сидит в
    // настоящем план-режиме, а в нём CLI сам предлагает завершить планирование карточкой
    // `plan_review` — и человек получал бы ДВА согласования подряд: штатное и наше командное.
    // Планирование заканчивается только карточкой плана, поэтому инструмент отключаем целиком.
    // Запрет висит на СЕССИИ (режим включён), а не на ходе: состав tools/list не должен
    // зависеть от стадии — иначе каждый переход перезапускал бы CLI со всеми MCP-серверами.
    // Отдельно от CoordinatorDisallowed: тот снимается настройкой «координатор не пишет код»,
    // а этот держится, пока режим активен.
    public static readonly string[] ModeDisallowed = ["ExitPlanMode"];

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
            TeamImplementStage.Interview => "Стадия: интервью — сначала спроси человека, потом планируй.",
            TeamImplementStage.Planning => "Стадия: планирование — разбери вводную и подготовь план.",
            TeamImplementStage.Confirming => "Стадия: план опубликован и ждёт подтверждения человека.",
            TeamImplementStage.Wave => $"Стадия: идёт волна {team.WaveNumber}" +
                                       (team.PlannedWaves > 0 ? $" из {team.PlannedWaves}" : "") +
                                       " — задачи розданы исполнителям, дождись их докладов.",
            TeamImplementStage.AwaitingDecision => "Стадия: практика ждёт решения человека.",
            TeamImplementStage.Checking => "Стадия: проверка результата волн (сборка/тесты).",
            _ => "Стадия: итерация завершена, ждём следующую вводную.",
        });
        // Э8: на стадиях интервью и планирования чат сидит в план-режиме, поэтому CLI сам
        // подталкивает завершить планирование через ExitPlanMode. Инструмент отключён —
        // говорим об этом прямо, иначе координатор будет биться в запрещённый вызов вместо
        // того, чтобы отдать постановку маркером.
        if (team.Stage.IsPlanPhase())
            sb.AppendLine(PlanPhaseNote);
        // Э8: в интервью классифицировать нечего — вводная уже признана работой, идёт разговор
        // по ней. Вместо этого протокол вопросов: ASK-карточками, максимум 2 раунда.
        if (team.Stage == TeamImplementStage.Interview)
        {
            sb.AppendLine(InterviewProtocol(team));
            // M6: легальный выход без работы. Без него честный разговорный ответ (файлы
            // менять не нужно) детерминированно ловил stall-гард «Координатор не понял
            // вводную» — у модели не было способа сказать «работы нет» (аудит 2026-08-01).
            sb.AppendLine(InterviewTalkOut);
        }
        // Э5: разбор вводной нужен там, где итерация ещё не идёт — на входе в неё
        // (планирование) и в ожидании следующей. В работающей итерации сообщение человека
        // разбирать нечего: волна уже роздана, а вторую по ходу дела мы не разворачиваем.
        else if (team.Stage is TeamImplementStage.Planning or TeamImplementStage.Idle)
            sb.AppendLine(WorkClassificationProtocol);
        sb.AppendLine(BudgetLine(team.Budget));
        sb.Append(EscalationProtocol);
        return sb.ToString();
    }

    // Протокол интервью (Э8) — тот же, что у механики «Интервью» (deep-interview), но
    // укороченный до двух раундов: вход в итерацию должен оставаться разговором, а не допросом.
    // Канал вопросов — штатный ASK (`AskUserQuestion`): формулировка правила обкатана в
    // автоматизациях персон (PersonaAutomationService), там она надёжно уводит модель от
    // вопросов-простыней в тексте. Своих карточек вопросов не заводим.
    // Раундов осталось считает бэкенд (InterviewRounds на состоянии): модель не помнит,
    // сколько уже спрашивала, а исчерпанный лимит обязан переводить её к допущениям.
    public static string InterviewProtocol(SessionTeamImplement team)
    {
        var left = MaxInterviewRounds - team.InterviewRounds;
        var sb = new StringBuilder();
        sb.AppendLine("Прежде чем планировать, закрой развилки постановки:");
        sb.AppendLine("— вопросы задавай ТОЛЬКО инструментом AskUserQuestion с вариантами ответов " +
                      "(не пиши вопросы текстом) — человек получит карточку с кнопками; " +
                      "до 4 вопросов за раунд, у каждого 2–4 варианта, рекомендуемый помечай, " +
                      "свободный ответ человек даст через «Другое»;");
        sb.AppendLine("— спрашивай только то, что ДЕЙСТВИТЕЛЬНО развилка: два рабочих пути, и выбор " +
                      "меняет работу. Всё остальное не спрашивай, а оформи допущением — его " +
                      "человек утвердит вместе с планом;");
        sb.AppendLine(left > 0
            ? $"— раундов вопросов осталось: {left} из {MaxInterviewRounds}. Вопросов нет — так и скажи " +
              "и сразу давай маркер работы: фейковых вопросов ради обязательности стадии не бывает;"
            : $"— лимит раундов ({MaxInterviewRounds}) исчерпан: больше не спрашивай, остаток неясностей " +
              "оформи допущениями;");
        sb.Append("— когда постановка ясна, заверши интервью маркером " +
                  "<team:work>постановка для планировщика с учётом ответов и допущений</team> — " +
                  "по нему команда построит план и покажет его тебе и человеку.");
        return sb.ToString();
    }

    // Оговорка про план-режим стадий интервью и планирования (Э8): режим прав здесь наш,
    // а не выбор человека, и выходить из него штатным путём нельзя.
    public const string PlanPhaseNote =
        "Чат сейчас в режиме «план» — так задумано: пока план не согласован, файлы не правятся. " +
        "Инструмент ExitPlanMode отключён, им не пользуйся: планирование завершается маркером " +
        "работы, а согласование — карточкой плана команды.";

    // Потолок раундов вопросов на одну вводную: интервью — вход в работу, а не допрос
    // (риск «интервью превращается в допрос» из продуктового плана).
    public const int MaxInterviewRounds = 2;

    // Единая проверка исчерпания лимита раундов (Minor, волна 3): используется и текстом
    // (что сказать модели — InterviewProtocol/ClarifyInterviewTurn), и permission-гейтом
    // (что реально позволить — ClaudeSession.HandleControlRequestAsync денаит AskUserQuestion
    // сверх лимита). Раньше был только текст-просьба «больше не спрашивай» без проверки факта —
    // 3-й раунд так же уходил в карточку.
    public static bool InterviewRoundsExhausted(SessionTeamImplement team) =>
        team.InterviewRounds >= MaxInterviewRounds;

    // Выход из интервью без работы (M6): сообщение человека оказалось разговором — практику
    // на пустом месте не разворачиваем. Маркер, а не «просто ответь»: по голому тексту бэкенд
    // не отличит честный ответ от молчаливого тупика (stall-гард по концу хода).
    public const string InterviewTalkOut =
        "Если выяснилось, что работы нет (сообщение человека — разговор или вопрос, файлы менять " +
        "не нужно), постановку не выдумывай: ответь по существу и заверши ход маркером <team:talk/> — " +
        "интервью закроется без плана и задач, а практика вернётся в прежнее состояние без трат бюджета.";

    // Реплика перед первой ASK-карточкой — текст из спеки Э8. Уходит координатору как
    // подсказка тона: карточка вопросов не должна появляться без единого слова человеку.
    public const string InterviewOpeningLine =
        "Пара вопросов перед планом. Выберите варианты — или «Другое», чтобы ответить своими словами.";

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
        <escalate:decision>вопрос и варианты</escalate> — нужно продуктовое решение;
        <escalate:clarify>что именно неясно</escalate> — требования неясны и дальше действовать нельзя:
        волны встанут на паузу, а ты вернёшься к вопросам и покажешь обновлённый план на подтверждение.
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
        TeamEscalationKind.NeedsClarification => "Нужны уточнения — волны на паузе",
        _ => FirstLine(details),
    };

    // Заголовок уведомления и push (Э8). В push видны только заголовок и тело, поэтому имя
    // персоны вклеиваем в текст: штаб говорит с человеком от лица координатора. Персоны нет
    // (или её удалили) — остаётся обезличенный текст из раздела «Тексты» продуктового плана.
    // waitingForAnswers — ждём ответов на вопросы интервью, а не решения по карточке.
    public static string WaitingTitle(string? personaName, bool waitingForAnswers) =>
        string.IsNullOrWhiteSpace(personaName)
            ? waitingForAnswers ? "Команда ждёт ответов по задаче" : "Команда ждёт вашего решения"
            : waitingForAnswers ? $"{personaName} ждёт ответов по задаче" : $"{personaName}: нужно ваше решение";

    // Тело уведомления о вопросах интервью: сама карточка вопросов уже в ленте чата
    public const string QuestionNotificationBody = "Вопросы по задаче — откройте чат и выберите варианты";

    // Карточка тупика в волне (Э8): кнопок нет — вопросы придут ASK-карточками следом,
    // и человек отвечает ими (или обычным сообщением). Главное здесь — что работа стоит
    // и почему: молчаливых пауз в режиме не бывает.
    public static string NeedsClarificationDetails(string reason, int wave)
    {
        var sb = new StringBuilder();
        sb.AppendLine(wave > 0
            ? $"Волна {wave} на паузе: команда не знает, как действовать дальше."
            : "Команда не знает, как действовать дальше.");
        sb.AppendLine($"Что неясно: {reason.Trim()}");
        sb.Append("Сейчас придут вопросы с вариантами ответов. После них команда покажет " +
                  "обновлённый план — работа продолжится только после вашего «Запустить».");
        return sb.ToString();
    }

    // Ход координатору сразу после карточки тупика: карточка уже в ленте, теперь надо
    // задать сами вопросы. Без этого хода практика встала бы молча — маркер поставлен
    // в конце ответа, а спросить в том же ходу координатор уже не может.
    // team — лимит раундов уже мог быть исчерпан РАНЬШЕ в той же итерации (несколько
    // clarify-тупиков подряд): раньше текст безусловно велел «задай вопросы», что в одном
    // и том же ходу противоречило InterviewProtocol из CoordinatorTurn («лимит исчерпан,
    // больше не спрашивай») — модель получала два взаимоисключающих указания разом.
    public static string ClarifyInterviewTurn(string reason, SessionTeamImplement team)
    {
        var exhausted = InterviewRoundsExhausted(team);
        var instruction = exhausted
            ? "Раунды вопросов на эту вводную уже исчерпаны — не спрашивай снова, оформи неясность " +
              "допущением и сразу дай маркер работы: команда пересоберёт план с ним и покажет на подтверждение."
            : InterviewOpeningLine + " Задай вопросы инструментом AskUserQuestion (не текстом), а после " +
              "ответов человека дай маркер работы — команда пересоберёт план и покажет его на подтверждение.";
        return "[Режим «Командная реализация»] Волны на паузе — ты сообщил, что требования неясны: " +
               $"«{FirstLine(reason)}». " + instruction;
    }

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

    // Тупик clarify-интервью (M9): карточка «Нужны уточнения» обещала вопросы, а ход
    // координатора закончился текстом — ни ASK-карточки, ни маркера. Волны на паузе, и без
    // этой карточки практика молча стояла бы вечно: сторож волн в интервью не тикает.
    public static string ClarifyStallDetails(string turnText, int wave) =>
        $"Волна {wave} на паузе, но обещанные вопросы так и не пришли: координатор закончил ход "
        + $"без ASK-карточки и без маркера работы — «{FirstLine(turnText)}» "
        + "Ответьте сообщением в чат, что делать дальше — команда продолжит с того же места.";

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

    // Реплика человеку по клику в устаревшую карточку плана (M8): решение по ней ничего не
    // меняет — работа идёт по актуальной версии, и решать надо по ней.
    public static string StalePlanCardNotice(int cardVersion, int currentVersion) =>
        $"Эта карточка плана устарела: она про версию v{cardVersion}, а актуальная — v{currentVersion}. "
        + "Решение по ней ничего не меняет — карточка закрыта. "
        + "Найдите в ленте свежую карточку плана и решайте по ней.";

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
