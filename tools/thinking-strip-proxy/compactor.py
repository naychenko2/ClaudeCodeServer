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

# Заголовки секций блока <ccs-preserved>: единый источник для сборки (build_preserved) и
# разбора прошлого блока (шаг 8, _prev_block). Заголовок — всегда отдельная строка, и
# разделитель блока — точное совпадение строки; заголовок внутри постановки (собственный
# markdown человека) — редкий случай, в нём секция обрезается на этой строке.
SEC_HEAD = "## Постановка задачи (дословно)"
SEC_HUMANS = "## Реплики человека (дословно, новые сверху)"
SEC_FILES = "## Изменённые файлы"
SEC_CHECKS = "## Последние проверки"
SEC_PLAN = "## План и задачи"
SEC_ANCHORS = "## Якоря (дословно из истории)"
_TITLE_KEY = {SEC_HEAD: "head", SEC_HUMANS: "humans", SEC_FILES: "files",
              SEC_CHECKS: "checks", SEC_PLAN: "plan", SEC_ANCHORS: "anchors"}

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
#   ^FAIL\b(?!_)       — xUnit «FAIL TestName» (в начале строки; «FAIL_SAFE ok» — не падение)
# re.MULTILINE: ^ — начало каждой строки.
_FAILURE_RE = re.compile(
    r"Failed:\s*[1-9]|[1-9]\d*\s+failed|упало\s*[1-9]|Build FAILED"
    r"|error CS\d+|error TS\d+|FAILED \([^)]*\)|^FAIL\b(?!_)",
    re.MULTILINE)
# Traceback — падение, только если зелёного итога нет: тесты, проверяющие обработку
# исключений, печатают трассы и всё равно проходят («OK», «Passed!», «N passed»).
_TRACEBACK_RE = re.compile(r"Traceback")
_GREEN_SUMMARY_RE = re.compile(
    r"^OK(?:\s|$)|Passed!|\b\d+\s+passed\b|Failed:\s*0\b|упало\s*0\b", re.MULTILINE)


def _has_failure_marker(text):
    """Ненулевое падение в выводе проверки: маркер в тексте (см. _FAILURE_RE)."""
    text = text or ""
    if _FAILURE_RE.search(text):
        return True
    return bool(_TRACEBACK_RE.search(text)) and not _GREEN_SUMMARY_RE.search(text)

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


# Экранируем теги <analysis>/<summary> (открывающие и закрывающие) в тексте блока:
# CLI 2.1.301 разбирает ответ сжатия регулярками по ПЕРВОМУ вхождению (вырезает первый
# <analysis>…</analysis>, заменяет первый <summary>(…)</summary> на «Summary:»). Блок
# <ccs-preserved> дописывается ПОСЛЕ сводки: если модель не написала свой <analysis>/
# <summary>, а в блоке есть такие теги (дословная реплика, якорь, вывод проверки), CLI
# вырежет кусок блока или примет его за сводку. < → ‹ (U+2039): разбор CLI больше не
# задевает, дословность почти не портится. Без учёта регистра, с пробелами внутри скобок;
# сами <ccs-preserved>/</ccs-preserved> не трогает (имя тега не совпадает).
_CLI_TAG_RE = re.compile(r"<\s*/?\s*(?:analysis|summary)\s*>", re.IGNORECASE)


def _escape_cli_tags(text):
    """<analysis>/<summary> (открывающие/закрывающие, регистр/пробелы) → ‹…>: < → ‹ (U+2039)."""
    return _CLI_TAG_RE.sub(lambda m: "‹" + m.group(0)[1:], text)


# =============================================================================
# Шаг 8: повторное сжатие — перенос прошлого <ccs-preserved> в новый блок
# =============================================================================
#
# После первого сжатия постановка, ранние реплики, файлы и проверки живут только в записи
# сжатия — user-сообщении «This session is being continued from a previous conversation…»,
# где после «Summary:» стоит наш <ccs-preserved>…</ccs-preserved>. При втором сжатии
# human_texts эту запись пропускает целиком (_is_cli_summary), и прошлый блок терялся:
# «Постановка» становилась первой репликой после сжатия (часто «Продолжай»), а исходная
# постановка, старые поправки и список изменённых файлов исчезали. Поэтому прошлый блок
# разбираем по секциям и переносим его содержимое в новый.

def _prev_block_body(text):
    """Тело ПОСЛЕДНЕГО <ccs-preserved>…</ccs-preserved> в тексте; None — нет или сломан.

    «Последний» — последнее открывающее и последнее закрывающее после него: если блоков
    вдруг несколько, берём самый свежий. Сломанный блок (нет закрывающего тега, или он
    стоит до открывающего) → None: поведение как без блока.
    """
    start = text.rfind("<ccs-preserved>")
    if start < 0:
        return None
    end = text.rfind("</ccs-preserved>")
    if end < start + len("<ccs-preserved>"):
        return None
    return text[start + len("<ccs-preserved>"):end]


def _split_sections(body):
    """Тело блока → dict key -> текст секции (разделители — точные совпадения строк).

    Секции, которых в блоке нет, — без ключа. Известных заголовков нет вовсе (мусор
    вместо блока) — пустой dict.
    """
    lines = body.split("\n")
    marks = []
    for i, ln in enumerate(lines):
        key = _TITLE_KEY.get(ln)
        if key is not None:
            marks.append((i, key))
    out = {}
    for j, (i, key) in enumerate(marks):
        stop = marks[j + 1][0] if j + 1 < len(marks) else len(lines)
        text = "\n".join(lines[i + 1:stop]).strip()
        if text:
            out[key] = text
    return out


# Строка секции «Изменённые файлы»: `path` или `path (N правок)` или
# `path (N правок, перезапись)` — вывод _fmt_files. Суффикс закреплён на конце строки,
# поэтому путь в скобками не путается с суффиксом.
_OLD_FILE_LINE = re.compile(
    r"^(?P<path>.+?)\s*\((?:(?P<edits>\d+)\s+правок(?:,\s*перезапись)?|перезапись)\)$")


def _parse_old_files(text):
    """Секция «Изменённые файлы» прошлого блока: list {path, edits, write}."""
    out = []
    for ln in text.split("\n"):
        ln = ln.strip()
        if not ln:
            continue
        m = _OLD_FILE_LINE.match(ln)
        if m:
            path = m.group("path")
            edits = int(m.group("edits")) if m.group("edits") else 0
            write = "перезапись" in m.group(0)[len(path):]
        else:
            path, edits, write = ln, 0, False
        out.append({"path": path, "edits": edits, "write": write})
    return out


# Строка секции «Последние проверки»: `[kind] label — status` (вывод _fmt_checks);
# результат — отступные строки после заголовка.
_OLD_CHECK_LINE = re.compile(r"^\[(?P<kind>[^\]]+)\]\s*(?P<label>.*?)\s*—\s*(?P<status>\S+)$")


def _parse_old_checks(text):
    """Секция «Последние проверки» прошлого блока: list {kind, label, is_error, status,
    result}. Результат — отступные строки после заголовочной (до следующего заголовка)."""
    out = []
    lines = text.split("\n")
    i = 0
    while i < len(lines):
        m = _OLD_CHECK_LINE.match(lines[i])
        if not m:
            i += 1
            continue
        result = []
        j = i + 1
        while j < len(lines) and (lines[j].startswith("    ") or not lines[j].strip()):
            if lines[j].strip():
                result.append(lines[j].strip())
            j += 1
        out.append({"kind": m.group("kind"), "label": m.group("label"),
                    "is_error": m.group("status") == "ошибка",
                    "status": m.group("status"), "result": "\n".join(result)})
        i = j
    return out


def _prev_block(msgs):
    """Разобранный прошлый блок <ccs-preserved> из записи сжатия CLI; None — нет.

    Ищем в user-сообщениях, начинающихся с «This session is being continued…» (сводка
    прошлого сжатия): в последнем таком сообщении — последнее <ccs-preserved>…</ccs-preserved>.
    Секции переносим в те же формы, что и свежие:
      head     — постановка (текст);
      humans   — реплики (newest first, как в блоке; склеенный текст секции — одна запись:
                 границы реплик в собранном блоке неоднозначны, а для урезания «самые
                 старые» это самый старый кусок);
      files    — list {path, edits, write};
      checks   — list {kind, label, is_error, status, result};
      plan     — текст плана;
      anchors  — list[str].
    Мусор вместо блока (нет закрывающего тега, нет ни одного заголовка) → None: поведение
    как без блока, без исключения.
    """
    for m in reversed(msgs or []):
        if m.get("role") != "user":
            continue
        text = _text_blocks(m)
        if not _is_cli_summary(text):
            continue
        body = _prev_block_body(text)
        if body is None:
            continue  # этой записи блока нет (сжатие с выключенным флагом) — ищем раньше
        secs = _split_sections(body)
        if not secs:
            continue  # мусор вместо блока — ищем раньше
        return {
            "head": secs.get("head"),
            "humans": [secs["humans"]] if "humans" in secs else [],
            "files": _parse_old_files(secs["files"]) if "files" in secs else [],
            "checks": _parse_old_checks(secs["checks"]) if "checks" in secs else [],
            "plan": secs.get("plan"),
            "anchors": secs["anchors"].split("\n") if "anchors" in secs else [],
        }
    return None


def _merge_files(new_files, old_files):
    """Файлы: новые касания сверху, прошлые ниже; дубли пути схлопываются (число правок —
    сумма, перезапись — OR)."""
    merged = {}
    order = []
    for f in list(new_files) + list(old_files):
        if f["path"] not in merged:
            merged[f["path"]] = {"path": f["path"], "edits": 0, "write": False}
            order.append(f["path"])
        e = merged[f["path"]]
        e["edits"] += f["edits"]
        e["write"] = e["write"] or f["write"]
    return [merged[p] for p in order]


def _merge_checks(new_checks, old_checks):
    """Проверки: по каждому виду (kind) — самая свежая; новая перекрывает прошлую."""
    by_kind = {}
    for c in list(new_checks) + list(old_checks):
        by_kind.setdefault(c["kind"], c)
    return list(by_kind.values())


def _merge_anchors(new_anchors, old_anchors, cap=8000):
    """Якоря: новые + прошлые, дедуп (новое вхождение держим), обрезка по cap как в anchors()."""
    seen = set()
    out = []
    for a in list(new_anchors) + list(old_anchors):
        if a not in seen:
            seen.add(a)
            out.append(a)
    total = 0
    kept = []
    for a in out:
        if total + len(a) > cap and kept:
            break
        kept.append(a)
        total += len(a)
    return kept


def _cap_humans(humans, cap=HUMANS_CAP):
    """Потолок HUMANS_CAP на списке реплик (новые сверху): урезаются самые старые (хвост).

    Та же логика бюджета, что в _recent_humans_indexed: одна длинная реплика не отбрасывается,
    а берётся с меткой пропуска, если влезает в оставшийся бюджет, — и обрыв.
    """
    out = []
    budget = cap
    for t in humans:
        if not t:
            continue
        if len(t) <= budget:
            out.append(t)
            budget -= len(t)
        else:
            out.append(_truncate(t, budget))
            break
    return out


def build_preserved(doc):
    """Блок <ccs-preserved>: постановка + реплики + файлы + проверки + план + якоря.

    str|None (None, если ничего нет вовсе). Пустые разделы не выводим. Общий потолок
    PRESERVED_CAP: при превышении режем в порядке «якоря → проверки → файлы → старые
    реплики»; постановка и самая свежая реплика — никогда. Теги <analysis>/<summary> в
    тексте экранируются (< → ‹), чтобы разбор CLI по первому вхождению их не задел.

    Повторное сжатие (шаг 8): если в истории есть запись прошлого сжатия CLI с нашим
    блоком, его секции переносим: постановка — из прошлого блока (а не первая реплика
    после сжатия), реплики — новые сверху + прошлые (общий потолок HUMANS_CAP, урезаются
    самые старые), файлы — объединение (дубли схлопываются, правки суммируются),
    проверки — по каждому виду самая свежая, план — свежий иначе прошлый, якоря — новые +
    прошлые (дедуп, потолок). Сводка CLI без нашего блока — поведение как без переноса.
    """
    msgs = (doc or {}).get("messages") or []
    prev = _prev_block(msgs)
    # Постановка: из прошлого блока, если он её сохранил; иначе — как раньше (первая
    # реплика текущей истории).
    head_text = head(msgs)
    if prev and prev["head"]:
        head_text = prev["head"]
    # Реплики (newest first): свежие из текущей истории, затем прошлые из прошлого блока.
    # Постановка (head) уже в «Постановке» — из «Реплик» её исключаем, чтобы не дублировать,
    # но только когда она из текущей истории (первое сжатие: head = первая реплика). Когда
    # head перенесён из прошлого блока, свежих не исключаем: их первая (idx 0) — обычная
    # реплика после сжатия, а не постановка. Потолок HUMANS_CAP — на объединённом списке:
    # урезаются самые старые, то есть сначала прошлые.
    texts = list(reversed(human_texts(msgs)))  # newest first; head (idx 0) — в хвосте
    if prev and prev["head"]:
        new_humans = texts
    else:
        new_humans = texts[:-1] if texts else []
    humans = _cap_humans(new_humans + (prev["humans"] if prev else []))
    files = _merge_files(changed_files(msgs), prev["files"] if prev else [])
    checks = _merge_checks(last_checks(msgs), prev["checks"] if prev else [])
    plan = last_plan(msgs) or (prev["plan"] if prev else None)
    anchors_list = _merge_anchors(anchors(msgs), prev["anchors"] if prev else [])

    # Разделы: key -> (title, lines). Порядок в блоке фиксирован.
    secs = {}
    if head_text:
        secs["head"] = (SEC_HEAD, [head_text])
    if humans:
        secs["humans"] = (SEC_HUMANS, list(humans))
    if files:
        secs["files"] = (SEC_FILES, _fmt_files(files))
    if checks:
        secs["checks"] = (SEC_CHECKS, _fmt_checks(checks))
    if plan:
        secs["plan"] = (SEC_PLAN, plan.splitlines())
    if anchors_list:
        secs["anchors"] = (SEC_ANCHORS, list(anchors_list))

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
        # Экранируем <analysis>/<summary> в тексте блока (wrapper <ccs-preserved> имя не
        # совпадает — не трогается): иначе разбор CLI по первому вхождению их задел бы.
        return _escape_cli_tags("\n".join(parts))

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

    Текст блока <ccs-preserved> модели НЕ передаём — только инструкцию. Блок встаёт ПОСЛЕ
    сводки: прокси дописывает его в конец текстового блока модели с <summary> — только этот
    блок CLI 2.1.301 и берёт в запись сжатия (README, «Сохранение дословного»).
    """
    return (
        "A <ccs-preserved> block is appended verbatim after your summary. It holds the "
        "task statement, the human's replies, the changed files, the last checks, the plan "
        "and literal anchors. Do NOT restate the human's replies, the files, or the checks "
        "from it — refer to them. Spend your summary on the reasoning, the decisions, the "
        "rejected approaches, and the reasons. Keep your answer in the required "
        "<analysis>/<summary> format; the preserved block is inserted by the system, do "
        "not reproduce it."
    )
