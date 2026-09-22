#!/usr/bin/env python3
# Прокси между claude CLI и vLLM локальной модели: правит тело запроса ради prefix cache.
#
# Что делает: вырезает output_config (см. ниже), вырезает напоминания <total_tokens> и
# повторные снимки «# Environment», по желанию обрезает старые выводы инструментов
# (PRUNE_TOOL_RESULTS). Всё остальное проксируется как есть. Устройство и замеры — README.md.
#
# Зачем: CLI шлёт "output_config": {"effort": ...} на каждом ходу (без --effort — "high",
# который шаблон модели отвергает 400). vLLM по любому допустимому effort сам подставляет
# chat_template_kwargs.enable_thinking=true и перебивает серверный дефолт false — модель
# уходит в размышления (замер 2026-09-19: ~100 с перед каждым шагом агента). Без
# output_config срабатывает серверный дефолт, и ответ идёт сразу текстом.
#
# Всё остальное проксируется как есть, ответ — потоком (SSE). Невалидный JSON — fail-open.
import http.client, json, os, re, sys, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UP_HOST = os.environ.get("UPSTREAM_HOST", "127.0.0.1")
UP_PORT = int(os.environ.get("UPSTREAM_PORT", "18020"))
LISTEN = os.environ.get("LISTEN_HOST", "0.0.0.0")
PORT = int(os.environ.get("LISTEN_PORT", "18021"))
# Режим размышлений (эксперимент 2026-09-19):
#   off    — вырезаем output_config: vLLM не включает размышления (серверный дефолт false);
#   on     — пропускаем output_config: размышления включены, длина не ограничена;
#   budget — как on, плюс thinking_token_budget=THINKING_BUDGET (нужен патч Anthropic-роутера
#            vLLM, иначе поле молча игнорируется — см. vllm-patch/).
THINKING_MODE = os.environ.get("THINKING_MODE", "off")
THINKING_BUDGET = int(os.environ.get("THINKING_BUDGET", "512"))
STRIP_KEYS = ("output_config",) if THINKING_MODE == "off" else ()
HOP = {"connection", "keep-alive", "proxy-connection", "transfer-encoding", "te",
       "trailer", "upgrade", "content-length", "host"}

# Прунинг старых выводов инструментов (по умолчанию ВЫКЛЮЧЕН, включаем вручную на замер).
PRUNE_MODE = os.environ.get("PRUNE_TOOL_RESULTS", "off")
# Обе ручки границы — в ТОКЕНАХ, а не в штуках вызовов: 35 вызовов бывают и 20k токенов, и
# 200k, то есть в вызовах задача не измеряется. Живой прогон 2026-09-22 на этом и погорел —
# порог «70 вызовов» не наступил, пока чат не упёрся в автосжатие при 234k токенов.
# Сколько свежего вывода не трогаем никогда:
PRUNE_KEEP_TAIL_TOKENS = int(os.environ.get("PRUNE_KEEP_TAIL_TOKENS", "40000"))
# Ступень границы, она же порог первого срабатывания: пока обрезаемого объёма меньше ступени,
# прунинг молчит. Ступенчатость обязательна — непрерывная граница ползла бы на один вывод
# каждый ход и рвала кэш каждый шаг.
#
# 100k, а не 50k: живой прогон 2026-09-22 показал, что при ступени 50k граница на жирных
# выводах (один Read даёт 20k+ токенов) сдвигается раз в пару шагов — за прогон 4 сдвига,
# каждый ценой ПОЛНОГО пересчёта (замерено: 158k и 98k токенов мимо кэша подряд), hit rate
# просел до 63 %. При 100k на той же истории сдвигов 2, а срабатывает прунинг почти так же
# рано (164k против 156k мерки) — обрезаемый объём растёт быстро, и вторая ступень набирается
# почти сразу за первой.
PRUNE_STEP_TOKENS = int(os.environ.get("PRUNE_STEP_TOKENS", "100000"))
PRUNE_MIN_CHARS = int(os.environ.get("PRUNE_MIN_CHARS", "2000"))
PRUNE_MIN_TOKENS = int(os.environ.get("PRUNE_MIN_TOKENS", "20000"))
# Явное условие «чат уже большой». Без него порог включения задавался АРИФМЕТИКОЙ кванта
# (граница > 0 требует PROTECT_LAST + QUANTUM вызовов, то есть 70), и правка кванта ради
# частоты скачков молча двигала бы и его — две разные вещи на одной ручке. Значение 150k
# взято из замеров: чат на 133k живёт без автосжатия, и резать там нечего (скачок границы
# пришлось бы оплатить впустую), а беда начиналась к 318k при окне модели 262k.
PRUNE_MIN_CONTEXT_TOKENS = int(os.environ.get("PRUNE_MIN_CONTEXT_TOKENS", "150000"))
# Разбор каждого запроса в журнал. ВЫКЛЮЧЕН по умолчанию: в лог попадает начало реплики,
# то есть кусок чужого чата. Включать точечно, на время разбирательства.
PRUNE_DEBUG = os.environ.get("PRUNE_DEBUG", "off")

stats = {"requests": 0, "stripped": 0, "pruned_blocks": 0, "pruned_chars": 0}

# CLI вставляет в историю system-сообщение "<total_tokens>N tokens left</total_tokens>" на
# каждом шаге, и N каждый раз новое. Шаблон Qwen требует system только первым, поэтому
# vLLM (merge_inline_system) переносит ВСЕ такие сообщения в верхний системный блок — он
# стоит сразу за инструментами, до истории. Новое напоминание меняет текст на ~14-тысячном
# токене, и prefix cache обрывается: вся история пересчитывается на каждом шаге агента
# (замер 2026-09-19: ~20 с на шаг при 47k контекста). Бюджет в 15M токенов локальной модели
# ничего не сообщает — вырезаем только сообщения, целиком состоящие из этого напоминания.
_BUDGET_RE = re.compile(r"^\s*<total_tokens>\d+ tokens left</total_tokens>\s*$")


# Тот же перенос в системный блок у снимка окружения: после каждого `cd` CLI шлёт новое
# system-сообщение "# Environment … (was …)", и история снова выпадает из кэша (замер
# 2026-09-19: ~25 с на каждый переход каталога). Первый снимок стабилен и остаётся, повторные
# вырезаем — о смене каталога модель знает сама: она его и меняла.
def _is_environment(m):
    return _system_text(m).lstrip().startswith("# Environment")


# Повторный снимок вырезаем, ТОЛЬКО если он целиком состоит из заголовка «# Environment update»,
# пунктов-строк и необязательного счётчика <total_tokens>: всё, что CLI однажды допишет туда
# сверх этого, остаётся — лучше потерять кэш, чем информацию.
_ENV_UPDATE_RE = re.compile(
    r"^\s*# Environment update\s*\n(?:[ \t]*- [^\n]*\n?)+\s*"
    r"(?:<total_tokens>\d+ tokens left</total_tokens>\s*)?$")


def _is_env_update_only(m):
    return bool(_ENV_UPDATE_RE.match(_system_text(m)))


def _system_text(m):
    if not isinstance(m, dict) or m.get("role") != "system":
        return ""
    c = m.get("content")
    if isinstance(c, str):
        return c
    if isinstance(c, list) and all(isinstance(b, dict) and b.get("type") == "text" for b in c):
        return "".join(b.get("text") or "" for b in c)
    return ""


def _is_budget_reminder(m):
    return bool(_BUDGET_RE.match(_system_text(m)))


# Прунинг старых выводов инструментов. Основную массу контекста агентного чата дают выводы
# Bash/Grep/Read по 5–30 КБ: модель помнит, ЧТО она запускала (блоки tool_use остаются на
# месте), а результат при надобности перечитает. Заменяем только поле content блока
# tool_result — сам блок и парный tool_use живут дальше, иначе поедет связность истории.
#
# ГЛАВНОЕ ТРЕБОВАНИЕ — стабильность префикса. Прямолинейное «защищаем последние N токенов»
# двигает границу на КАЖДОМ ходу: очередное сообщение переезжает из живых в обрезанные,
# середина истории меняется, prefix cache обрывается — ровно та беда, от которой заведён этот
# прокси. Поэтому границу считаем от НАЧАЛА истории (порядковый номер вывода никогда не
# меняется при дописывании хвоста) и квантуем её вниз по ступени в токенах: префикс
# перестраивается раз в PRUNE_STEP_TOKENS, а не каждый шаг. Состояние по сессиям тут держать
# негде — у запроса нет её идентификатора, — и оно не нужно: решение зависит только от тела.
PRUNE_PLACEHOLDER = "[Old tool result content cleared]"


def _prunable_size(content):
    """Длина вывода в символах или None, если форму не разбираем (картинки и прочее — не трогаем)."""
    if isinstance(content, str):
        return len(content)
    if isinstance(content, list) and all(
            isinstance(b, dict) and b.get("type") == "text" and isinstance(b.get("text"), str)
            for b in content):
        return sum(len(b["text"]) for b in content)
    return None


def _text_len(value):
    """Длина текстового поля; чужое тело вправе прислать туда что угодно — не наша забота."""
    return len(value) if isinstance(value, str) else 0


def _context_chars(msgs, extra_chars=0):
    """Грубый размер контекста в символах — за один проход, без сериализации всего тела.

    extra_chars — системная часть запроса (промпт и определения инструментов). Без неё мерка
    врёт: на реальном чате она дала 71k против 133k настоящих токенов по usage, потому что
    у агента системный блок и тулсет весят как половина истории.
    """
    total = extra_chars
    for m in msgs:
        if not isinstance(m, dict):
            continue
        content = m.get("content")
        if isinstance(content, str):
            total += len(content)
            continue
        if not isinstance(content, list):
            continue
        for b in content:
            if not isinstance(b, dict):
                continue
            kind = b.get("type")
            if kind == "text":
                total += _text_len(b.get("text"))
            elif kind == "thinking":
                total += _text_len(b.get("thinking"))
            elif kind == "tool_result":
                total += _prunable_size(b.get("content")) or 0
            elif kind == "tool_use":
                total += len(json.dumps(b.get("input") or {}, ensure_ascii=False))
    return total


def _system_chars(doc):
    """Размер системной части тела: системный промпт плюс определения инструментов."""
    total = 0
    system = doc.get("system")
    if isinstance(system, str):
        total += len(system)
    elif isinstance(system, list):
        total += sum(_text_len(b.get("text")) for b in system if isinstance(b, dict))
    tools = doc.get("tools")
    if isinstance(tools, list):
        total += len(json.dumps(tools, ensure_ascii=False))
    return total


def _placeholder_like(content):
    """Плейсхолдер той же формы, что и исходный content, — чтобы не менять форму блока."""
    return PRUNE_PLACEHOLDER if isinstance(content, str) else [
        {"type": "text", "text": PRUNE_PLACEHOLDER}]


def prune_tool_results(msgs, keep_tail_tokens=None, step_tokens=None, min_chars=None,
                       min_tokens=None, min_context_tokens=None, extra_chars=0):
    """Возвращает (сообщения, сколько символов освободили, сколько блоков обрезали). Вход не мутирует."""
    keep_tail_tokens = PRUNE_KEEP_TAIL_TOKENS if keep_tail_tokens is None else keep_tail_tokens
    step_tokens = max(1, PRUNE_STEP_TOKENS if step_tokens is None else step_tokens)
    min_chars = PRUNE_MIN_CHARS if min_chars is None else min_chars
    min_tokens = PRUNE_MIN_TOKENS if min_tokens is None else min_tokens
    min_context_tokens = PRUNE_MIN_CONTEXT_TOKENS if min_context_tokens is None else min_context_tokens

    # Маленький чат не трогаем вовсе: автосжатие ему не грозит, а скачок границы он оплатит.
    #
    # История append-only и только растёт, а вот системная часть НЕ обязана: секции промпта у
    # этого продукта пересобираются каждый ход. Похудей системный блок — оценка ушла бы обратно
    # под порог, прунинг выключился бы, и середина истории вернулась бы в полный вид: мигание
    # на каждом ходу, то есть ровно потеря кэша. Поэтому огрубляем вниз НЕСТАБИЛЬНОЕ слагаемое,
    # а не сумму (огрубление суммы до кратного порогу — тождество, оно ничего не даёт):
    # колебания системной части в пределах сотни тысяч символов решение не меняют. Огрубление
    # вниз занижает оценку, то есть включает прунинг позже — безопасная сторона ошибки.
    stable_extra = max(0, extra_chars) // 100_000 * 100_000
    if _context_chars(msgs, stable_extra) // 4 < min_context_tokens:
        return msgs, 0, 0

    found = []  # (номер сообщения, номер блока, размер) в порядке появления
    for mi, m in enumerate(msgs):
        if not isinstance(m, dict) or m.get("role") != "user":
            continue
        content = m.get("content")
        if not isinstance(content, list):
            continue
        for bi, b in enumerate(content):
            if isinstance(b, dict) and b.get("type") == "tool_result":
                found.append((mi, bi, _prunable_size(b.get("content"))))

    # Граница в ТОКЕНАХ, а не в штуках вызовов. Мерить ступени в вызовах — мерить задачу не в
    # той единице, в которой она стоит: 35 вызовов бывают и 20k токенов, и 200k. Живой прогон
    # 2026-09-22 на этом и погорел — порог «70 вызовов» не наступил, пока чат не упёрся в
    # автосжатие. Здесь обе ручки в токенах, скрытого второго порога больше нет.
    #
    # Ступенчатость остаётся обязательной: непрерывная граница ползла бы на один вывод каждый
    # ход, меняя середину истории, — то есть рвала бы кэш каждый шаг. Стабильность даёт то,
    # что target квантован, а размеры выводов ДО него уже не меняются: пока target тот же,
    # граница стоит на месте бит в бит.
    keep_chars = keep_tail_tokens * 4
    step_chars = step_tokens * 4  # ноль отсечён при разборе аргументов

    tail, protect_idx = 0, len(found)  # защищаем хвост по объёму: свежее модель ещё читает
    while protect_idx > 0 and tail < keep_chars:
        tail += found[protect_idx - 1][2] or 0
        protect_idx -= 1

    # Считаем ТОЛЬКО тот объём, который реально обрежем: мелкие выводы мы не трогаем (экономии
    # нет, а кэш рвём), поэтому они не должны и двигать границу. Иначе чат с мелкими выводами
    # набирает ступень штуками, а освобождает копейки — на истории 035c3dd7 (медиана вывода
    # 1 КБ) такая схема не срабатывала вовсе при 217k контекста.
    def весомый(size):
        return size is not None and size >= min_chars

    prunable = sum(size for _, _, size in found[:protect_idx] if весомый(size))
    target = prunable // step_chars * step_chars  # квантование вниз: редкие скачки
    if target <= 0:
        return msgs, 0, 0

    acc, boundary = 0, 0
    for i in range(protect_idx):
        size = found[i][2]
        if весомый(size):
            if acc + size > target:
                break
            acc += size
        boundary = i + 1
    if boundary <= 0:
        return msgs, 0, 0

    # Мелкие выводы пропускаем: экономить на них нечего, а кэш ломаем. Плейсхолдер короче
    # порога, поэтому повторный прогон уже обрезанной истории ничего не меняет.
    victims = [(mi, bi) for mi, bi, size in found[:boundary]
               if size is not None and size >= min_chars]
    freed = sum(size - len(PRUNE_PLACEHOLDER) for _, _, size in found[:boundary]
                if size is not None and size >= min_chars)
    # Порог выгоды (грубая оценка 4 символа на токен): ради мелочи префикс не рвём. Оценка
    # монотонна — граница только растёт, — поэтому порог срабатывает один раз за чат.
    if freed // 4 < min_tokens:
        return msgs, 0, 0

    by_msg = {}
    for mi, bi in victims:
        by_msg.setdefault(mi, set()).add(bi)
    out = list(msgs)
    for mi, blocks in by_msg.items():
        m = dict(out[mi])
        content = list(m["content"])
        for bi in blocks:
            b = dict(content[bi])
            b["content"] = _placeholder_like(b.get("content"))
            content[bi] = b
        m["content"] = content
        out[mi] = m
    return out, freed, len(victims)


def _debug_dump(doc, msgs, body_len):
    """Разбор запроса в журнал: зачем прунинг решил так, а не иначе.

    Заведён под вопрос «почему запрос автосжатия проходит мимо прунинга» (замер 2026-09-22:
    сжатие 253k→37k за 330 с, счётчик обрезки при этом не двинулся). По умолчанию ВЫКЛЮЧЕН:
    сюда попадает начало реплики, то есть кусок чужого чата, и в журнале ему не место.
    """
    try:
        выводы = [_prunable_size(b.get("content"))
                  for m in msgs if isinstance(m, dict) and m.get("role") == "user"
                  and isinstance(m.get("content"), list)
                  for b in m["content"] if isinstance(b, dict) and b.get("type") == "tool_result"]
        крупные = [s for s in выводы if s is not None and s >= PRUNE_MIN_CHARS]
        системная = _system_chars(doc)
        контекст = _context_chars(msgs, системная) // 4
        _, freed, blocks = prune_tool_results(msgs, extra_chars=системная)
        # хвост последнего сообщения: по нему и видно, ход это или запрос сжатия
        хвост = ""
        for m in reversed(msgs):
            c = m.get("content") if isinstance(m, dict) else None
            if isinstance(c, str):
                хвост = c
            elif isinstance(c, list):
                хвост = " ".join(b.get("text") or "" for b in c
                                 if isinstance(b, dict) and b.get("type") == "text")
            if хвост.strip():
                break
        print(f"[отладка] тело {body_len // 1024} КБ | сообщений {len(msgs)} | "
              f"выводов {len(выводы)} (крупных {len(крупные)} на {sum(крупные) // 4000}k ток) | "
              f"системная часть {системная // 4000}k ток | контекст {контекст // 1000}k ток | "
              f"прунинг: {blocks} блоков, {freed // 4000}k ток | "
              f"хвост: {хвост.strip()[:160]!r}", file=sys.stderr, flush=True)
    except Exception as e:  # отладка не смеет ломать проксирование
        print(f"[отладка] не удалась: {e!r}", file=sys.stderr, flush=True)


lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _read_body(self):
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            parts = []
            while True:
                size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()
                    break
                parts.append(self.rfile.read(size))
                self.rfile.readline()
            return b"".join(parts)
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def _stats(self):
        with lock:
            msg = json.dumps(dict(stats, prune=PRUNE_MODE, thinking=THINKING_MODE)).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(msg)))
        self.end_headers()
        self.wfile.write(msg)

    def _proxy(self):
        # Счётчики наружу: замер «сколько на самом деле обрезали» снимать больше неоткуда.
        # Путь служебный, у Anthropic-роутера vLLM такого нет — с upstream не пересекается.
        if self.command == "GET" and self.path.split("?")[0] == "/__proxy/stats":
            self._read_body()
            return self._stats()
        body = self._read_body()
        original = body  # на откат: любая осечка разбора обязана вернуть тело нетронутым
        stripped = False
        if body and self.command == "POST":
            try:
                doc = json.loads(body)
                if isinstance(doc, dict):
                    for k in STRIP_KEYS:
                        if k in doc:
                            del doc[k]
                            stripped = True
                    if THINKING_MODE == "budget" and "thinking_token_budget" not in doc:
                        doc["thinking_token_budget"] = THINKING_BUDGET
                        stripped = True
                    msgs = doc.get("messages")
                    if isinstance(msgs, list):
                        kept, seen_env = [], False
                        for m in msgs:
                            if _is_budget_reminder(m):
                                continue
                            if _is_environment(m):
                                if seen_env and _is_env_update_only(m):
                                    continue
                                seen_env = True
                            kept.append(m)
                        if len(kept) != len(msgs):
                            doc["messages"] = kept
                            stripped = True
                        if PRUNE_DEBUG == "on":
                            _debug_dump(doc, kept, len(body))
                        if PRUNE_MODE == "on":
                            pruned_msgs, freed, blocks = prune_tool_results(
                                kept, extra_chars=_system_chars(doc))
                            if blocks:
                                doc["messages"] = pruned_msgs
                                stripped = True
                                with lock:
                                    stats["pruned_blocks"] += blocks
                                    stats["pruned_chars"] += freed
                    if stripped:
                        body = json.dumps(doc, ensure_ascii=False).encode("utf-8")
            except Exception as e:
                # fail-open: не наш формат ИЛИ неожиданная структура внутри — отдаём тело как
                # есть. Ловим всё намеренно: исключение отсюда уходит в handle_one_request,
                # клиент остаётся без ответа и висит до своего таймаута — то есть опечатка в
                # разборе тела ломает ход пользователя молча. Тело чужое, доверять его форме
                # нельзя: `{"type":"text","text":5}` достаточно, чтобы получить TypeError.
                body, stripped = original, False
                print(f"тело пропущено без правок: {e!r}", file=sys.stderr, flush=True)
        with lock:
            stats["requests"] += 1
            stats["stripped"] += stripped

        headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
        if body:
            headers["Content-Length"] = str(len(body))
        conn = http.client.HTTPConnection(UP_HOST, UP_PORT, timeout=None)
        try:
            conn.request(self.command, self.path, body=body or None, headers=headers)
            resp = conn.getresponse()
        except OSError as e:
            print(f"upstream-ошибка {self.command} {self.path}: {e!r}", file=sys.stderr, flush=True)
            msg = json.dumps({"type": "error", "error": {"type": "proxy_error",
                              "message": f"upstream {UP_HOST}:{UP_PORT} недоступен: {e}"}}).encode()
            self.send_response(502)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(msg)))
            self.end_headers()
            self.wfile.write(msg)
            return

        self.send_response(resp.status, resp.reason)
        for k, v in resp.getheaders():
            if k.lower() not in HOP:
                self.send_header(k, v)
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        try:
            while True:
                chunk = resp.read1(65536)
                if not chunk:
                    break
                self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass  # клиент ушёл (прерывание хода) — upstream закроется вместе с conn
        finally:
            conn.close()

    def handle_one_request(self):
        try:
            super().handle_one_request()
        except (ConnectionResetError, BrokenPipeError):
            # клиент закрыл keep-alive соединение, пока мы ждали следующий запрос — штатно
            self.close_connection = True
        except Exception:
            import traceback
            traceback.print_exc(file=sys.stderr)
            sys.stderr.flush()
            # Клиенту ответа уже не будет — рвём соединение, чтобы он увидел обрыв сразу,
            # а не висел на keep-alive до собственного таймаута.
            self.close_connection = True

    do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = do_HEAD = do_OPTIONS = _proxy


if __name__ == "__main__":
    srv = ThreadingHTTPServer((LISTEN, PORT), Handler)
    srv.daemon_threads = True
    print(f"thinking-strip-proxy: {LISTEN}:{PORT} -> {UP_HOST}:{UP_PORT}, режим размышлений {THINKING_MODE}"
          + (f" (бюджет {THINKING_BUDGET})" if THINKING_MODE == "budget" else "")
          + f", вырезаю {STRIP_KEYS}, напоминания total_tokens и повторные # Environment"
          + (f", прунинг выводов вкл (хвост {PRUNE_KEEP_TAIL_TOKENS // 1000}k ток, ступень "
             f"{PRUNE_STEP_TOKENS // 1000}k ток, порог контекста {PRUNE_MIN_CONTEXT_TOKENS // 1000}k)"
             if PRUNE_MODE == "on" else ", прунинг выводов выкл"), flush=True)
    srv.serve_forever()
