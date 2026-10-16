#!/usr/bin/env python3
# Сжатие с сохранением дословного (шаг 3): голова + реплики человека.
#
# Чистые функции над телом Anthropic Messages (doc["messages"]), без сети и состояния:
# прокси вызывает их только на маршруте сжатия, остальные запросы остаются «трубой».
# Всё выводится из тела запроса — состояние между запросами не держим.
#
# Маркер реплики человека (контракт шага 1, ClaudeSession.WithHumanTextMarker):
#   <!-- ccs:human-text chars=N sha=XXXXXXXX -->\n + сырой текст
# N — длина сырой реплики в КОДОВЫХ ТОЧКАХ (EnumerateRunes в C#, не .Length: эмодзи
# по UTF-16 разойдутся), sha — первые 8 hex (нижний регистр) SHA-256 от UTF-8 байтов
# сырой реплики. Для КАЖДОГО вхождения берём N кодовых точек сразу после "-->\n" и
# принимаем только то, чей хеш совпал: цитата маркера в recall/заметке/реплике хеш не
# пройдёт. Размеченный чат (есть хоть один верный маркер) → человеческие = только
# размеченные куски.
#
# Старый чат (верных маркеров нет) → эвристика: если в тексте есть заголовки хвоста хода
# («## Память команды проекта», «Возможно релевантные заметки») — реплика = текст после
# ПОСЛЕДНЕГО "\n\n---\n\n"; иначе — весь текст. <system-reminder>…</system-reminder>
# вырезаем всегда. Сомнение — в пользу «оставить больше».
#
# Сводка прошлого сжатия CLI (user-сообщение, начинающееся с «This session is being
# continued from a previous conversation») — ни реплика, ни голова: её пропускаем.
import hashlib
import re

# Потолки в КОДОВЫХ ТОЧКАХ (Python str — последовательность кодовых точек).
HEAD_CAP = 15000
HUMANS_CAP = 30000

# Формат маркера — зеркалит константу ClaudeSession.HumanTextMarkerFormat
# ("<!-- ccs:human-text chars={0} sha={1} -->"). Тест-константа сверяет эту строку с
# константой в ClaudeSession.cs: разойдутся — красный.
MARKER_FORMAT = "<!-- ccs:human-text chars={0} sha={1} -->"

# Маркер + "\n" + сырой текст. {n} — кодовые точки, {s} — 8 hex SHA-256.
_MARKER_RE = re.compile(r"<!--\s*ccs:human-text\s+chars=(\d+)\s+sha=([0-9a-fA-F]{8})\s*-->\n")

# Заголовки секций хвоста хода из бэкенда (ClaudeSession: turnRecallSections /
# alwaysTailSections). Их наличие в тексте старой реплики означает, что перед ними
# (до последнего "---") — обвязка, а реплика человека — после.
#
# Полный список заголовков (сверен по коду бэкенда):
#   «## Память команды проекта»        — TeamMemoryService.cs:26
#   «Возможно релевантные заметки»     — NotesKnowledgeService.cs:77
#   «## Контекст хода»                  — TurnContextContributor.cs:95
#   «## Картинки в этом чате»          — ImageEditorStateContributor.cs:89
#   «## Звук в этом чате»              — AudioEditorStateContributor.cs:51
#   «## Видео в этом чате»             — VideoEditorStateContributor.cs:110
#   «## Картинки и видео: локальная модель по умолчанию» — LocalMediaDefaultContributor.cs:74
#   «## Структура кода проекта»        — CodeGraphPromptProvider.cs:148
#   «### Привязанные знания и правила» — PersonaBindingsService.cs:780
#
# НЕ включаем: «## ЗАДАЧА / ## ПРАВИЛА / ## КОНТЕКСТ / ## Возможно релевантные заметки»
# — это части текста задачи-исполнителя (пишет бэкенд), а не хвост хода.
_TAIL_HEADERS = (
    "## Память команды проекта",
    "Возможно релевантные заметки",
    "## Контекст хода",
    "## Картинки в этом чате",
    "## Звук в этом чате",
    "## Видео в этом чате",
    "## Картинки и видео: локальная модель по умолчанию",
    "## Структура кода проекта",
    "### Привязанные знания и правила",
)

# Сводка прошлого сжатия CLI: user-сообщение, начинающееся с этой строки — не реплика и не
# голова (это сводка, а не человек).
_CLI_SUMMARY_PREFIX = "This session is being continued from a previous conversation"

# <system-reminder>…</system-reminder> — служебная обвязка (память, заметки, правила),
# не текст человека: вырезаем всегда.
_SYS_REMINDER_RE = re.compile(r"<system-reminder>.*?</system-reminder>", re.DOTALL)


def _sha8(text):
    """Первые 8 hex SHA-256 (нижний регистр) от UTF-8 байтов текста."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]


def _text_blocks(msg):
    """Текст user-сообщения: content — строка или массив блоков.

    Строка — как есть. Массив: склеиваем text-блоки; сообщение, состоящее из tool_result
    (без text-блоков), — не реплика (вывод инструмента, не человек). Картинки (image)
    text-блоков не дают — такие сообщения не считаются репликами.
    """
    c = msg.get("content")
    if isinstance(c, str):
        return c
    if isinstance(c, list):
        texts = [b.get("text") or "" for b in c
                 if isinstance(b, dict) and b.get("type") == "text"]
        return "\n".join(texts)
    return ""


def _strip_system_reminder(text):
    """Вырезаем <system-reminder>…</system-reminder> (служебная обвязка, не человек)."""
    return _SYS_REMINDER_RE.sub("", text).strip()


def _old_chat_reply(text):
    """Старый чат (верных маркеров нет): извлекаем реплику человека из сырого текста.

    Заголовки хвоста хода («## Память команды проекта», «Возможно релевантные заметки»)
    — обвязка перед репликой. Идём от ПОСЛЕДНЕГО разделителя "\n\n---\n\n" назад и
    режем на том, у которого сегмент СЛЕВА (часть, стоящая непосредственно перед
    разделителем) содержит такой заголовок: это граница обвязки/реплики. Если реплика
    сама содержит "---" (без заголовка в сегменте слева) — её не режем. Нет ни одного
    такого разделителя — весь текст (сомнение → «оставить больше»).
    """
    if not any(h in text for h in _TAIL_HEADERS):
        return text.strip()
    parts = text.split("\n\n---\n\n")
    if len(parts) < 2:
        return text.strip()
    for i in range(len(parts) - 1, 0, -1):
        if any(h in parts[i - 1] for h in _TAIL_HEADERS):
            return "\n\n---\n\n".join(parts[i:]).strip()
    return text.strip()


def _is_cli_summary(text):
    """Сводка прошлого сжатия CLI — не реплика и не голова."""
    return text.lstrip().startswith(_CLI_SUMMARY_PREFIX)


def _marked_replies(msgs):
    """Размеченный чат: все верные маркеры (N кодовых точек + совпавший sha), в порядке
    сообщений. Возвращает список (текст, True) или None, если верных маркеров нет."""
    found = []
    for m in msgs:
        if m.get("role") != "user":
            continue
        text = _text_blocks(m)
        if not text:
            continue
        for mt in _MARKER_RE.finditer(text):
            n = int(mt.group(1))
            sha = mt.group(2).lower()
            # N кодовых точек сразу после "-->\n" (mt.end() — позиция "\n" после маркера).
            raw = text[mt.end():mt.end() + n]
            if len(raw) < n or _sha8(raw) != sha:
                continue
            found.append(raw)
    return found if found else None


def human_texts(messages):
    """Реплики человека в хронологическом порядке (list[str]).

    Размеченный чат → только размеченные куски (верные маркеры). Старый чат → эвристика
    (хвост хода / system-reminder). Сводка CLI и сообщения из tool_result — не реплики.
    """
    msgs = messages or []
    marked = _marked_replies(msgs)
    if marked is not None:
        return marked
    out = []
    for m in msgs:
        if m.get("role") != "user":
            continue
        text = _text_blocks(m)
        if not text.strip():
            continue
        if _is_cli_summary(text):
            continue
        reply = _strip_system_reminder(_old_chat_reply(text))
        if reply:
            out.append(reply)
    return out


def _truncate(text, cap):
    """Длиннее cap → начало и конец с меткой «[… пропущено N символов …]» (N — кодовые точки)."""
    if len(text) <= cap:
        return text
    keep = cap // 2
    head = text[:keep]
    tail = text[-keep:]
    return head + f"\n[… пропущено {len(text) - 2 * keep} символов …]\n" + tail


def head(messages, cap=HEAD_CAP):
    """Первая реплика человека (постановка задачи); длиннее cap → с меткой пропуска.

    None, если реплик нет.
    """
    texts = human_texts(messages)
    if not texts:
        return None
    return _truncate(texts[0], cap)


def _recent_humans_indexed(messages, cap=HUMANS_CAP):
    """Реплики человека от новых к старым, пока влезает в cap (кодовые точки).

    Возвращает list[(text, index)], где index — позиция реплики в ХРОНОЛОГИЧЕСКОМ
    списке human_texts (0 = постановка, самая старая). Одна длинная реплика — тоже с
    меткой пропуска, а не отбрасывается: берём её целиком (с обрезкой), если она
    влезает в оставшийся бюджет, и останавливаемся.
    """
    texts = human_texts(messages)
    out = []
    budget = cap
    for idx in range(len(texts) - 1, -1, -1):
        t = texts[idx]
        if not t:
            continue
        if len(t) <= budget:
            out.append((t, idx))
            budget -= len(t)
        else:
            out.append((_truncate(t, budget), idx))
            break
    return out


def recent_humans(messages, cap=HUMANS_CAP):
    """Реплики человека от новых к старым, пока влезает в cap (кодовые точки).

    Одна длинная реплика — тоже с меткой пропуска, а не отбрасывается: берём её целиком
    (с обрезкой), если она влезает в оставшийся бюджет, и останавливаемся.
    """
    return [t for t, _ in _recent_humans_indexed(messages, cap)]


# =============================================================================
# Шаг 4: якоря, изменённые файлы, проверки, план и сборка блока <ccs-preserved>
# =============================================================================

# Потолок блока <ccs-preserved> в КОДОВЫХ ТОЧКАХ.
PRESERVED_CAP = 60000

# Виды проверок (last_checks): подстрока в команде Bash / имя mcp-тула.
# npm: только build|test|lint|typecheck (npx tsc — тоже) — каждый скрипт отдельный вид;
# «npm run dev» (dev-сервер) — не проверка.
_CHECK_BASH_PATTERNS = ("dotnet build", "dotnet test", "npm run build", "npm run test",
                         "npm run lint", "npm run typecheck", "npx tsc",
                         "pytest", "python3 -m unittest")
_CHECK_MCP_TOOLS = ("mcp__dev__build", "mcp__tests__run_tests")

# Маркеры НЕНОЛЕВОГО падения в выводе проверки (is_error=false, но тесты/сборка упали).
# Нули не считаются: «Failed: 0», «0 failed», «упало 0» — маркер не дают.
#   Failed:\s*[1-9]   — dotnet test «Failed: 3»
#   [1-9]\d*\s+failed  — xUnit «3 failed»
#   упало\s*[1-9]      — «упало 3»
#   Build FAILED       — dotnet build
#   error CS\d+        — C#
#   error TS\d+        — TypeScript
#   FAILED \(          — pytest «FAILED (…»
#   ^FAIL              — xUnit «FAIL TestName» (в начале строки)
#   Traceback          — Python
# re.MULTILINE: ^ — начало каждой строки.
_FAILURE_RE = re.compile(
    r"Failed:\s*[1-9]|[1-9]\d*\s+failed|упало\s*[1-9]|Build FAILED"
    r"|error CS\d+|error TS\d+|FAILED \([^)]*\)|^FAIL|Traceback",
    re.MULTILINE)


def _has_failure_marker(text):
    """Ненулевое падение в выводе проверки: маркер в тексте (см. _FAILURE_RE)."""
    return bool(_FAILURE_RE.search(text or ""))

# Якоря (anchors): только дословные совпадения, не пересказ.
_ANCHOR_PATH = re.compile(r"(?:[A-Za-z]:)?(?:/[\w.\-~]+)+/[\w.\-]+\.\w{1,8}")
_ANCHOR_URL = re.compile(r"https?://\S+")
_ANCHOR_UUID = re.compile(
    r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
# SHA: 7-40 hex, но не длиннее 40: (?=[0-9a-fA-F]*\b) — граница на первом
# не-hex/белом после 40 (без неё \b после {7,40} не держит 41+ hex: \b не
# требует hex-границы, и «1234567890abcdef1234567890abcdef1234567890» (48 hex)
# резалось бы в 40).
_ANCHOR_SHA = re.compile(r"\b[0-9a-fA-F]{7,40}(?=[0-9a-fA-F]*\b)")
_ANCHOR_TASKID = re.compile(r"<task-id>([^<]+)</task-id>")
# «Первые строки ошибок»: маркеры, по которым строка считается ошибкой.
_ERROR_MARKERS = ("error", "exception", "fail", "ошибка")


def _tool_result_text(content):
    """Текст tool_result: content — строка или список блоков (склейка text-блоков)."""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "\n".join(b.get("text") or "" for b in content
                         if isinstance(b, dict) and b.get("type") == "text")
    return ""


def _tool_results_map(messages):
    """tool_use_id -> tool_result: парование вывода инструмента с tool_use."""
    out = {}
    for m in messages or []:
        c = m.get("content")
        if not isinstance(c, list):
            continue
        for b in c:
            if isinstance(b, dict) and b.get("type") == "tool_result":
                tid = b.get("tool_use_id")
                if tid:
                    out[tid] = b
    return out


def changed_files(messages):
    """Изменённые файлы по tool_use Edit/MultiEdit/Write/NotebookEdit.

    list[dict] {path, edits, write}, новые сверху (по последнему касанию).
    edits — число правок (Edit + MultiEdit), write — была ли перезапись (Write).
    """
    files = {}
    seq = 0
    for m in messages or []:
        if m.get("role") != "assistant":
            continue
        c = m.get("content")
        if not isinstance(c, list):
            continue
        for b in c:
            if not isinstance(b, dict) or b.get("type") != "tool_use":
                continue
            name = b.get("name")
            if name not in ("Edit", "MultiEdit", "Write", "NotebookEdit"):
                continue
            inp = b.get("input") or {}
            path = inp.get("file_path") or inp.get("notebook_path")
            if not path:
                continue
            seq += 1
            e = files.setdefault(path, {"edits": 0, "write": False, "last": 0})
            if name in ("Edit", "MultiEdit"):
                e["edits"] += 1
            if name == "Write":
                e["write"] = True
            e["last"] = seq
    out = []
    for path, e in sorted(files.items(), key=lambda kv: kv[1]["last"], reverse=True):
        out.append({"path": path, "edits": e["edits"], "write": e["write"]})
    return out


def last_checks(messages):
    """Последний запуск каждого вида проверки (сборка/тесты/tsc/pytest/unittest/mcp).

    list[dict] {kind, label, is_error, result}: kind — вид (подстрока команды Bash
    или имя mcp-тула), label — команда (Bash) или «имя: target», is_error — признак
    ошибки парного tool_result, result — первые ~600 кодовых точек вывода.
    Парование tool_use↔tool_result — по tool_use_id.
    """
    results = _tool_results_map(messages)
    last = {}
    for m in messages or []:
        if m.get("role") != "assistant":
            continue
        c = m.get("content")
        if not isinstance(c, list):
            continue
        for b in c:
            if not isinstance(b, dict) or b.get("type") != "tool_use":
                continue
            name = b.get("name")
            inp = b.get("input") or {}
            kinds = []
            if name == "Bash":
                cmd = inp.get("command") or ""
                for p in _CHECK_BASH_PATTERNS:
                    if p in cmd:
                        kinds.append(p)
            elif name in _CHECK_MCP_TOOLS:
                kinds.append(name)
            if not kinds:
                continue
            tr = results.get(b.get("id"))
            # Статус трёхзначный:
            #   «неизвестно» — парного tool_result нет (прерван или ещё идёт);
            #   «ошибка»     — is_error=true ИЛИ маркер ненулевого падения в выводе;
            #   «ok»         — пара есть, is_error=false и маркеров падения нет.
            if tr is None:
                is_error, status, result = False, "неизвестно", ""
            else:
                is_error = bool(tr.get("is_error"))
                full = _tool_result_text(tr.get("content"))
                status = "ошибка" if (is_error or _has_failure_marker(full)) else "ok"
                result = full[:600]
            if name == "Bash":
                label = (inp.get("command") or "")[:200]
            else:
                target = inp.get("target") or inp.get("project") or ""
                label = f"{name}: {target}".strip(": ")
            for k in kinds:
                last[k] = {"kind": k, "label": label, "is_error": is_error,
                            "status": status, "result": result}
    return list(last.values())


def last_plan(messages):
    """Последний TodoWrite (компактно, по строке на задачу) + последний ExitPlanMode.plan.

    str|None, потолок 6000 кодовых точек.
    """
    todo = None
    plan = None
    for m in messages or []:
        if m.get("role") != "assistant":
            continue
        c = m.get("content")
        if not isinstance(c, list):
            continue
        for b in c:
            if not isinstance(b, dict) or b.get("type") != "tool_use":
                continue
            name = b.get("name")
            inp = b.get("input") or {}
            if name == "TodoWrite":
                todos = inp.get("todos") or []
                if todos:
                    todo = todos
            elif name == "ExitPlanMode":
                p = inp.get("plan")
                if p:
                    plan = p
    parts = []
    if todo:
        lines = []
        for t in todo:
            if not isinstance(t, dict):
                continue
            mark = "x" if t.get("status") == "completed" else " "
            content = (t.get("content") or "").strip()
            if content:
                lines.append(f"[{mark}] {content}")
        if lines:
            parts.append("\n".join(lines))
    if plan:
        parts.append(str(plan))
    if not parts:
        return None
    return "\n".join(parts)[:6000]


def _anchor_candidates(text, is_tool_result=False):
    """Дословные якоря в тексте: пути, URL, UUID, id задач, SHA (рядом с git/commit/коммит),
    строки ошибок. Порядок — порядок вхождения в тексте.

    is_tool_result: строки ошибок берём ТОЛЬКО из вывода инструмента (не из прозы человека
    и ассистента); одну строку урезаем до 300 кодовых точек.
    """
    out = []
    for mt in _ANCHOR_PATH.finditer(text):
        out.append(mt.group(0))
    for mt in _ANCHOR_URL.finditer(text):
        out.append(mt.group(0))
    for mt in _ANCHOR_UUID.finditer(text):
        out.append(mt.group(0))
    for mt in _ANCHOR_TASKID.finditer(text):
        out.append(mt.group(1))
    # SHA: 7-40 hex, хотя бы одна буква a-f (чистые числа/даты/размеры — не SHA),
    # рядом со словом git/commit/коммит (слово целиком, не подстрока «digit»/«legit»).
    for mt in _ANCHOR_SHA.finditer(text):
        sha = mt.group(0)
        if not re.search(r"[a-fA-F]", sha):
            continue
        ctx = text[max(0, mt.start() - 40):mt.end() + 40].lower()
        if (re.search(r"\bgit\b", ctx) or re.search(r"\bcommit\b", ctx)
                or re.search(r"\bкоммит\w*", ctx)):
            out.append(sha)
    # строки ошибок: только из tool_result, одна строка ≤ 300 кодовых точек
    if is_tool_result:
        for ln in text.splitlines():
            low = ln.lower()
            if any(mk in low for mk in _ERROR_MARKERS) or re.search(r"error\s+cs\d+", low):
                s = ln.strip()
                if s:
                    out.append(s[:300])
    return out


def anchors(messages, cap=8000):
    """Дословные якоря (пути, SHA, UUID, id задач, URL, строки ошибок) из истории.

    list[str], новые сверху, дедуп, обрезка по cap (кодовые точки).
    """
    seen = set()
    out = []
    # новые сверху: идём от новых сообщений к старым, дедуп держит самое свежее вхождение
    for m in reversed(messages or []):
        # text-блоки: пути, URL, UUID, taskid, SHA (строки ошибок — нет)
        text = _text_blocks(m)
        if text:
            for cand in _anchor_candidates(text, is_tool_result=False):
                if cand not in seen:
                    seen.add(cand)
                    out.append(cand)
        # tool_result: строки ошибок (только отсюда, не из прозы человека/ассистента)
        c = m.get("content")
        if isinstance(c, list):
            for b in c:
                if not (isinstance(b, dict) and b.get("type") == "tool_result"):
                    continue
                tr_text = _tool_result_text(b.get("content"))
                if not tr_text:
                    continue
                for cand in _anchor_candidates(tr_text, is_tool_result=True):
                    if cand not in seen:
                        seen.add(cand)
                        out.append(cand)
    # обрезка по cap: берём с новых (сверху), пока влезает
    total = 0
    kept = []
    for a in out:
        if total + len(a) > cap and kept:
            break
        kept.append(a)
        total += len(a)
    return kept


def _fmt_files(files):
    lines = []
    for f in files:
        extra = []
        if f["edits"]:
            extra.append(f"{f['edits']} правок")
        if f["write"]:
            extra.append("перезапись")
        suffix = f" ({', '.join(extra)})" if extra else ""
        lines.append(f"{f['path']}{suffix}")
    return lines


def _fmt_checks(checks):
    lines = []
    for c in checks:
        lines.append(f"[{c['kind']}] {c['label']} — {c['status']}")
        if c["result"]:
            lines.append("    " + c["result"].replace("\n", "\n    "))
    return lines


def build_preserved(doc):
    """Блок <ccs-preserved>: постановка + реплики + файлы + проверки + план + якоря.

    str|None (None, если ничего нет вовсе). Пустые разделы не выводим. Общий потолок
    PRESERVED_CAP: при превышении режем в порядке «якоря → проверки → файлы → старые
    реплики»; постановка и самая свежая реплика — никогда.
    """
    msgs = (doc or {}).get("messages") or []
    head_text = head(msgs)
    # Реплики с индексом: постановка (index 0) уже в «Постановке» — из «Реплик» её
    # исключаем, чтобы не дублировать. Индекс — позиция в истории, не текст: одинаковые
    # короткие «Продолжай» не схлопываются.
    humans_indexed = _recent_humans_indexed(msgs)
    humans = [t for t, idx in humans_indexed if idx != 0]
    files = changed_files(msgs)
    checks = last_checks(msgs)
    plan = last_plan(msgs)
    anchors_list = anchors(msgs)

    # Разделы: key -> (title, lines). Порядок в блоке фиксирован.
    secs = {}
    if head_text:
        secs["head"] = ("## Постановка задачи (дословно)", [head_text])
    if humans:
        secs["humans"] = ("## Реплики человека (дословно, новые сверху)", list(humans))
    if files:
        secs["files"] = ("## Изменённые файлы", _fmt_files(files))
    if checks:
        secs["checks"] = ("## Последние проверки", _fmt_checks(checks))
    if plan:
        secs["plan"] = ("## План и задачи", plan.splitlines())
    if anchors_list:
        secs["anchors"] = ("## Якоря (дословно из истории)", list(anchors_list))

    if not secs:
        return None

    order = ["head", "humans", "files", "checks", "plan", "anchors"]

    def assemble():
        parts = ["<ccs-preserved>"]
        for k in order:
            if k in secs:
                title, lines = secs[k]
                if not lines:
                    continue
                parts.append(title)
                parts.extend(lines)
        parts.append("</ccs-preserved>")
        return "\n".join(parts)

    if len(assemble()) <= PRESERVED_CAP:
        return assemble()

    # Урезаем по приоритету: якоря → проверки → файлы → (старые реплики по одной) → план.
    # «head» (постановка) и самая свежая реплика (humans[0]) — никогда.
    for k in ("anchors", "checks", "files"):
        if len(assemble()) <= PRESERVED_CAP:
            break
        secs.pop(k, None)
    # старые реплики: убираем с конца (oldest), newest (index 0) держим
    if "humans" in secs:
        while len(assemble()) > PRESERVED_CAP and len(secs["humans"][1]) > 1:
            secs["humans"][1].pop()
    if len(assemble()) > PRESERVED_CAP:
        secs.pop("plan", None)
    return assemble()


def instruction_addendum():
    """Дополнение к инструкции сжатия CLI (по-английски, как сама инструкция).

    Текст блока <ccs-preserved> модели НЕ передаём — только инструкцию.
    """
    return (
        "A <ccs-preserved> block is inserted verbatim before your summary. It holds the "
        "task statement, the human's replies, the changed files, the last checks, the plan "
        "and literal anchors. Do NOT restate the human's replies, the files, or the checks "
        "from it — refer to them. Spend your summary on the reasoning, the decisions, the "
        "rejected approaches, and the reasons. Keep your answer in the required "
        "<analysis>/<summary> format; the preserved block is inserted by the system, do "
        "not reproduce it."
    )
