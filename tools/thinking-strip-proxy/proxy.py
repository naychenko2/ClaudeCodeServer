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
import http.client, json, os, re, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

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

# --- Мерка контекста: сколько символов приходится на токен ---------------------------------
#
# Делитель был один на всё («÷ 4») и врал в две стороны сразу, гася одну ошибку другой.
# Замер /tokenize стенда 2026-09-22 на реальных телах запросов (таблица — в README):
# история 3.18…3.36 симв/ток (русский текст, код, выводы инструментов), системная часть
# 3.60…3.83 (английский JSON определений инструментов плюс системный промпт). Поэтому
# слагаемых два, и у каждого свой коэффициент: «÷ 4» занижал историю на 17–20 %, а
# системную часть — на 4–10 %, и композит выходил случайным.
CHARS_PER_TOKEN_HISTORY = 3.3
CHARS_PER_TOKEN_SYSTEM = 3.7
# base64-подписи thinking-блоков: 1.36 симв/ток (замерено на истории 035c3dd7 — 689k символов
# подписей = 506k токенов). Обычно их доля ничтожна (0.45 % тела у локальной модели), но чат,
# переехавший к нам с чужой модели, приносит подписи целиком, и без этого слагаемого оценка
# промаха давала бы кратную ошибку.
CHARS_PER_TOKEN_SIGNATURE = 1.4
# Консервативные (меньшие) коэффициенты — для ответа на вопрос «влезет ли запрос в
# max_model_len». Там занижение = мгновенный HTTP 400 от vLLM и упавший ход, поэтому берём
# не среднее, а наблюдённый минимум с запасом: история 2.85 (та же 035c3dd7 по мерочным
# кускам) → 2.6, системная часть 3.60 → 3.4. С этими значениями верхняя оценка вышла выше
# факта на ВСЕХ пяти снятых телах, включая самое плотное.
CONSERVATIVE_CHARS_PER_TOKEN_HISTORY = 2.6
CONSERVATIVE_CHARS_PER_TOKEN_SYSTEM = 3.4


def estimate_tokens(history_chars, system_chars=0, signature_chars=0, conservative=False):
    """Оценка размера запроса в токенах по символам его частей.

    По умолчанию — СРЕДНЯЯ оценка: ею меряются пороги включения прунинга, где занижение
    безопасно (прунинг просто включится позже). `conservative=True` даёт верхнюю оценку для
    проверки «влезет ли запрос в окно модели»: там ошибка в другую сторону стоит хода.
    """
    per_history = (CONSERVATIVE_CHARS_PER_TOKEN_HISTORY if conservative
                   else CHARS_PER_TOKEN_HISTORY)
    per_system = (CONSERVATIVE_CHARS_PER_TOKEN_SYSTEM if conservative
                  else CHARS_PER_TOKEN_SYSTEM)
    return int(max(0, history_chars) / per_history + max(0, system_chars) / per_system
               + max(0, signature_chars) / CHARS_PER_TOKEN_SIGNATURE)


# --- Ручки прунинга ------------------------------------------------------------------------
#
# Обе ручки границы — в ТОКЕНАХ, а не в штуках вызовов: 35 вызовов бывают и 20k токенов, и
# 200k, то есть в вызовах задача не измеряется. Живой прогон 2026-09-22 на этом и погорел —
# порог «70 вызовов» не наступил, пока чат не упёрся в автосжатие при 234k токенов.
# Сколько свежего вывода не трогаем никогда:
#
# 48000, а не прежние 40000: хвост и ступень разворачиваются в СИМВОЛЫ, и после смены
# делителя 4 → 3.3 прежние значения ужали бы защищённый хвост 160k → 132k символов, а ступень
# 400k → 330k. Геометрия при этом поехала бы молча, а тюнинг ступени снимался живым прогоном
# (p90 40 с, hit rate 80.3 %) ровно на старой геометрии и в новой не наследуется. Поэтому
# значения ручек пересчитаны так, чтобы в символах остаться на месте: 48000 * 3.3 = 158.4k
# против прежних 160k (−1 %). Переснимать 40-минутный живой прогон ради смены единицы
# измерения дороже, чем сохранить геометрию.
PRUNE_KEEP_TAIL_TOKENS = int(os.environ.get("PRUNE_KEEP_TAIL_TOKENS", "48000"))
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
#
# 121000, а не прежние 100000, по той же причине, что и хвост: 121000 * 3.3 = 399.3k символов
# против прежних 400k (−0.2 %), то есть снятая живьём геометрия ступени сохранена.
PRUNE_STEP_TOKENS = int(os.environ.get("PRUNE_STEP_TOKENS", "121000"))
# Первая ступень — меньше обычной. Пока порог первого срабатывания равнялся ступени (121k),
# прунинг включался лишь при контексте ~185–215k (системная часть + хвост 48k + 121k
# обрезаемого), а автосжатие CLI с незаданной ручкой наступает на 232k: запас 17–45k, один
# Read большого файла (25k+) его перепрыгивает. Живой замер меры 4 (2026-09-23) так и кончился:
# к сжатию на 189k вне хвоста лежало 5 блоков на 82k — до ступени не хватило. С первой
# ступенью 60k граница впервые встаёт уже при ~60k обрезаемого (контекст ~130–150k, а с
# порогом «чат уже большой» — на 175k), дальше квант прежний: 121k, 242k… Цена — один
# лишний сдвиг границы за чат (~80 с), выгода — прунинг гарантированно опережает сжатие.
# 0 — выключить: первая ступень равна обычной. Значение выше ступени обрезается до неё.
PRUNE_FIRST_STEP_TOKENS = int(os.environ.get("PRUNE_FIRST_STEP_TOKENS", "60000"))
PRUNE_MIN_CHARS = int(os.environ.get("PRUNE_MIN_CHARS", "2000"))
# Порог выгоды, тоже пересчитан к новому коэффициенту: 24000 * 3.3 = 79.2k символов против
# прежних 20000 * 4 = 80k. Смысл прежний — ради мелочи префикс не рвём.
PRUNE_MIN_TOKENS = int(os.environ.get("PRUNE_MIN_TOKENS", "24000"))
# Явное условие «чат уже большой». Без него порог включения задавался АРИФМЕТИКОЙ кванта
# (граница > 0 требует PROTECT_LAST + QUANTUM вызовов, то есть 70), и правка кванта ради
# частоты скачков молча двигала бы и его — две разные вещи на одной ручке. Значение взято из
# замеров: чат на 133k живёт без автосжатия, и резать там нечего (скачок границы пришлось бы
# оплатить впустую), а беда начиналась к 318k при окне модели 262k.
#
# История порога: 150k при делителе 4 → 175k при честной мерке (то же место в символах,
# 600k) → снова 150k, но уже по честной мерке, то есть РАНЬШЕ. Причина — живой замер меры 4
# (2026-09-23): порог сравнивается с оценкой, где системная часть огрублена вниз до сотен
# тысяч символов (см. ниже), и у прогона без MCP она обнулилась — прокси видел 167k, когда CLI
# по usage видел 176k, и автосжатие (порог CLI 232k по умолчанию) пришло раньше прунинга.
# Недосчёт из-за огрубления — до 27k токенов при боевом теле с MCP, и запас 232k − 175k не
# покрывал его вместе с одним большим Read. 150k даёт запас 80k; чат на 133k, где резать
# нечего, по-прежнему ниже порога. Первая ступень 60k (PRUNE_FIRST_STEP_TOKENS) к этому
# моменту обычно уже набрана — хвост 48k плюс 60k обрезаемого укладываются в 150k.
PRUNE_MIN_CONTEXT_TOKENS = int(os.environ.get("PRUNE_MIN_CONTEXT_TOKENS", "150000"))
# Резать ли тела Write/Edit в аргументах вызовов — до 36 % контекста на задачах правки кода.
# Отдельный флаг, потому что 22.09 его выключали по ложной тревоге: в живом прогоне модель
# записала файл строкой «[Old tool input content cleared]», и это приняли за подражание
# плейсхолдеру. Повторы с честным лимитом вывода (8192, как у CLI; по три на вариант)
# показали, что метка в истории на письмо не влияет: 14985 / 22193 / 13465 символов против
# контроля 11309 / 12091 / 14802. Те 32 байта — единичный сбой генерации. Подражание
# провоцирует только ИНФОРМАТИВНАЯ метка (с путём и размером — скопирована целиком), простая
# безопасна. Дефолт off сохранён как предохранитель на смену модели; в бою включается через
# mode.conf. Разбор с таблицами — README.
PRUNE_INPUTS = os.environ.get("PRUNE_INPUTS", "off")
# Резать ли старые размышления. Отдельный флаг: у thinking-блоков есть подпись (vLLM блок без
# неё принимает — проверено). Серия 23.09 (два чата, три варианта × три повтора, лимит 16384):
# обрезка безвредна — итоговые отчёты с обрезкой несут те же факты, что контроль. Важно, ГДЕ
# она что-то даёт: шаблон Qwen3 (preserve_thinking=false) и так отбрасывает размышления всех
# прошлых ходов пользователя, в промпт попадают только блоки текущего хода — их и снимаем.
# Дефолт off сохранён как предохранитель на смену модели; в бою включается через mode.conf.
PRUNE_THINKING = os.environ.get("PRUNE_THINKING", "off")
# Разбор каждого запроса в журнал. ВЫКЛЮЧЕН по умолчанию: в лог попадает начало реплики,
# то есть кусок чужого чата. Включать точечно, на время разбирательства.
PRUNE_DEBUG = os.environ.get("PRUNE_DEBUG", "off")

# --- Маршрут запроса автосжатия на облачную модель -----------------------------------------
#
# Автосжатие у локальной модели стоит 330–430 с стоп-мира (замеры 2026-09-22: 253k → 37k за
# 330 с). Раскладка: prefill всей истории 64 %, decode 11 %, повторные проходы из-за лимита
# вывода 25 %. У облачной модели с окном 1M prefill 250k — секунды, повторных проходов нет.
# Второй выигрыш — качество: сюда тело уходит БЕЗ прунинга, и сводка пишется по оригиналам
# выводов, а не по плейсхолдерам.
#
# Это вторая точка маршрутизации мимо LlmProviderRegistry и осознанное исключение для стенда,
# а НЕ паттерн: логики выбора провайдера у прокси нет и заводить её нельзя. Здесь одна явная
# ручка на один распознаваемый тип запроса — на другие типы не расширять.
COMPACT_UPSTREAM = os.environ.get("COMPACT_UPSTREAM", "")  # пусто = выключено
COMPACT_MODEL = os.environ.get("COMPACT_MODEL", "MiniMax-M3")
# Ключ берём из конфига бэкенда, а не из своего окружения: дублировать секрет в юнит прокси
# значит завести вторую копию, которая разойдётся с первой.
COMPACT_KEY_FILE = os.environ.get("COMPACT_KEY_FILE", "/opt/ccs/app/appsettings.Local.json")
COMPACT_KEY_PATH = os.environ.get("COMPACT_KEY_PATH", "LlmProviders.minimax.ApiKey")
COMPACT_ANTHROPIC_VERSION = os.environ.get("COMPACT_ANTHROPIC_VERSION", "2023-06-01")
COMPACT_TIMEOUT = float(os.environ.get("COMPACT_TIMEOUT", "300"))
# Сигнатура запроса сжатия, пойманная отладочным логом 2026-09-22: хвост последнего
# user-сообщения начинается ровно с неё. Вынесена в ручку, потому что это чужой текст —
# формулировка CLI может смениться с обновлением, и тогда маршрут просто перестанет
# срабатывать (сжатие уйдёт в локаль), а не сломается.
COMPACT_SIGNATURE = os.environ.get(
    "COMPACT_SIGNATURE", "CRITICAL: Respond with TEXT ONLY. Do NOT call any tools")
# Второй признак распознавания — «чат уже размером с окно». Отдельная ручка, а не
# PRUNE_MIN_CONTEXT_TOKENS напрямую: порог автосжатия CLI двигается ручкой
# CLAUDE_CODE_AUTO_COMPACT_WINDOW, и опустив его ниже порога прунинга, мы бы молча выключили
# распознавание. По умолчанию — то же значение.
COMPACT_MIN_CONTEXT_TOKENS = int(os.environ.get("COMPACT_MIN_CONTEXT_TOKENS",
                                                str(PRUNE_MIN_CONTEXT_TOKENS)))

# --- Карточка в ленте чата: событие о сдвиге границы -----------------------------------------
#
# Сдвиг границы стоит полного пересчёта префикса (замеры 2026-09-22: 158k и 98k токенов мимо
# кэша, 80–90 с). Для человека это неотличимо от зависшего чата, поэтому о нём рассказывает
# карточка в ленте — как о штатном автосжатии CLI. Событие уходит ТОЛЬКО на сдвиге: между
# сдвигами обрезка бесплатна (префикс тот же), и карточка на каждом ходу была бы шумом.
#
# Пусто = выключено: прокси остаётся прежним, пока адрес не задан в юните. Дефолт именно
# такой, потому что служба запускается из рабочего дерева репозитория — код на ветке
# разработки не должен менять поведение живого стенда до отмашки.
BACKEND_EVENTS_URL = os.environ.get("BACKEND_EVENTS_URL", "")
# Секрет ручки: тот же конфиг бэкенда, что и ключ облака, — вторую копию секрета в юните не
# заводим по той же причине (разойдётся с первой).
EVENT_SECRET_FILE = os.environ.get("EVENT_SECRET_FILE", COMPACT_KEY_FILE)
EVENT_SECRET_PATH = os.environ.get("EVENT_SECRET_PATH", "LlmProxy.EventSecret")
# Короткий таймаут: событие декоративное, ждать его дольше, чем идёт ответ модели, незачем.
EVENT_TIMEOUT = float(os.environ.get("EVENT_TIMEOUT", "5"))
# Заголовок с id сессии бэкенд доставляет через ANTHROPIC_CUSTOM_HEADERS процесса CLI —
# проверено на стенде 2026-09-23: CLI дописывает его к каждому запросу к API.
EVENT_SESSION_HEADER = os.environ.get("EVENT_SESSION_HEADER", "X-CCS-Session")

stats = {"requests": 0, "stripped": 0, "pruned_blocks": 0, "pruned_chars": 0,
         "compact_routed": 0, "compact_fallback": 0, "compact_signature_small": 0,
         "events_sent": 0, "events_failed": 0}

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
PRUNE_INPUT_PLACEHOLDER = "[Old tool input content cleared]"
PRUNE_THINKING_PLACEHOLDER = "[Old thinking cleared]"


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


def _context_chars(msgs):
    """Размер ИСТОРИИ в символах — за один проход, без сериализации всего тела.

    Только история: системная часть (промпт и определения инструментов) считается отдельно
    `_system_chars`, потому что токенизируется иначе — см. коэффициенты выше. Но и выбрасывать
    её из оценки нельзя: без неё мерка давала 71k против 133k настоящих токенов по usage, у
    агента системный блок и тулсет весят как половина истории.
    """
    total = 0
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


def _signature_chars(msgs):
    """Длина base64-подписей thinking-блоков: они едут в теле, но токенизируются втрое плотнее."""
    total = 0
    for m in msgs:
        if not isinstance(m, dict) or not isinstance(m.get("content"), list):
            continue
        for b in m["content"]:
            if isinstance(b, dict) and b.get("type") == "thinking":
                total += _text_len(b.get("signature"))
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


def _input_size(inp, min_chars):
    """Обрезаемый объём аргументов вызова: сумма длинных строковых полей.

    Без привязки к именам инструментов: у Write длинное поле content, у Edit — old_string и
    new_string, у MultiEdit — вложенный список правок. Короткие поля (file_path, command)
    не считаем: их оставляем, чтобы модель помнила, КАКОЙ файл она писала.
    """
    if not isinstance(inp, dict):
        return None
    total = 0
    for v in inp.values():
        if isinstance(v, str) and len(v) >= min_chars:
            total += len(v)
        elif isinstance(v, list):
            for item in v:
                if isinstance(item, dict):
                    total += sum(len(x) for x in item.values() if isinstance(x, str) and len(x) >= min_chars)
    return total or None


def _prune_input(inp, min_chars):
    """Те же длинные поля — в плейсхолдер; форма и короткие поля сохраняются."""
    out = {}
    for k, v in inp.items():
        if isinstance(v, str) and len(v) >= min_chars:
            out[k] = PRUNE_INPUT_PLACEHOLDER
        elif isinstance(v, list):
            out[k] = [{x: (PRUNE_INPUT_PLACEHOLDER if isinstance(y, str) and len(y) >= min_chars else y)
                       for x, y in item.items()} if isinstance(item, dict) else item for item in v]
        else:
            out[k] = v
    return out


def prune_tool_results(msgs, keep_tail_tokens=None, step_tokens=None, min_chars=None,
                       min_tokens=None, min_context_tokens=None, extra_chars=0,
                       prune_thinking=None, prune_inputs=None, first_step_tokens=None,
                       kinds_out=None):
    """Возвращает (сообщения, сколько символов освободили, сколько блоков обрезали). Вход не мутирует.

    `kinds_out` — необязательный словарь, в который кладётся разбивка обрезанных блоков по
    видам (`result`/`input`/`thinking`) для карточки в чате. Отдельным выходным параметром, а
    не четвёртым элементом кортежа: разбивка нужна ровно одному вызывающему из трёх, а смена
    формы возврата задела бы каждый вызов и каждый тест границы.
    """
    prune_thinking = (PRUNE_THINKING == "on") if prune_thinking is None else prune_thinking
    prune_inputs = (PRUNE_INPUTS == "on") if prune_inputs is None else prune_inputs
    keep_tail_tokens = PRUNE_KEEP_TAIL_TOKENS if keep_tail_tokens is None else keep_tail_tokens
    step_tokens = max(1, PRUNE_STEP_TOKENS if step_tokens is None else step_tokens)
    first_step_tokens = PRUNE_FIRST_STEP_TOKENS if first_step_tokens is None else first_step_tokens
    # первая ступень не больше обычной; 0 (и любое неположительное) = первая ступень обычная
    first_step_tokens = step_tokens if first_step_tokens <= 0 else min(first_step_tokens, step_tokens)
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
    if estimate_tokens(_context_chars(msgs), stable_extra,
                       _signature_chars(msgs)) < min_context_tokens:
        return msgs, 0, 0

    # (номер сообщения, номер блока, размер, вид) в порядке появления. Три вида обрезаемого:
    #   result   — вывод инструмента (Bash/Grep/Read): до 97 % контекста на задачах чтения;
    #   input    — тело Write/Edit в аргументах вызова: до 47 % на задачах правки кода, модель
    #              записала файл, и весь его текст остался в истории навсегда;
    #   thinking — старые размышления: 7–15 %, модели самой они не нужны.
    # Разложение по четырём живым транскриптам 2026-09-22 — в README.
    found = []
    for mi, m in enumerate(msgs):
        if not isinstance(m, dict):
            continue
        content = m.get("content")
        if not isinstance(content, list):
            continue
        role = m.get("role")
        for bi, b in enumerate(content):
            if not isinstance(b, dict):
                continue
            kind = b.get("type")
            if role == "user" and kind == "tool_result":
                found.append((mi, bi, _prunable_size(b.get("content")), "result"))
            elif role == "assistant" and kind == "tool_use" and prune_inputs:
                found.append((mi, bi, _input_size(b.get("input"), min_chars), "input"))
            elif role == "assistant" and kind == "thinking" and prune_thinking:
                found.append((mi, bi, _text_len(b.get("thinking")) or None, "thinking"))

    # Граница в ТОКЕНАХ, а не в штуках вызовов. Мерить ступени в вызовах — мерить задачу не в
    # той единице, в которой она стоит: 35 вызовов бывают и 20k токенов, и 200k. Живой прогон
    # 2026-09-22 на этом и погорел — порог «70 вызовов» не наступил, пока чат не упёрся в
    # автосжатие. Здесь обе ручки в токенах, скрытого второго порога больше нет.
    #
    # Ступенчатость остаётся обязательной: непрерывная граница ползла бы на один вывод каждый
    # ход, меняя середину истории, — то есть рвала бы кэш каждый шаг. Стабильность даёт то,
    # что target квантован, а размеры выводов ДО него уже не меняются: пока target тот же,
    # граница стоит на месте бит в бит.
    # Разворачиваем ручки в символы коэффициентом ИСТОРИИ: режем мы её, а не системную часть.
    keep_chars = int(keep_tail_tokens * CHARS_PER_TOKEN_HISTORY)
    step_chars = max(1, int(step_tokens * CHARS_PER_TOKEN_HISTORY))  # ноль отсечён выше
    first_chars = max(1, int(first_step_tokens * CHARS_PER_TOKEN_HISTORY))

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

    prunable = sum(size for _, _, size, _ in found[:protect_idx] if весомый(size))
    # Квантование вниз: редкие скачки. Ряд целей — первая ступень, затем кратные обычной
    # (60k, 121k, 242k…): до полной ступени граница стоит на первой, дальше — как прежде.
    if prunable < step_chars:
        target = first_chars if prunable >= first_chars else 0
    else:
        target = prunable // step_chars * step_chars
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
    victims = [(mi, bi, kind) for mi, bi, size, kind in found[:boundary]
               if size is not None and size >= min_chars]
    freed = sum(size - len(PRUNE_PLACEHOLDER) for _, _, size, _ in found[:boundary]
                if size is not None and size >= min_chars)
    # Порог выгоды (освобождаем мы историю, значит и коэффициент её): ради мелочи префикс не
    # рвём. Оценка монотонна — граница только растёт, — поэтому порог срабатывает один раз за чат.
    if estimate_tokens(freed) < min_tokens:
        return msgs, 0, 0

    if kinds_out is not None:
        for _, _, kind in victims:
            kinds_out[kind] = kinds_out.get(kind, 0) + 1

    by_msg = {}
    for mi, bi, kind in victims:
        by_msg.setdefault(mi, {})[bi] = kind
    out = list(msgs)
    for mi, blocks in by_msg.items():
        m = dict(out[mi])
        content = list(m["content"])
        for bi, kind in blocks.items():
            b = dict(content[bi])
            if kind == "result":
                b["content"] = _placeholder_like(b.get("content"))
            elif kind == "input":
                b["input"] = _prune_input(b.get("input"), min_chars)
            else:  # thinking: текст в плейсхолдер, подпись вместе с ним теряет смысл
                b["thinking"] = PRUNE_THINKING_PLACEHOLDER
                b.pop("signature", None)
            content[bi] = b
        m["content"] = content
        out[mi] = m
    return out, freed, len(victims)


def _tail_text(msgs, role=None):
    """Текст последнего непустого сообщения: по нему и видно, ход это или запрос сжатия.

    Блоки склеиваются пробелом ровно так же, как в отладочном разборе, — сигнатура ловилась
    именно на этой склейке. `role` сужает поиск до сообщений одной роли.
    """
    for m in reversed(msgs):
        if not isinstance(m, dict) or (role is not None and m.get("role") != role):
            continue
        c = m.get("content")
        if isinstance(c, str):
            текст = c
        elif isinstance(c, list):
            текст = " ".join(b.get("text") or "" for b in c
                             if isinstance(b, dict) and b.get("type") == "text")
        else:
            continue
        if текст.strip():
            return текст
    return ""


def compact_verdict(doc, min_context_tokens=None):
    """Разбор запроса на три исхода: `("route"|"signature_small"|"no", оценка контекста)`.

    Признака распознавания ДВА, и оба обязательны. По одной сигнатуре сработал бы и обычный
    ход, в котором строка просто процитирована (человеком, файлом, выводом инструмента): такой
    ход уехал бы в облако и вернулся текстом без tool_use — агентная петля сбилась бы. Второму
    признаку (история уже размером с окно) обычный ход в норме не удовлетворяет: сжатие
    наступает только у чата, дошедшего до потолка.

    Отдельный исход `signature_small` — не оттенок «не распознали», а ДИАГНОЗ настройки:
    сигнатура пришла, значит CLI действительно затеял сжатие, но тело до порога не доросло.
    Живой замер 2026-09-23 показал, при чём это бывает: `CLAUDE_CODE_AUTO_COMPACT_WINDOW`
    ниже ~196 192 заставляет CLI сжиматься раньше, чем история наберёт
    `COMPACT_MIN_CONTEXT_TOKENS`. Без этого исхода сбой невидим: не растёт ни `compact_routed`,
    ни `compact_fallback` — запрос молча уезжает на локальную модель на свои 330–430 с.
    """
    min_context_tokens = (COMPACT_MIN_CONTEXT_TOKENS if min_context_tokens is None
                          else min_context_tokens)
    msgs = doc.get("messages")
    if not isinstance(msgs, list) or not msgs:
        return "no", None
    if not _tail_text(msgs, role="user").lstrip().startswith(COMPACT_SIGNATURE):
        return "no", None
    оценка = estimate_tokens(_context_chars(msgs), _system_chars(doc), _signature_chars(msgs))
    return ("route" if оценка >= min_context_tokens else "signature_small"), оценка


def is_compact_request(doc, min_context_tokens=None):
    """Это запрос автосжатия истории, а не обычный ход агента?"""
    return compact_verdict(doc, min_context_tokens)[0] == "route"


def compact_body(doc):
    """Тело для облака: то же самое, только `model` подменена. Вход не мутирует.

    Прунинг сюда не применяется намеренно — сводку облачная модель пишет по ОРИГИНАЛАМ
    выводов, ради этого маршрут заведён вторым по счёту выигрышем. Прочие правки прокси
    (`output_config`, `<total_tokens>`, повторные «# Environment») тоже не применяются: они
    лечат prefix cache vLLM, а здесь движок другой и запрос одноразовый. `output_config`
    MiniMax принимает молча — проверено прямым запросом 2026-09-22 (HTTP 200).
    """
    return json.dumps(dict(doc, model=COMPACT_MODEL), ensure_ascii=False).encode("utf-8")


def _secret_from_config(файл, путь, зачем):
    """Строка из json-конфига бэкенда по пути вида `A.B.C`; "" — не прочитали (с журналом)."""
    try:
        with open(файл, encoding="utf-8") as f:
            узел = json.load(f)
        for часть in путь.split("."):
            узел = узел[часть]
        return узел if isinstance(узел, str) else ""
    except Exception as e:
        print(f"{зачем} не прочитан ({файл}): {e!r}", file=sys.stderr, flush=True)
        return ""


_compact_key = None  # None — ещё не читали; "" — читать нечего, маршрут выключится сам


def compact_api_key():
    """Ключ облачного провайдера из конфига бэкенда. Читается один раз на процесс.

    Кэш на процесс, а не на запрос: ключ меняется раз в годы, а чтение файла на каждом ходе —
    лишний системный вызов на горячем пути. Сменился ключ — перезапуск службы, как и у любой
    другой ручки прокси.
    """
    global _compact_key
    if _compact_key is None:
        _compact_key = _secret_from_config(COMPACT_KEY_FILE, COMPACT_KEY_PATH,
                                           "ключ для автосжатия")
    return _compact_key


def open_compact(body, path, connector=None):
    """Шлёт тело в облачный upstream; возвращает (соединение, ответ, первый кусок) или None.

    None — любая осечка: нет ключа, сеть, не-200, пустой ответ, таймаут. Клиенту к этому
    моменту не отправлено ни байта, поэтому запрос уходит в локальный upstream штатным путём,
    с прунингом, как раньше (fail-closed): пользователь разницы не видит, кроме времени.

    Первый кусок читается ЗДЕСЬ, до ответа клиенту, намеренно: обрыв в начале потока — самый
    вероятный из отказов, и он ещё лечится фолбэком. Дальше поток уже начат, и отказ середины
    потока фолбэком не лечится ни при какой схеме — так же, как и у локального upstream.
    """
    key = compact_api_key()
    if not key:
        return None
    адрес = urlsplit(COMPACT_UPSTREAM if "//" in COMPACT_UPSTREAM else "https://" + COMPACT_UPSTREAM)
    conn = None
    try:
        фабрика = connector or (http.client.HTTPConnection if адрес.scheme == "http"
                                else http.client.HTTPSConnection)
        conn = фабрика(адрес.hostname, адрес.port, timeout=COMPACT_TIMEOUT)
        conn.request("POST", адрес.path.rstrip("/") + path, body=body, headers={
            "Content-Type": "application/json", "Content-Length": str(len(body)),
            "anthropic-version": COMPACT_ANTHROPIC_VERSION, "x-api-key": key})
        resp = conn.getresponse()
        if resp.status != 200:
            # Тело ошибки провайдера чужого чата не содержит — его в журнал брать можно.
            raise RuntimeError(f"HTTP {resp.status}: {resp.read(300)!r}")
        первый = resp.read1(65536)
        if not первый:
            raise RuntimeError("пустой ответ")
        print(f"автосжатие -> {COMPACT_MODEL} ({адрес.hostname}), тело {len(body) // 1024} КБ",
              file=sys.stderr, flush=True)
        return conn, resp, первый
    except Exception as e:
        print(f"автосжатие: облако не ответило ({e!r}) — уходим в локальный upstream",
              file=sys.stderr, flush=True)
        if conn is not None:
            conn.close()
        return None


# --- Событие о сдвиге границы: карточка в ленте чата -----------------------------------------

_event_secret = None  # None — ещё не читали; "" — читать нечего, канал выключится сам


def event_secret():
    """Секрет ручки бэкенда из его же конфига. Кэш на процесс — по тем же мотивам, что у ключа.

    Не-ASCII секрет отбрасываем ЗДЕСЬ, а не на отправке: http-заголовки кодируются latin-1, и
    UnicodeEncodeError несёт в тексте саму строку — fail-open записал бы секрет в журнал.
    """
    global _event_secret
    if _event_secret is None:
        секрет = _secret_from_config(EVENT_SECRET_FILE, EVENT_SECRET_PATH, "секрет ручки событий")
        if not секрет.isascii():
            print("секрет ручки событий содержит не-ASCII символы — канал выключен "
                  "(заголовок с ним не отправить); задай секрет латиницей",
                  file=sys.stderr, flush=True)
            секрет = ""
        _event_secret = секрет
    return _event_secret


# Последняя граница прунинга по сессиям: сколько блоков было обрезано в прошлый раз. Только в
# памяти процесса — переживать перезапуск этому состоянию не нужно (в худшем случае человек
# увидит одну лишнюю карточку), а файл на диске стоил бы синхронизации.
_boundaries = {}
_boundaries_lock = threading.Lock()


def boundary_shifted(session_id, blocks):
    """Запоминает границу сессии и отвечает, сдвинулась ли она. Первое срабатывание — сдвиг.

    Сравниваем число обрезанных блоков: граница квантована ступенью и между сдвигами стоит на
    месте бит в бит, поэтому одно и то же число блоков = та же граница = обрезка бесплатна, и
    карточке в ленте взяться неоткуда.
    """
    if not session_id or blocks <= 0:
        return False
    with _boundaries_lock:
        прежняя = _boundaries.get(session_id)
        if прежняя == blocks:
            return False
        _boundaries[session_id] = blocks
        return True


def usage_from_chunk(chunk):
    """`(cache_read_input_tokens, весь промпт)` из `message_start` первого куска SSE.

    `(None, None)` — если в куске его нет (частичное событие, чужой формат). Промпт — СУММА
    трёх слагаемых usage: `input_tokens` считает только то, что прошло мимо кэша, и доля
    `cache_read / input_tokens` вышла бы больше единицы.
    """
    try:
        for строка in chunk.split(b"\n"):
            if not строка.startswith(b"data:"):
                continue
            событие = json.loads(строка[5:].strip())
            if событие.get("type") != "message_start":
                continue
            usage = (событие.get("message") or {}).get("usage") or {}
            чтение = usage.get("cache_read_input_tokens")
            промпт = sum(usage.get(k) or 0 for k in
                         ("input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"))
            return (чтение if isinstance(чтение, int) else None), (промпт or None)
    except Exception:
        pass  # чужой формат ответа карточку не ломает: уйдёт без цифр кэша
    return None, None


def post_event(payload, connector=None):
    """Шлёт событие бэкенду. Fail-open: любой отказ — строка в журнал, ход продолжается.

    Канал декоративный: недоступный бэкенд не смеет ни задержать поток пользователю, ни тем
    более оборвать ход. Поэтому короткий таймаут, ловим всё и ничего не пробрасываем.
    """
    if not BACKEND_EVENTS_URL:
        return False
    секрет = event_secret()
    if not секрет:
        return False
    conn = None
    try:
        адрес = urlsplit(BACKEND_EVENTS_URL if "//" in BACKEND_EVENTS_URL
                         else "http://" + BACKEND_EVENTS_URL)
        фабрика = connector or (http.client.HTTPSConnection if адрес.scheme == "https"
                                else http.client.HTTPConnection)
        conn = фабрика(адрес.hostname, адрес.port, timeout=EVENT_TIMEOUT)
        тело = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        conn.request("POST", адрес.path or "/", body=тело, headers={
            "Content-Type": "application/json", "Content-Length": str(len(тело)),
            "X-Proxy-Secret": секрет})
        ответ = conn.getresponse()
        ответ.read()
        if ответ.status >= 300:
            raise RuntimeError(f"HTTP {ответ.status}")
        with lock:
            stats["events_sent"] += 1
        return True
    except Exception as e:
        with lock:
            stats["events_failed"] += 1
        print(f"событие о сдвиге границы не доставлено ({e!r})", file=sys.stderr, flush=True)
        return False
    finally:
        if conn is not None:
            conn.close()


_last_event_thread = None  # нужен тестам: дождаться фоновой отправки, не заводя ожиданий по времени


def send_event(payload):
    """Отправка в фоновом потоке: поток ответа клиенту не ждёт бэкенда ни секунды."""
    global _last_event_thread
    if not BACKEND_EVENTS_URL:
        return
    поток = threading.Thread(target=post_event, args=(payload,), daemon=True)
    _last_event_thread = поток
    поток.start()


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
        мысли = [_text_len(b.get("thinking"))
                 for m in msgs if isinstance(m, dict) and isinstance(m.get("content"), list)
                 for b in m["content"] if isinstance(b, dict) and b.get("type") == "thinking"]
        входы = [_input_size(b.get("input"), PRUNE_MIN_CHARS) or 0
                 for m in msgs if isinstance(m, dict) and isinstance(m.get("content"), list)
                 for b in m["content"] if isinstance(b, dict) and b.get("type") == "tool_use"]
        системная = _system_chars(doc)
        подписи = _signature_chars(msgs)
        контекст = estimate_tokens(_context_chars(msgs), системная, подписи)
        потолок = estimate_tokens(_context_chars(msgs), системная, подписи, conservative=True)
        _, freed, blocks = prune_tool_results(msgs, extra_chars=системная)
        хвост = _tail_text(msgs)
        print(f"[отладка] тело {body_len // 1024} КБ | сообщений {len(msgs)} | "
              f"выводов {len(выводы)} (крупных {len(крупные)} на "
              f"{estimate_tokens(sum(крупные)) // 1000}k ток) | "
              f"thinking {len(мысли)} шт на {estimate_tokens(sum(мысли)) // 1000}k ток | "
              f"Write/Edit {sum(1 for x in входы if x)} шт на "
              f"{estimate_tokens(sum(входы)) // 1000}k ток | "
              f"системная часть {estimate_tokens(0, системная) // 1000}k ток | "
              f"контекст {контекст // 1000}k ток (потолок {потолок // 1000}k) | "
              f"прунинг: {blocks} блоков, {estimate_tokens(freed) // 1000}k ток | "
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
            msg = json.dumps(dict(stats, prune=PRUNE_MODE, thinking=THINKING_MODE,
                                  compact=COMPACT_MODEL if COMPACT_UPSTREAM else "off")).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(msg)))
        self.end_headers()
        self.wfile.write(msg)

    @staticmethod
    def _событие_с_замером(событие, начало, chunk):
        """Дописывает в заготовку замер prefill и цифры кэша из первого куска — и отправляет."""
        чтение, промпт = usage_from_chunk(chunk)
        событие["prefillSeconds"] = round(time.monotonic() - начало, 3)
        событие["cacheReadTokens"] = чтение
        событие["promptTokens"] = промпт
        send_event(событие)

    def _proxy(self):
        # Счётчики наружу: замер «сколько на самом деле обрезали» снимать больше неоткуда.
        # Путь служебный, у Anthropic-роутера vLLM такого нет — с upstream не пересекается.
        if self.command == "GET" and self.path.split("?")[0] == "/__proxy/stats":
            self._read_body()
            return self._stats()
        body = self._read_body()
        original = body  # на откат: любая осечка разбора обязана вернуть тело нетронутым
        stripped = False
        compact = None  # тело для облака, если это запрос автосжатия
        сессия = self.headers.get(EVENT_SESSION_HEADER) if BACKEND_EVENTS_URL else None
        событие = None  # заготовка карточки: цифры замера допишем по первому байту ответа
        compact_before = None
        if body and self.command == "POST":
            try:
                doc = json.loads(body)
                if isinstance(doc, dict):
                    # Распознаём и снимаем копию ДО правок: в облако едет сырая история.
                    if COMPACT_UPSTREAM:
                        вердикт, оценка = compact_verdict(doc)
                        if вердикт == "route":
                            compact = compact_body(doc)
                            compact_before = оценка
                        elif вердикт == "signature_small":
                            # Настройка стенда разъехалась: CLI сжимается раньше, чем история
                            # набирает порог. Молча отпускать такой запрос на локаль нельзя —
                            # это те самые 330–430 с стоп-мира, ради которых маршрут заведён.
                            with lock:
                                stats["compact_signature_small"] += 1
                            print(f"автосжатие: сигнатура есть, но тело {оценка} ток < порога "
                                  f"{COMPACT_MIN_CONTEXT_TOKENS} — запрос уходит на локаль; "
                                  f"подними CLAUDE_CODE_AUTO_COMPACT_WINDOW (>= 196192) или "
                                  f"опусти COMPACT_MIN_CONTEXT_TOKENS",
                                  file=sys.stderr, flush=True)
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
                            виды = {}
                            системная = _system_chars(doc)
                            pruned_msgs, freed, blocks = prune_tool_results(
                                kept, extra_chars=системная, kinds_out=виды)
                            if blocks:
                                doc["messages"] = pruned_msgs
                                stripped = True
                                with lock:
                                    stats["pruned_blocks"] += blocks
                                    stats["pruned_chars"] += freed
                                # Карточка — только на СДВИГЕ границы. Решение принимаем здесь,
                                # до запроса, а отправляем по первому байту ответа: замер
                                # prefill — это и есть «сколько человек ждал».
                                #
                                # Запрос, уезжающий в облако, пропускаем целиком, вместе с
                                # запоминанием границы: обрезанное тело туда не идёт (сводку
                                # облако пишет по оригиналам), и замер получился бы облачный
                                # при рассказе о локальном пересчёте. Границу не запоминаем
                                # намеренно — ход с фолбэком на локаль покажет карточку
                                # следующим шагом, с честным замером.
                                if compact is None and boundary_shifted(сессия, blocks):
                                    до = estimate_tokens(_context_chars(kept), системная,
                                                         _signature_chars(kept))
                                    событие = {
                                        "sessionId": сессия, "kind": "prune",
                                        "tokensBefore": до,
                                        "tokensAfter": max(0, до - estimate_tokens(freed)),
                                        "blocks": blocks,
                                        "resultBlocks": виды.get("result", 0),
                                        "inputBlocks": виды.get("input", 0),
                                        "thinkingBlocks": виды.get("thinking", 0)}
                    if stripped:
                        body = json.dumps(doc, ensure_ascii=False).encode("utf-8")
            except Exception as e:
                # fail-open: не наш формат ИЛИ неожиданная структура внутри — отдаём тело как
                # есть. Ловим всё намеренно: исключение отсюда уходит в handle_one_request,
                # клиент остаётся без ответа и висит до своего таймаута — то есть опечатка в
                # разборе тела ломает ход пользователя молча. Тело чужое, доверять его форме
                # нельзя: `{"type":"text","text":5}` достаточно, чтобы получить TypeError.
                body, stripped, compact, событие = original, False, None, None
                print(f"тело пропущено без правок: {e!r}", file=sys.stderr, flush=True)
        with lock:
            stats["requests"] += 1
            stats["stripped"] += stripped

        conn = resp = первый = None
        # Точка отсчёта prefill: время до первого байта ответа модели. Именно его человек и
        # ждёт на сдвиге границы (замеры: 80–90 с), поэтому цифра в карточке — эта. Отсчёт
        # ведём от отправки тела В ТОТ upstream, который в итоге отвечает: неудачная попытка
        # облака к ожиданию локальной модели не относится, и её время в замер не идёт.
        начало = time.monotonic()
        if compact is not None:
            попытка = open_compact(compact, self.path)
            with lock:
                stats["compact_routed" if попытка else "compact_fallback"] += 1
            if попытка:
                conn, resp, первый = попытка
                # Сжатие ушло в облако — рассказываем и об этом: для человека это тот же
                # «чат думает молча». Первый кусок уже прочитан внутри open_compact, поэтому
                # замер включает и установку соединения — на фоне десятков секунд не важно.
                if сессия:
                    событие = {"sessionId": сессия, "kind": "compact_cloud",
                               "tokensBefore": compact_before or 0, "tokensAfter": 0,
                               "blocks": 0, "resultBlocks": 0, "inputBlocks": 0,
                               "thinkingBlocks": 0}
                    self._событие_с_замером(событие, начало, первый)
                    событие = None  # уже отправлено, второй раз по первому чанку не шлём
        if resp is None:
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
            if body:
                headers["Content-Length"] = str(len(body))
            conn = http.client.HTTPConnection(UP_HOST, UP_PORT, timeout=None)
            try:
                начало = time.monotonic()
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
            if первый:  # кусок, прочитанный ради fail-closed, отдаём первым
                self.wfile.write(b"%x\r\n%s\r\n" % (len(первый), первый))
                self.wfile.flush()
            while True:
                chunk = resp.read1(65536)
                if not chunk:
                    break
                if событие is not None:
                    # Первый байт ответа: замер готов, цифры кэша лежат в message_start этого
                    # же куска. Отправка фоновая, поток пользователю не задерживается.
                    self._событие_с_замером(событие, начало, chunk)
                    событие = None
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
             f"{PRUNE_STEP_TOKENS // 1000}k ток, первая {PRUNE_FIRST_STEP_TOKENS // 1000}k, "
             f"порог контекста {PRUNE_MIN_CONTEXT_TOKENS // 1000}k)"
             if PRUNE_MODE == "on" else ", прунинг выводов выкл")
          + (f", автосжатие -> {COMPACT_MODEL} на {COMPACT_UPSTREAM} (порог "
             f"{COMPACT_MIN_CONTEXT_TOKENS // 1000}k ток)" if COMPACT_UPSTREAM
             else ", автосжатие в облако выкл")
          + (f", карточка сдвига границы -> {BACKEND_EVENTS_URL}" if BACKEND_EVENTS_URL
             else ", карточка сдвига границы выкл"), flush=True)
    srv.serve_forever()
