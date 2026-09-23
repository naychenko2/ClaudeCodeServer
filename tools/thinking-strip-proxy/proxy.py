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
import gzip, http.client, json, os, re, sys, threading, time
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
#
# С 2026-09-23 ступень квантует не обрезаемый объём, а СЫРОЙ КОНТЕКСТ (см.
# PRUNE_MIN_CONTEXT_TOKENS): полоса k = floor((ctx − G) / S) + 1, цель обрезки k × S. Так
# число сдвигов диктуется тем, сколько раз чат вырос на ступень, а не тем, сколько раз на
# ступень выросла куча обрезаемого. Прежняя схема на симуляции трёх транскриптов давала
# провал между первой и второй границей: usage успевал дойти до 313k (порог сжатия CLI 232k),
# потому что вторая полная ступень обрезаемого набиралась намного позже, чем росла история.
PRUNE_STEP_TOKENS = int(os.environ.get("PRUNE_STEP_TOKENS", "121000"))
PRUNE_MIN_CHARS = int(os.environ.get("PRUNE_MIN_CHARS", "2000"))
# Порог выгоды, тоже пересчитан к новому коэффициенту: 24000 * 3.3 = 79.2k символов против
# прежних 20000 * 4 = 80k. Смысл прежний — ради мелочи префикс не рвём.
PRUNE_MIN_TOKENS = int(os.environ.get("PRUNE_MIN_TOKENS", "24000"))
# Гейт G — он же «чат уже большой», он же начало первой полосы. Ниже него прунинга нет вовсе
# (маленькому чату автосжатие не грозит, а сдвиг границы он оплатил бы впустую), на нём
# граница встаёт первый раз, дальше полосы идут через ступень: G, G+S, G+2S…
#
# Значение выбирается ОДНИМ неравенством: пока прунинг молчит, usage обязан остаться ниже
# порога автосжатия CLI (232k при незаданной CLAUDE_CODE_AUTO_COMPACT_WINDOW). Худший ход,
# на котором прунинг ещё молчит, несёт G − 1 по нашей оценке; к нему прибавляются недосчёт
# огрубления системной части (ниже 13.6k при SYSTEM_ROUNDING_CHARS = 50 000) и один большой
# Read, который придёт следующим (25k):
#
#     190 000 + 13 600 + 25 000 = 228 600 < 232 000
#
# Отсюда 190 000. Прежние 150 000 брались при огрублении до 100k символов (недосчёт до 27k) и
# при первой ступени, которой больше нет: с ней порог включения задавался не контекстом, а
# накопленным обрезаемым объёмом, и запас приходилось держать вдвое больше.
PRUNE_MIN_CONTEXT_TOKENS = int(os.environ.get("PRUNE_MIN_CONTEXT_TOKENS", "190000"))
# Огрубление системной части в оценке контекста — стателесс-гистерезис. История append-only и
# только растёт, а системная часть НЕ обязана: секции промпта этого продукта пересобираются
# каждый ход. Похудей системный блок у края полосы — k упал бы, граница отъехала назад, и
# середина истории замигала бы на каждом ходу: ровно потеря кэша, от которой заведён прокси.
# Поэтому огрубляем вниз НЕСТАБИЛЬНОЕ слагаемое (огрубление суммы до кратного порогу —
# тождество, оно ничего не даёт).
#
# 50 000, а не прежние 100 000: огрубление вниз занижает оценку, и этот недосчёт съедает
# запас до порога сжатия CLI (см. выкладку у G). Вдвое мельче — вдвое меньше и недосчёт
# (13.6k против 27k токенов), и амплитуда скачка на краю огрубления, если системная часть
# всё-таки задрожит у кратного значения. Совсем убирать огрубление нельзя: тогда дрожь
# системной части у границы полосы перещёлкивает k каждый ход.
SYSTEM_ROUNDING_CHARS = int(os.environ.get("PRUNE_SYSTEM_ROUNDING_CHARS", "50000"))
# На сколько долей дробится цель обрезки, когда её физически не набрать (разбор — у самой
# проверки в prune_tool_results). Не ручка окружения намеренно: это не настройка стенда, а
# геометрия схемы, снятая симуляцией, — менять её значит переснимать симуляцию.
#
# 3 — из перебора на трёх транскриптах × двух размерах системной части (гейт 190k, симулятор
# sim_prune.py, таблица в README): 1 доля (дробления нет) — 5 автосжатий из 6 прогонов,
# 2 доли — 4, 3 доли — 1, 4 и 6 долей — тоже 1, но сдвигов уже 7 и 8 против 5.
PRUNE_DEFICIT_PARTS = 3
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

# --- Дамп тел запросов: разбор промахов prefix cache ----------------------------------------
#
# Заведён под промахи при НЕПОДВИЖНОЙ границе прунинга (живой прогон 2026-09-23: четыре из
# шести промахов при границе 30 блоков, из кэша ровно системная часть ~13k). Отладочная строка
# на такой вопрос не отвечает: она печатает размеры, а разошлись байты. Поэтому здесь на диск
# ложится ТЕЛО ПОСЛЕ ПРАВОК прокси — ровно то, что ушло в движок, — плюс `usage` ответа и
# время до первого байта. Разбирает дамп `diff_miss.py`.
#
# ВЫКЛЮЧЕНО по умолчанию и не должно включаться надолго: в файлах лежат чужие чаты целиком.
DUMP_DIR = os.environ.get("PRUNE_DUMP_DIR", "")
# Предохранитель на каталог. Не вкусовщина: дамп содержит переписку, и путь задаётся
# переменной окружения в юните — опечатка вроде `PRUNE_DUMP_DIR=.` высыпала бы чужие чаты в
# рабочее дерево репозитория, откуда они уехали бы в коммит. Поэтому разрешены ровно два
# корня: `/tmp` (чистится сам) и `data/` репозитория (в .gitignore и под бэкапом).
_REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DUMP_ALLOWED_ROOTS = ("/tmp", os.path.join(_REPO_ROOT, "data"))

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
# распознавание.
#
# Дефолт — собственный литерал, а не ссылка на гейт прунинга (2026-09-23): гейт переехал со
# 150k на 190k вместе со сменой схемы квантования, и привязанный к нему порог распознавания
# уехал бы следом, потребовав CLAUDE_CODE_AUTO_COMPACT_WINDOW уже не ≥196k, а сильно выше.
# Запрос сжатия при живом стенде молча уходил бы на локаль — те самые 330–430 с стоп-мира.
# Две величины отвечают на разные вопросы и совпадали случайно.
COMPACT_MIN_CONTEXT_TOKENS = int(os.environ.get("COMPACT_MIN_CONTEXT_TOKENS", "150000"))

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
                       prune_thinking=None, prune_inputs=None, kinds_out=None):
    """Возвращает (сообщения, сколько символов освободили, сколько блоков обрезали). Вход не мутирует.

    Схема границы (2026-09-23): цель обрезки задаётся ПОЛОСОЙ СЫРОГО КОНТЕКСТА, а не
    накопленным обрезаемым объёмом. `band = (ctx − G) // S + 1` при `ctx ≥ G`, цель `band × S`;
    блоки берутся с начала истории, пока накопленное не упрётся в цель. Недостижимую цель
    (обрезаемого меньше) дробим по долям цели — иначе граница поехала бы за скользящим хвостом.

    `kinds_out` — необязательный словарь, в который кладётся разбивка обрезанных блоков по
    видам (`result`/`input`/`thinking`) для карточки в чате. Отдельным выходным параметром, а
    не четвёртым элементом кортежа: разбивка нужна ровно одному вызывающему из трёх, а смена
    формы возврата задела бы каждый вызов и каждый тест границы.
    """
    prune_thinking = (PRUNE_THINKING == "on") if prune_thinking is None else prune_thinking
    prune_inputs = (PRUNE_INPUTS == "on") if prune_inputs is None else prune_inputs
    keep_tail_tokens = PRUNE_KEEP_TAIL_TOKENS if keep_tail_tokens is None else keep_tail_tokens
    step_tokens = max(1, PRUNE_STEP_TOKENS if step_tokens is None else step_tokens)
    min_chars = PRUNE_MIN_CHARS if min_chars is None else min_chars
    min_tokens = PRUNE_MIN_TOKENS if min_tokens is None else min_tokens
    min_context_tokens = PRUNE_MIN_CONTEXT_TOKENS if min_context_tokens is None else min_context_tokens

    # Полоса контекста. Меряем СЫРОЙ контекст (историю целиком плюс огрублённую системную
    # часть — см. SYSTEM_ROUNDING_CHARS) и по нему решаем, сколько резать: ниже гейта не режем
    # вовсе, дальше цель растёт на ступень с каждой пройденной ступенью контекста.
    #
    # Квантовать по контексту, а не по накопленному обрезаемому объёму (как было до
    # 2026-09-23), — потому что задача стоит в контексте: usage не должен доехать до порога
    # сжатия CLI. Обрезаемый объём растёт СВОИМ темпом, и на симуляции трёх транскриптов
    # старая схема оставляла провал между первой и второй границей — usage доходил до 313k
    # при пороге 232k, пока вторая ступень обрезаемого не набралась.
    rounding = max(1, SYSTEM_ROUNDING_CHARS)
    stable_extra = max(0, extra_chars) // rounding * rounding
    ctx = estimate_tokens(_context_chars(msgs), stable_extra, _signature_chars(msgs))
    if ctx < min_context_tokens:
        return msgs, 0, 0
    band = (ctx - min_context_tokens) // step_tokens + 1

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

    # Цель обрезки — полоса контекста, помноженная на ступень. Всё меряется в ТОКЕНАХ, а не в
    # штуках вызовов: 35 вызовов бывают и 20k токенов, и 200k, то есть в вызовах задача просто
    # не измеряется (живой прогон 2026-09-22 на этом и погорел — порог «70 вызовов» не
    # наступил, пока чат не упёрся в автосжатие).
    #
    # Ступенчатость обязательна: непрерывная граница ползла бы на один вывод каждый ход, меняя
    # середину истории, — то есть рвала бы кэш каждый шаг. Стабильность даёт то, что цель
    # зависит только от ПОЛОСЫ, а размеры выводов до границы при дописывании хвоста не
    # меняются: внутри полосы граница стоит бит в бит, на переходе двигается один раз.
    # Разворачиваем ручки в символы коэффициентом ИСТОРИИ: режем мы её, а не системную часть.
    keep_chars = int(keep_tail_tokens * CHARS_PER_TOKEN_HISTORY)
    target = int(band * step_tokens * CHARS_PER_TOKEN_HISTORY)

    tail, protect_idx = 0, len(found)  # защищаем хвост по объёму: свежее модель ещё читает
    while protect_idx > 0 and tail < keep_chars:
        tail += found[protect_idx - 1][2] or 0
        protect_idx -= 1

    # Мелкие выводы не трогаем вовсе: экономии с них нет, а кэш рвём. Поэтому они и в
    # накопленное не идут — иначе чат с мелкими выводами набирал бы цель штуками, освобождая
    # копейки (история 035c3dd7, медиана вывода 1 КБ).
    def весомый(size):
        return size is not None and size >= min_chars

    # Цель бывает физически недостижима: у чата с жирной системной частью контекст уже в
    # третьей полосе, а резать нечего — вся история короче цели. Наивное «тогда режем всё
    # обрезаемое вне хвоста» ломает главный инвариант: конец обрезаемого — это НАЧАЛО
    # защищённого хвоста, а он съезжает с каждым новым выводом, и граница ползёт каждый ход.
    # Симуляция трёх транскриптов (боевой тулсет с MCP): 52–67 сдвигов против 3 у прежней
    # схемы. Поэтому и недостижимая цель квантуется — половиной полосы:
    #
    #   цель = полоса × S, если столько обрезаемого есть; иначе половина; иначе не режем.
    #
    # Так граница ВСЕГДА упирается в цель, а не в хвост, и внутри полосы стоит бит в бит.
    # Доли берутся ОТ ЦЕЛИ, а не абсолютной величиной: квант растёт вместе с полосой (40k,
    # 81k, 121k в первой полосе, вдвое крупнее во второй), то есть чем больше чат, тем реже
    # сдвиги, а ранний первый срез сохраняется — 40k обрезаемого набирается даже раньше, чем
    # набиралась прежняя первая ступень 60k.
    available = sum(size for _, _, size, _ in found[:protect_idx] if весомый(size))
    if available < target:
        доли = max(1, PRUNE_DEFICIT_PARTS)
        target = max((target * i // доли for i in range(1, доли)
                      if available >= target * i // доли), default=0)
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


def usage_dict_from_chunk(chunk):
    """Сырой `usage` из куска SSE; `{}` — не нашли.

    Смотрит и `message_start` (`message.usage`), и `message_delta` (`usage` рядом с
    `delta`): роутер vLLM кладёт цифры кэша именно во второе событие, в конец потока.
    """
    собранный = {}
    try:
        for строка in chunk.split(b"\n"):
            if not строка.startswith(b"data:"):
                continue
            событие = json.loads(строка[5:].strip())
            usage = ((событие.get("message") or {}).get("usage")
                     if событие.get("type") == "message_start" else событие.get("usage"))
            if isinstance(usage, dict):
                собранный.update(usage)
    except Exception:
        pass  # чужой формат ответа карточку не ломает: уйдёт без цифр кэша
    return собранный


def merge_usage(копилка, chunk):
    """Копит `usage` по ходу потока: слияние по максимуму. Возвращает ту же копилку.

    По всему потоку, а не по первому куску: цифры кэша в `message_start` роутера vLLM НЕ
    приходят — там только `input_tokens` и `output_tokens` (снято живьём 2026-09-23);
    `cache_read_input_tokens` и `cache_creation_input_tokens` появляются в финальном
    `message_delta`. Собери usage по первому куску — и главная цифра будет пустой ВСЕГДА.

    По максимуму, потому что значения по ходу потока только растут (`output_tokens`
    накапливается, цифры кэша приходят один раз), а нули промежуточных событий не должны
    затирать уже известное.
    """
    свежий = usage_dict_from_chunk(chunk)
    for ключ, значение in свежий.items():
        if isinstance(значение, int):
            копилка[ключ] = max(значение, копилка.get(ключ) or 0)
        elif ключ not in копилка:
            копилка[ключ] = значение
    return копилка


def usage_totals(usage):
    """`(cache_read_input_tokens, весь промпт)` из накопленного usage — для карточки.

    `(None, None)` — если цифр нет вовсе (чужой формат ответа, оборванный поток).

    У `input_tokens` роутера vLLM ДВЕ семантики, и различать их обязательно (та же развилка,
    что в `diff_miss.цифры`): в `message_start` это только то, что прошло МИМО кэша, а в
    финальном `message_delta` — ВЕСЬ промпт, кэш включительно. Сложишь слагаемые вслепую —
    промпт удвоится, и доля из кэша в карточке соврёт вдвое. Признак полной формы простой:
    `input_tokens` не меньше `cache_read_input_tokens`.
    """
    вход = usage.get("input_tokens") or 0
    чтение = usage.get("cache_read_input_tokens")
    промпт = вход if вход >= (чтение or 0) else (
        вход + (чтение or 0) + (usage.get("cache_creation_input_tokens") or 0))
    return (чтение if isinstance(чтение, int) else None), (промпт or None)


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
    поток.start()
    # Присваиваем ПОСЛЕ запуска: ждущий отправки тест иначе поймает поток между созданием и
    # стартом, а join по такому — RuntimeError.
    _last_event_thread = поток


# --- Дамп тел на диск ------------------------------------------------------------------------

_dump_dir = None  # None — ещё не готовили; "" — дамп выключен (или каталог не годится)
_dump_seq = 0
_dump_lock = threading.Lock()


def prepare_dump_dir(путь=None):
    """Готовит каталог дампа и возвращает его; "" — дамп выключен. Кэш на процесс.

    Каталог сверяется с `DUMP_ALLOWED_ROOTS` — см. мотив у самой константы. Несогласный путь
    не молчаливая осечка, а строка в журнал: человек включил дамп и обязан узнать, что записи
    не будет, а не искать потом пустую папку.
    """
    global _dump_dir
    if путь is not None:  # явный вызов (тесты, повторная настройка) кэш не переиспользует
        _dump_dir = None
    цель = DUMP_DIR if путь is None else путь
    if _dump_dir is None:
        _dump_dir = ""
        if цель:
            полный = os.path.realpath(цель)
            разрешён = any(полный == корень or полный.startswith(корень + os.sep)
                           for корень in DUMP_ALLOWED_ROOTS)
            if not разрешён:
                print(f"дамп тел выключен: каталог {полный} вне разрешённых "
                      f"({', '.join(DUMP_ALLOWED_ROOTS)}) — в телах лежат чужие чаты",
                      file=sys.stderr, flush=True)
            else:
                try:
                    os.makedirs(полный, mode=0o700, exist_ok=True)
                    _dump_dir = полный
                    print(f"дамп тел запросов -> {полный} (в файлах чужие чаты — "
                          f"выключи, как разберёшься)", file=sys.stderr, flush=True)
                except OSError as e:
                    print(f"дамп тел выключен: каталог {полный} не создан ({e!r})",
                          file=sys.stderr, flush=True)
    return _dump_dir


class ЗаписьДампа:
    """Дамп одного запроса: тело сразу, мета — в конце ответа.

    Двумя файлами, а не одним: тело известно до отправки, а `usage` приходит последним
    событием потока, и запиши мы всё разом в конце, тела оборванных запросов (а они-то и
    интересны) не сохранились бы вовсе. Тело жмётся gzip: прогон — это 190 запросов по мегабайту.

    Fail-open, как и всё остальное в прокси: любая осечка записи — строка в журнал, ход идёт
    дальше. Дамп диагностический, и падать из-за него ходу пользователя не за что.
    """

    def __init__(self, каталог, seq, session, тело, мета):
        self.мета_путь = ""
        сейчас = time.time()
        метка = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(сейчас))
        self.мета = dict(мета, seq=seq, session=session or None, bodyBytes=len(тело),
                         ts=f"{метка}.{int(сейчас * 1000) % 1000:03d}", epoch=round(сейчас, 3))
        основа = os.path.join(каталог, f"{time.strftime('%H%M%S', time.localtime(сейчас))}-"
                                       f"{seq:05d}-{(session or 'nosess')[:12]}")
        try:
            with gzip.open(основа + ".req.json.gz", "wb") as f:
                f.write(тело)
            os.chmod(основа + ".req.json.gz", 0o600)
            self.мета["body"] = os.path.basename(основа) + ".req.json.gz"
            self.мета_путь = основа + ".meta.json"
        except Exception as e:
            print(f"дамп тела не записан ({e!r})", file=sys.stderr, flush=True)

    def первый_байт(self, chunk, начало):
        """Замер prefill: время до первого байта. Второй раз ничего не делает."""
        if "ttfbSeconds" in self.мета:
            return
        self.мета["ttfbSeconds"] = round(time.monotonic() - начало, 3)

    def усвоить(self, chunk):
        """Копит `usage` по ходу потока — той же копилкой, что и карточка (см. `merge_usage`)."""
        merge_usage(self.мета.setdefault("usage", {}), chunk)

    def записать(self):
        if not self.мета_путь:
            return
        путь, self.мета_путь = self.мета_путь, ""
        try:
            # Через временный файл с переименованием: мету пишет обработчик уже ПОСЛЕ ответа
            # клиенту, а разбор могут запустить в любой момент — пусть видит либо готовый
            # файл с правами 0600, либо ничего, но не половину.
            with open(путь + ".tmp", "w", encoding="utf-8") as f:
                json.dump(self.мета, f, ensure_ascii=False, indent=1)
            os.chmod(путь + ".tmp", 0o600)
            os.replace(путь + ".tmp", путь)
        except Exception as e:
            print(f"дамп меты не записан ({e!r})", file=sys.stderr, flush=True)


def dump_request(тело, session, мета):
    """Кладёт тело запроса в дамп и возвращает `ЗаписьДампа` (или None, если дамп выключен)."""
    каталог = prepare_dump_dir()
    if not каталог:
        return None
    global _dump_seq
    with _dump_lock:
        _dump_seq += 1
        seq = _dump_seq
    return ЗаписьДампа(каталог, seq, session, тело, мета)


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
    def _отправить_событие(событие, prefill, usage):
        """Дописывает в заготовку замер prefill и цифры кэша — и отправляет.

        Замер prefill снят по ПЕРВОМУ байту ответа (это и есть время, которое человек ждал),
        а usage — по концу потока: цифры кэша роутер vLLM кладёт в финальный `message_delta`,
        и на первом байте их не существует.
        """
        событие["prefillSeconds"] = None if prefill is None else round(prefill, 3)
        событие["cacheReadTokens"], событие["promptTokens"] = usage_totals(usage)
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
        # Заголовок чата нужен обоим потребителям — карточке и дампу: разбор промахов идёт
        # ПО СЕССИЯМ (сравнивается соседняя пара запросов одного чата), и без него дамп
        # прогона превращается в кашу из ходов исполнителя и фоновых действий продукта.
        сессия = (self.headers.get(EVENT_SESSION_HEADER)
                  if (BACKEND_EVENTS_URL or DUMP_DIR) else None)
        событие = None  # заготовка карточки: цифры замера допишем по первому байту ответа
        дамп = None  # дамп тела, если включён PRUNE_DUMP_DIR
        compact_before = None
        прунинг = {"blocks": 0, "freedChars": 0}
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
                                прунинг = {"blocks": blocks, "freedChars": freed}
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
                дамп = dump_request(compact, сессия, dict(
                    прунинг, path=self.path, upstream="cloud", stripped=False))
                if дамп is not None:
                    дамп.первый_байт(первый, начало)
                    дамп.усвоить(первый)
                # Сжатие ушло в облако — рассказываем и об этом: для человека это тот же
                # «чат думает молча». Первый кусок уже прочитан внутри open_compact, поэтому
                # замер включает и установку соединения — на фоне десятков секунд не важно.
                if сессия:
                    событие = {"sessionId": сессия, "kind": "compact_cloud",
                               "tokensBefore": compact_before or 0, "tokensAfter": 0,
                               "blocks": 0, "resultBlocks": 0, "inputBlocks": 0,
                               "thinkingBlocks": 0}
        if resp is None:
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
            if body:
                headers["Content-Length"] = str(len(body))
            conn = http.client.HTTPConnection(UP_HOST, UP_PORT, timeout=None)
            try:
                начало = time.monotonic()
                conn.request(self.command, self.path, body=body or None, headers=headers)
                # Дамп пишется ПОСЛЕ отправки тела и до чтения ответа: так сжатие мегабайта
                # идёт, пока модель делает prefill, и замер `ttfbSeconds` остаётся честным —
                # запись на диск в него не попадает.
                дамп = dump_request(body, сессия, dict(
                    прунинг, path=self.path, upstream="local", stripped=stripped))
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
                if дамп is not None:
                    дамп.записать()  # ответа не было — мета уходит без usage и замера
                return

        self.send_response(resp.status, resp.reason)
        for k, v in resp.getheaders():
            if k.lower() not in HOP:
                self.send_header(k, v)
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        # Копилка usage для карточки. Своя, а не из дампа: дамп включают на время
        # разбирательства, а карточка работает всегда, и делить одно состояние на две ручки с
        # разным сроком жизни значит потерять цифры кэша, как только дамп выключат.
        usage_карточки, prefill = {}, None
        try:
            if первый:  # кусок, прочитанный ради fail-closed, отдаём первым
                prefill = time.monotonic() - начало
                merge_usage(usage_карточки, первый)
                self.wfile.write(b"%x\r\n%s\r\n" % (len(первый), первый))
                self.wfile.flush()
            while True:
                chunk = resp.read1(65536)
                if not chunk:
                    break
                if дамп is not None:
                    дамп.первый_байт(chunk, начало)  # второй раз метод ничего не делает
                    дамп.усвоить(chunk)
                if prefill is None:  # первый байт ответа: замер того, что человек ждал
                    prefill = time.monotonic() - начало
                merge_usage(usage_карточки, chunk)
                self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass  # клиент ушёл (прерывание хода) — upstream закроется вместе с conn
        finally:
            if дамп is not None:
                дамп.записать()  # оборванный ход: мета без usage, зато тело в дампе осталось
            # Карточка уходит ПО КОНЦУ ответа, а не по первому байту: цифры кэша роутер vLLM
            # кладёт в финальный `message_delta`, и на первом байте карточка несла бы
            # `cacheReadTokens: null` на каждом сдвиге (живой прогон 2026-09-23). Замер
            # prefill при этом снят по первому байту и от переноса не пострадал.
            #
            # В finally, а не после цикла: оборванный ход (человек нажал «Стоп») — это тоже
            # ход, за который он заплатил ожиданием, и карточка о нём должна остаться. Цифры
            # кэша в этом случае уйдут те, что успели прийти.
            if событие is not None:
                self._отправить_событие(событие, prefill, usage_карточки)
                событие = None
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
             f"{PRUNE_STEP_TOKENS // 1000}k ток, гейт контекста "
             f"{PRUNE_MIN_CONTEXT_TOKENS // 1000}k)"
             if PRUNE_MODE == "on" else ", прунинг выводов выкл")
          + (f", автосжатие -> {COMPACT_MODEL} на {COMPACT_UPSTREAM} (порог "
             f"{COMPACT_MIN_CONTEXT_TOKENS // 1000}k ток)" if COMPACT_UPSTREAM
             else ", автосжатие в облако выкл")
          + (f", карточка сдвига границы -> {BACKEND_EVENTS_URL}" if BACKEND_EVENTS_URL
             else ", карточка сдвига границы выкл"), flush=True)
    prepare_dump_dir()  # сообщит в журнал, включён дамп или каталог не годится
    srv.serve_forever()
