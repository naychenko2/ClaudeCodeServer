using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Планирование режима «Командная реализация» (Э2): кто планирует, из кого выбирать и
// как получить структурный план с исполнителем под каждой под-задачей.
// Раздача задач и волны — Э3, здесь только план и его карточка.
// См. docs/architecture/team-implement-mode.md, раздел «Этапы → Э2».
public class TeamPlanningService(
    PersonaManager personas,
    ICheapTextRunner? cheap = null,
    ILogger<TeamPlanningService>? log = null)
{
    // Потолок под-задач в плане: совпадает с дефолтом бюджета итерации (12 задач).
    // План длиннее бюджета бессмысленен — волны всё равно упрутся в потолок.
    public const int MaxSubtasks = 12;
    // Кандидатов в промпт — не больше: команда крупнее размывает выбор и раздувает промпт
    public const int MaxCandidates = 20;

    // Причина отказа планировщика. None — план собран. TimedOut / Truncated /
    // InvalidJson — отказы с разным текстом для человека и разной диагностикой в логе
    // (прод 2026-08-05: таймаут и обрыв по лимиту вывода давали одну пустоту, разбор
    // каждого сбоя превращался в раскопки). Failed — всё прочее (модель не настроена,
    // вызов упал, JSON без под-задач и т.п.).
    public enum Failure { None, TimedOut, Truncated, InvalidJson, Failed }

    // Текст для карточки отказа. Ключи — Failure (None не отдаётся, план есть).
    // Разные причины — разные советы: таймаут «это не ваша вина, повторите», обрыв
    // «упростите задачу, уменьшите число кандидатов» (это пройдёт через бóльший
    // CloudNumPredict после фикса), невалидный JSON «повторите планирование», сбой —
    // как сейчас «уточните задачу». Решение о длинных планах — за человеком, не за
    // планировщиком: если вводная большая и явно не лезет — он сократит или раздробит.
    public const string PlannerTimeoutReason = "Планировщик не уложился во время";
    public const string PlannerTruncatedReason = "Планировщик не уместил план в лимит вывода";
    public const string PlannerInvalidJsonReason = "Планировщик вернул неразборчивый план";

    // Маршрут модели планировщика (для карточки и лога). Делаем отдельной записью,
    // а не вкладываем в (Plan, Failure) — тестам и логике карточки так удобнее.
    public sealed record RouteInfo(string? Model, bool Local);

    // Итог планирования. Plan = null — отказ; Failure и Route уточняют причину и
    // помогают карточке и логу. Diagnostics — длина промпта/ответа и финальный маршрут
    // (только в лог, в карточку не идёт: человек увидит причину, а не «длина 3.2k»).
    public sealed record Result(
        TeamImplementPlan? Plan,
        Failure Failure,
        RouteInfo? Route,
        int PromptChars,
        int ResponseChars,
        TimeSpan Elapsed);

    // Сводный маршрут для лога и события: kind (claude/tier/model/local) + модель/тир.
    public string DescribeRoute(string? actionKey, string? plannerModel) =>
        actionKey is null
            ? "no-runner"
            : (cheap?.DescribeRoute(actionKey, plannerModel) ?? "no-runner");

    // Координатор режима — СОБЕСЕДНИК ЧАТА. Чат без персоны координатора не имеет:
    // режим требует выбрать его явно (фронт показывает пикер при включении).
    // Явно сохранённый в состоянии режима id приоритетнее — им фронт и пользуется.
    public Persona? ResolveCoordinator(Session session, string ownerId)
    {
        var id = session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId;
        return id is null ? null : personas.Get(id, ownerId);
    }

    // Планировщик: явно заданный → по специальности (Planner → Coordinator → Analyst)
    // среди кандидатов → сам координатор. Специальность — машинный тег способности
    // (PersonaSpecialty), поэтому подбор идёт по ней, а не по тексту роли.
    public Persona? ResolvePlanner(Session session, string ownerId, IReadOnlyList<Persona> candidates)
    {
        if (session.TeamImplement?.PlannerPersonaId is { } explicitId
            && personas.Get(explicitId, ownerId) is { } explicitPlanner)
            return explicitPlanner;

        var coordinator = ResolveCoordinator(session, ownerId);
        // Пул поиска — кандидаты плюс координатор: планировщиком может быть и он сам,
        // даже когда в состав исполнителей его не включили.
        var pool = candidates.ToList();
        if (coordinator is not null && pool.All(p => p.Id != coordinator.Id)) pool.Add(coordinator);

        foreach (var wanted in (ReadOnlySpan<PersonaSpecialty>)
                 [PersonaSpecialty.Planner, PersonaSpecialty.Coordinator, PersonaSpecialty.Analyst])
            if (pool.FirstOrDefault(p => p.Specialty == wanted) is { } found)
                return found;

        return coordinator;
    }

    // Состав кандидатов: явно выбранные персоны либо (пустой список) вся команда проекта.
    // Вне проекта команды нет — без явного выбора список пуст, и режим объясняет почему
    // (см. «Состав команды» продуктового плана).
    public IReadOnlyList<Persona> ResolveCandidates(Session session, string ownerId)
    {
        var explicitIds = session.TeamImplement?.ExecutorPersonaIds ?? [];
        if (explicitIds.Count > 0)
            return explicitIds.Select(id => personas.Get(id, ownerId)).OfType<Persona>().ToList();

        if (session.ProjectId is null) return [];
        // Команда проекта = доступные в его контексте персоны (глобальные + проектные)
        return personas.GetForContext(ownerId, session.ProjectId)
            .Where(p => p.Id != session.TeamImplement?.CoordinatorPersonaId)
            .Take(MaxCandidates).ToList();
    }

    // Карточка кандидата для промпта планировщика: по ней он и подбирает исполнителя.
    // Привязки к папкам/знаниям — самый предметный признак «за что персона отвечает».
    public static TeamCandidateCard BuildCard(Persona p) => new(
        p.Id, p.Handle, p.Name, p.Role, p.Description,
        p.Specialty == PersonaSpecialty.None ? null : SpecialtyLabel(p.Specialty),
        BuildBindingHints(p));

    // Привязки персоны человекочитаемо: только предметные (папки проекта и базы знаний) —
    // рубильники инструментов и скиллы к выбору исполнителя отношения не имеют.
    private static IReadOnlyList<string> BuildBindingHints(Persona p)
    {
        if (p.Bindings is not { Count: > 0 } bindings) return [];
        var hints = new List<string>();
        foreach (var b in bindings.Where(b => b.Mode != PersonaBindingMode.Off))
        {
            var hint = b.Type switch
            {
                PersonaBindingType.ProjectPath when !string.IsNullOrWhiteSpace(b.Path) => $"папка {b.Path}",
                PersonaBindingType.Knowledge => "база знаний",
                PersonaBindingType.Notes => "заметки",
                _ => null,
            };
            if (hint is null) continue;
            // Условие привязки («когда применять») — самая содержательная часть карточки
            if (!string.IsNullOrWhiteSpace(b.Condition)) hint += $" — {b.Condition.Trim()}";
            hints.Add(hint);
            if (hints.Count >= 5) break;
        }
        return hints;
    }

    // Подписи — из единого источника (SpecialtyCatalog): исполнительские специальности
    // с профильными вариантами и актуальными подписями. Здесь намеренно СИСТЕМНАЯ подпись
    // из каталога: промпт планировщика — машинный контракт, ответ сверяется с ключами ролей;
    // личное имя живёт только в интерфейсе (решение владельца об отмене персонализации
    // подписей роли, см. docs/architecture/features.md §Специальности).
    private static string SpecialtyLabel(PersonaSpecialty s) =>
        s == PersonaSpecialty.None ? "" : SpecialtyCatalog.Label(s);

    // Промпт планировщика. Главное требование Э2: у КАЖДОЙ под-задачи есть исполнитель
    // из списка кандидатов и одна строка обоснования — бэкендовая часть уходит бэкендеру,
    // фронтовая фронтендеру. Отсюда и жёсткий контракт ответа: только JSON.
    // feedback — правка человека к текущему плану («Изменить план»): план пересобирается
    // именно под неё; без previous она смысла не имеет и игнорируется.
    public static string BuildPlannerPrompt(string request, IReadOnlyList<TeamCandidateCard> cards,
        string? projectHint = null, TeamImplementPlan? previous = null, string? feedback = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты планировщик командной реализации. Разбей задачу на под-задачи и раздай их " +
                      "исполнителям из списка ниже — каждому то, что по его профилю.");
        sb.AppendLine();
        sb.AppendLine("ЗАДАЧА:");
        sb.AppendLine(request.Trim());
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(projectHint))
        {
            sb.AppendLine($"ПРОЕКТ: {projectHint.Trim()}");
            sb.AppendLine();
        }
        // Перепланирование (Э8): план строится не с нуля, а поверх предыдущего — иначе
        // «что изменилось» планировщику взять неоткуда, и человек сверял бы два состава глазами.
        if (previous is not null)
        {
            sb.AppendLine($"ПРЕДЫДУЩИЙ ПЛАН (версия {previous.Version}) — его надо пересобрать с учётом задачи выше:");
            foreach (var s in previous.Subtasks)
                sb.AppendLine($"- {s.Title} (волна {s.Wave}" +
                              (s.TaskId is not null ? ", уже роздана исполнителю" : "") + ")");
            sb.AppendLine();
        }
        // Правка человека («Изменить план»): причина пересборки. Стоит отдельным блоком,
        // а не вливается в ЗАДАЧУ — планировщик обязан применить её дословно, как значения
        // из вводной, и отразить в changes.
        if (previous is not null && !string.IsNullOrWhiteSpace(feedback))
        {
            sb.AppendLine("ПРАВКА ЧЕЛОВЕКА К ПЛАНУ — применить в точности, это и есть причина пересборки:");
            sb.AppendLine(feedback.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("ИСПОЛНИТЕЛИ (выбирать ТОЛЬКО из них, по personaId):");
        foreach (var c in cards)
        {
            sb.Append($"- personaId={c.PersonaId} · @{c.Handle} · {c.Name}");
            if (!string.IsNullOrWhiteSpace(c.Role)) sb.Append($" — {c.Role}");
            if (!string.IsNullOrWhiteSpace(c.Specialty)) sb.Append($" [специальность: {c.Specialty}]");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(c.Description)) sb.AppendLine($"    описание: {c.Description.Trim()}");
            if (c.Bindings.Count > 0) sb.AppendLine($"    отвечает за: {string.Join("; ", c.Bindings)}");
        }
        sb.AppendLine();
        sb.AppendLine("ПРАВИЛА:");
        sb.AppendLine("1. Под-задачи независимы по файлам: два исполнителя не правят один файл в одной волне.");
        sb.AppendLine("2. У каждой под-задачи ОБЯЗАТЕЛЬНО executorPersonaId из списка выше и " +
                      "executorRationale — ОДНА строка, почему именно он (по роли, специальности, привязкам).");
        sb.AppendLine("3. Работу распределяй по профилю: бэкенд — бэкендеру, фронтенд — фронтендеру, " +
                      "дизайн — дизайнеру, тесты — тестировщику. Не раздавай по кругу.");
        sb.AppendLine($"4. Волны: wave=1 идёт первой, зависимые части — в следующие волны. Под-задач не больше {MaxSubtasks}.");
        sb.AppendLine("5. files — файлы/папки во владении под-задачи; doneCriteria — как проверить, что готово.");
        sb.AppendLine("6. assumptions — что ты додумал за человека: неочевидные решения, принятые без его ответа. " +
                      "Он утверждает их вместе с планом, поэтому пиши по делу и без воды. Нечего додумывать — пустой список.");
        sb.AppendLine("6a. НИКАКИХ плейсхолдеров и шаблонных скобок вроде «<файл-1 в корне проекта>», " +
                      "«<заданная строка>», «[имя файла]» — ни в title/goal/doneCriteria, ни в files. Если " +
                      "человек в ЗАДАЧЕ выше назвал конкретный файл, строку, значение или путь — перенеси " +
                      "его дословно. Если конкретики в задаче нет и её должен назвать человек — не " +
                      "выдумывай и не оставляй метку-заглушку: сформулируй разумное допущение и добавь его " +
                      "в assumptions, а под-задаче дай goal/doneCriteria без пропусков, которые исполнитель " +
                      "сможет выполнить и без этого значения.");
        sb.AppendLine("6b. Дословный перенос — это не только про пустографки: заменить названное человеком " +
                      "«hello5.txt» на своё «smoke5.txt» или сократить точную строку содержимого — такая же " +
                      "ошибка, как оставить плейсхолдер (прод 2026-08-03). Придумывать свои имена, пути и " +
                      "тексты можно ТОЛЬКО там, где человек не задал конкретики вовсе — и это тоже допущение.");
        sb.AppendLine("7. intent — замысел на 3–5 строк: к чему идём, какие ключевые решения приняты и что " +
                      "осознанно не делаем. Это то, что человек читает ПЕРВЫМ, ещё до состава под-задач, — " +
                      "по нему он и проверяет, что команда поняла задачу верно. Без технических деталей — " +
                      "им место в под-задачах.");
        if (previous is not null)
            sb.AppendLine("8. changes — чем этот план отличается от предыдущего: по строке на изменение " +
                          "(добавлено, убрано, переехало в другую волну, сменился исполнитель).");
        sb.AppendLine();
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом без пояснений и без markdown-обёртки:");
        sb.AppendLine(previous is null
            ? """
            {"summary":"одна строка что делаем","intent":"замысел на 3-5 строк","assumptions":[""],
             "subtasks":[{"title":"","goal":"","executorPersonaId":"","executorRationale":"",
                          "files":[""],"wave":1,"doneCriteria":""}]}
            """
            : """
            {"summary":"одна строка что делаем","intent":"замысел на 3-5 строк","assumptions":[""],"changes":[""],
             "subtasks":[{"title":"","goal":"","executorPersonaId":"","executorRationale":"",
                          "files":[""],"wave":1,"doneCriteria":""}]}
            """);
        sb.Append("По-русски.");
        return sb.ToString();
    }

    // Построить план: промпт планировщику → JSON → валидация исполнителей.
    // Plan = null — нет кандидатов, не настроен раннер или модель не вернула валидный план;
    // Failure уточняет причину отказа — вызывающая сторона (SessionManager) объясняет её
    // человеку карточкой/сообщением. Route + диагностика уходят в лог и событие планировщика.
    public async Task<Result> CreatePlanAsync(Session session, string ownerId,
        string request, string? projectHint = null, CancellationToken ct = default,
        // Перепланирование после интервью (Э8): предыдущая версия плана, поверх которой
        // строится vN — из неё берётся блок «Что изменилось».
        TeamImplementPlan? previous = null,
        // Правка человека к плану («Изменить план»): план пересобирается под неё.
        string? feedback = null)
    {
        var promptChars = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var route = new RouteInfo(Model: null, Local: false);

        if (cheap is null) return EmptyResult(Failed());
        if (string.IsNullOrWhiteSpace(request)) return EmptyResult(Failed());

        var candidates = ResolveCandidates(session, ownerId);
        if (candidates.Count == 0) return EmptyResult(Failed());

        var planner = ResolvePlanner(session, ownerId, candidates);
        var cards = candidates.Select(BuildCard).ToList();
        var prompt = BuildPlannerPrompt(request, cards, projectHint, previous, feedback);
        promptChars = prompt.Length;

        var actionKey = LocalActionCatalog.TeamImplementPlan;
        var fallback = planner?.Model;

        string raw;
        try
        {
            raw = await cheap.RunAsync(actionKey, prompt,
                fallbackModel: fallback, ownerId: ownerId, ct: ct);
        }
        catch (LlmTimeoutException ex)
        {
            // Диагностика: таймаут — главный враг планировщика в проде, и без длин
            // промпта/ответа каждый разбор превращался в раскопки. Маршрут и модель —
            // из роутера, чтобы видеть, какой шаг цепочки не уложился.
            log?.LogWarning(ex,
                "Планировщик командной реализации не уложился во время: сессия {SessionId}, маршрут {Route}, " +
                "модель {Model}, длина промпта {PromptChars} символов, заняло {Elapsed} мс",
                session.Id, DescribeRoute(actionKey, fallback), fallback ?? "-",
                promptChars, sw.ElapsedMilliseconds);
            return new Result(null, Failure.TimedOut, route, promptChars, 0, sw.Elapsed);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex,
                "Планировщик командной реализации не ответил: сессия {SessionId}, маршрут {Route}, " +
                "модель {Model}, длина промпта {PromptChars} символов, заняло {Elapsed} мс",
                session.Id, DescribeRoute(actionKey, fallback), fallback ?? "-",
                promptChars, sw.ElapsedMilliseconds);
            return new Result(null, Failure.Failed, route, promptChars, 0, sw.Elapsed);
        }

        var plan = ParsePlan(raw, request, candidates, out var looksTruncated);
        if (plan is null)
        {
            // Два неудачных исхода, оба без плана: (1) JSON не распарсился и при этом
            // похож на обрезку (открыли скобку и не закрыли) — это провайдер выдал
            // первые N токенов и заткнулся; (2) JSON распарсился, но под-задач в нём нет.
            // Первый — единственный с собственным текстом для человека, потому что совет
            // другой: «план не уместился, попробуйте короче». Второй — общий сбой парсера.
            var reason = looksTruncated ? Failure.Truncated : Failure.InvalidJson;
            log?.LogWarning(
                "Планировщик вернул неразборчивый план: сессия {SessionId}, маршрут {Route}, модель {Model}, " +
                "длина промпта {PromptChars} символов, ответа {ResponseChars} символов, " +
                "похоже на обрезку {LooksTruncated}, заняло {Elapsed} мс",
                session.Id, DescribeRoute(actionKey, fallback), fallback ?? "-",
                promptChars, raw.Length, looksTruncated, sw.ElapsedMilliseconds);
            return new Result(null, reason, route, promptChars, raw.Length, sw.Elapsed);
        }

        plan.PlannerPersonaId = planner?.Id;
        return new Result(plan, Failure.None, route, promptChars, raw.Length, sw.Elapsed);

        static Failure Failed() => Failure.Failed;
        static Result EmptyResult(Failure f) => new(null, f, null, 0, 0, TimeSpan.Zero);
    }

    // Разбор ответа планировщика. Под-задачи без валидного исполнителя не выбрасываем:
    // исполнитель проставляется первым кандидатом с пометкой, что выбор не обоснован —
    // человек увидит это в карточке и поправит до запуска (это и есть страховка Э2).
    // looksTruncated — out-параметр: raw похож на обрез по лимиту вывода (есть открытая
    // скобка, но баланс не сошёлся). Используется для отдельного Failure.Truncated
    // (прод 2026-08-05: 1024 токенов обрывали план и сливались с таймаутом).
    internal static TeamImplementPlan? ParsePlan(string raw, string request,
        IReadOnlyList<Persona> candidates, out bool looksTruncated)
    {
        looksTruncated = false;
        var json = ExtractJsonObject(raw, ref looksTruncated);
        if (json is null) return null;

        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); root = doc.RootElement; }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("subtasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var byId = candidates.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var byHandle = candidates
                .GroupBy(c => c.Handle, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var fallback = candidates[0];

            var plan = new TeamImplementPlan
            {
                Request = request,
                Summary = ReadString(root, "summary") ?? "",
                // Блоки карточки Э8: допущения планировщика и — у версии vN — «что изменилось».
                // Обоих может не быть: у первого плана нет изменений, а у ясной постановки
                // с ответами человека может не быть и допущений.
                Assumptions = ReadStringArray(root, "assumptions"),
                Changes = ReadStringArray(root, "changes"),
                Intent = ReadString(root, "intent")?.Trim() ?? "",
            };

            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var title = ReadString(e, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                // Модель иногда отдаёт handle вместо id — принимаем оба, чужие значения отсекаем
                var rawExecutor = ReadString(e, "executorPersonaId");
                var executor = rawExecutor is not null
                    && (byId.TryGetValue(rawExecutor, out var byIdHit) ? byIdHit
                        : byHandle.GetValueOrDefault(rawExecutor.TrimStart('@')))
                    is { } hit ? hit : null;

                var rationale = ReadString(e, "executorRationale")?.Trim() ?? "";
                if (executor is null)
                {
                    executor = fallback;
                    rationale = "Планировщик не указал исполнителя — проверьте выбор";
                }
                else if (rationale.Length == 0)
                    rationale = "Планировщик не обосновал выбор — проверьте";

                plan.Subtasks.Add(new TeamImplementSubtask
                {
                    Title = title.Trim(),
                    Goal = ReadString(e, "goal")?.Trim() ?? "",
                    ExecutorPersonaId = executor.Id,
                    ExecutorRationale = rationale,
                    Files = ReadStringArray(e, "files"),
                    Wave = ReadWave(e),
                    DoneCriteria = ReadString(e, "doneCriteria")?.Trim() ?? "",
                });
                if (plan.Subtasks.Count >= MaxSubtasks) break;
            }

            return plan.Subtasks.Count == 0 ? null : plan;
        }
    }

    private static int ReadWave(JsonElement e)
    {
        if (!e.TryGetProperty("wave", out var w)) return 1;
        var n = w.ValueKind switch
        {
            JsonValueKind.Number when w.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(w.GetString(), out var i) => i,
            _ => 1,
        };
        return n < 1 ? 1 : n;
    }

    private static string? ReadString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<string> ReadStringArray(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            if (list.Count >= 20) break;
        }
        return list;
    }

    // Первый сбалансированный JSON-объект из ответа (модель любит обрамлять его текстом
    // или ```-заборами) — как в DocumentAiService/SkillTranslationService.
    // looksTruncated — внешний out: если объект НЕ найден, но в raw есть открытая
    // сбалансированная часть (depth > 0 в конце) — похоже на обрез по лимиту вывода.
    // Это не строгий признак: модель могла просто не закрыть комментарий или оборвать
    // текст за JSON; но в сочетании с большим raw и нулевым результатом парсинга —
    // самый честный сигнал, который у нас есть без чтения finish_reason.
    private static string? ExtractJsonObject(string raw, ref bool looksTruncated)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        // До сюда дошли — JSON-объект не закрыт. depth == 0 тут невозможно (иначе бы
        // вернулись выше): значит, depth >= 1, что и есть «открыли и не закрыли».
        looksTruncated = true;
        return null;
    }
}
