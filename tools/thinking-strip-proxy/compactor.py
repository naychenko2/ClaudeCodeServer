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

# Заголовки хвоста хода: их наличие в тексте старой реплики означает, что перед ними
# (до последнего "---") — обвязка, а реплика человека — после.
_TAIL_HEADERS = ("## Память команды проекта", "Возможно релевантные заметки")

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


def recent_humans(messages, cap=HUMANS_CAP):
    """Реплики человека от новых к старым, пока влезает в cap (кодовые точки).

    Одна длинная реплика — тоже с меткой пропуска, а не отбрасывается: берём её целиком
    (с обрезкой), если она влезает в оставшийся бюджет, и останавливаемся.
    """
    texts = human_texts(messages)
    out = []
    budget = cap
    for t in reversed(texts):
        if not t:
            continue
        if len(t) <= budget:
            out.append(t)
            budget -= len(t)
        else:
            out.append(_truncate(t, budget))
            break
    return out
