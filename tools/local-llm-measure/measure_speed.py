#!/usr/bin/env python3
"""Кривая скорости локальной модели по ЗАНЯТОСТИ контекста: prefill и decode на нескольких уровнях.

Зачем: выгода раннего автосжатия (кроме длины самой паузы) держится на гипотезе «decode
деградирует с ростом занятого контекста». Гипотеза проверяется здесь и только здесь —
цифры 85/50-70/34 ток/с из appsettings.json описывают ПАРАЛЛЕЛЬНЫХ агентов, а не занятость окна.

Метод на каждый уровень N токенов:
  1) холодный запрос (соль в начале промпта делает префикс уникальным) с max_tokens=1 —
     это чистый prefill, decode в него почти не входит; prefill = prompt_tokens / t;
  2) тот же самый промпт повторно с max_tokens=DECODE_TOKENS — префикс теперь в prefix cache,
     поэтому время до первого токена мало, а вся остальная длительность — генерация ПРИ
     занятом контексте N; decode = (вышло токенов − 1) / (общее время − ttft).

Второй шаг обязан идти сразу за первым: prefix cache у стенда вмещает 1.17 контекста, и
чужой большой запрос между ними вытеснит префикс (видно по ttft — он тогда сравним с prefill).

Запуск (из корня репозитория):
    python3 tools/local-llm-measure/measure_speed.py 60000 140000 230000
    python3 tools/local-llm-measure/measure_speed.py --json out.json 60000 140000

Требует ЖИВОГО стенда на :18020 и отсутствия параллельной нагрузки: замер идёт в общую
очередь движка (max_num_seqs=4), соседний ход агента исказит обе цифры.
"""
import argparse
import glob
import json
import os
import random
import sys
import threading
import time
import urllib.request

BASE = os.environ.get("VLLM_BASE", "http://127.0.0.1:18020")
MODEL = os.environ.get("VLLM_MODEL", "qwen3.8-27b")
DECODE_TOKENS = int(os.environ.get("DECODE_TOKENS", "256"))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def post(path, payload, timeout=1200):
    req = urllib.request.Request(
        BASE + path, data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def engine_stats():
    """Счётчики движка: сколько запросов крутится и сколько токенов он уже посчитал.

    Оба признака нужны вместе. Счётчики токенов движок обновляет по ЗАВЕРШЕНИИ запроса,
    поэтому чужой длинный ход (а на 230k контекста один запрос идёт минуты) выглядит по ним
    полной тишиной — при 99 % занятости обеих карт. `num_requests_running` же показывает и
    такой запрос, но сам по себе не говорит, движется ли работа.
    """
    try:
        with urllib.request.urlopen(BASE + "/metrics", timeout=10) as r:
            text = r.read().decode("utf-8", "replace")
    except OSError:
        return None
    got = {"running": None, "waiting": None, "prompt": 0.0, "generation": 0.0}
    for line in text.splitlines():
        if line.startswith("vllm:num_requests_running{"):
            got["running"] = float(line.rsplit(" ", 1)[1])
        elif line.startswith("vllm:num_requests_waiting{"):
            got["waiting"] = float(line.rsplit(" ", 1)[1])
        elif line.startswith("vllm:prompt_tokens_total{"):
            got["prompt"] = float(line.rsplit(" ", 1)[1])
        elif line.startswith("vllm:generation_tokens_total{"):
            got["generation"] = float(line.rsplit(" ", 1)[1])
    return got


def work_done(stats):
    return None if stats is None else (stats["prompt"], stats["generation"])


def wait_idle(timeout=900, window=6):
    """Ждём тишины на стенде: соседний ход агента занижает и prefill, и decode.

    Тишина = нет крутящихся запросов И счётчики токенов за окно не сдвинулись. Продуктовый
    стенд общий, поэтому тишины может не быть вовсе — по истечении таймаута мерим как есть,
    а прогон помечается признаком чужой нагрузки (см. LoadWatch).
    """
    t0 = time.time()
    warned = False
    while time.time() - t0 < timeout:
        stats = engine_stats()
        if stats is None:
            return True
        before = work_done(stats)
        time.sleep(window)
        after = engine_stats()
        if after is None:
            return True
        if not after["running"] and work_done(after) == before:
            return True
        if not warned:
            print("  ждём тишины на стенде: движок занят чужим запросом", flush=True)
            warned = True
    print("  ВНИМАНИЕ: стенд так и не освободился, цифры прогона считать грязными", flush=True)
    return False


class LoadWatch:
    """Следит за чужой нагрузкой ВО ВРЕМЯ запроса: наш запрос — один, всё сверх него чужое."""

    def __init__(self):
        stats = engine_stats()
        self.baseline = (stats or {}).get("running") or 0.0
        self.peak = self.baseline
        self._stop = threading.Event()
        self._t = threading.Thread(target=self._loop, daemon=True)

    def _loop(self):
        while not self._stop.wait(2):
            stats = engine_stats()
            if stats and stats["running"] is not None:
                self.peak = max(self.peak, stats["running"])

    def __enter__(self):
        self._t.start()
        return self

    def __exit__(self, *_):
        self._stop.set()
        self._t.join(timeout=5)
        return False

    @property
    def contended(self):
        # базовая линия снята ДО нашего запроса, наш добавляет ровно единицу
        return self.peak > self.baseline + 1


def count_tokens(text):
    """Точная мерка стенда: сколько токенов в этом тексте по токенизатору самой модели."""
    return post("/tokenize", {"model": MODEL, "prompt": text})["count"]


def filler_source():
    """Наполнитель — реальный код и доки репозитория: смесь, похожая на историю агента."""
    parts = []
    pats = ["backend/**/*.cs", "frontend/src/**/*.tsx", "docs/**/*.md", "tools/**/*.py"]
    for pat in pats:
        for p in sorted(glob.glob(os.path.join(REPO, pat), recursive=True)):
            if "/bin/" in p or "/obj/" in p or "/node_modules/" in p:
                continue
            try:
                parts.append(open(p, encoding="utf-8", errors="replace").read())
            except OSError:
                continue
            if sum(len(x) for x in parts) > 4_000_000:
                return "\n\n".join(parts)
    return "\n\n".join(parts)


def build_prompt(source, target_tokens, salt):
    """Подгоняем длину промпта под целевое число токенов (сверка — токенизатором стенда)."""
    probe = source[:20000]
    ratio = len(probe) / max(1, count_tokens(probe))  # символов на токен
    text = ("[" + salt + "] ") + source[: int(target_tokens * ratio)]
    got = count_tokens(text)
    # одна поправка по факту: у кода и русского текста плотность разная
    if got and abs(got - target_tokens) > target_tokens * 0.02:
        text = ("[" + salt + "] ") + source[: int(len(text) * target_tokens / got)]
        got = count_tokens(text)
    return text, got


def stream_chat(prompt, max_tokens):
    """Возвращает (ttft, общее время, prompt_tokens, completion_tokens)."""
    payload = {
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream": True,
        "stream_options": {"include_usage": True},
    }
    req = urllib.request.Request(
        BASE + "/v1/chat/completions", data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"})
    t0 = time.time()
    ttft = None
    usage = {}
    with urllib.request.urlopen(req, timeout=1800) as r:
        for raw in r:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            body = line[5:].strip()
            if body == "[DONE]":
                break
            try:
                chunk = json.loads(body)
            except ValueError:
                continue
            if chunk.get("usage"):
                usage = chunk["usage"]
            ch = (chunk.get("choices") or [{}])[0]
            delta = (ch.get("delta") or {}).get("content") or ""
            if delta and ttft is None:
                ttft = time.time() - t0
    total = time.time() - t0
    return ttft, total, usage.get("prompt_tokens"), usage.get("completion_tokens")


def measure_level(source, target, salt):
    prompt, tok = build_prompt(source, target, salt)
    print(f"уровень ~{target} ток: промпт {len(prompt)} симв = {tok} ток "
          f"({len(prompt) / tok:.2f} симв/ток)", flush=True)

    wait_idle()
    with LoadWatch() as w1:
        _, t_prefill, ptok, _ = stream_chat(prompt, 1)
    ptok = ptok or tok
    prefill_rate = ptok / t_prefill
    print(f"  prefill (холодный): {t_prefill:7.1f} с на {ptok} ток → "
          f"{prefill_rate:6.0f} ток/с"
          + ("  [ЧУЖАЯ НАГРУЗКА]" if w1.contended else ""), flush=True)

    with LoadWatch() as w2:
        ttft, total, ptok2, ctok = stream_chat(prompt, DECODE_TOKENS)
    ctok = ctok or DECODE_TOKENS
    decode_rate = (ctok - 1) / max(0.001, total - (ttft or 0))
    print(f"  decode при {ptok2 or ptok} занятых: ttft {ttft:5.2f} с (кэш "
          f"{'попал' if (ttft or 99) < t_prefill / 5 else 'МИМО — цифра испорчена'}), "
          f"{ctok} ток за {total - (ttft or 0):5.1f} с → {decode_rate:5.1f} ток/с"
          + ("  [ЧУЖАЯ НАГРУЗКА]" if w2.contended else ""), flush=True)

    return {
        "target_tokens": target,
        "prompt_chars": len(prompt),
        "prompt_tokens": ptok,
        "chars_per_token": round(len(prompt) / tok, 3),
        "prefill_seconds": round(t_prefill, 2),
        "prefill_tokens_per_second": round(prefill_rate, 1),
        "prefill_contended": w1.contended,
        "decode_ttft_seconds": round(ttft or 0, 2),
        "decode_tokens": ctok,
        "decode_tokens_per_second": round(decode_rate, 2),
        "decode_cache_hit": bool((ttft or 99) < t_prefill / 5),
        "decode_contended": w2.contended,
    }


def main():
    ap = argparse.ArgumentParser(description="Кривая prefill/decode по занятости контекста")
    ap.add_argument("levels", nargs="*", type=int, default=[60000, 140000, 230000],
                    help="уровни занятости контекста в токенах")
    ap.add_argument("--json", help="куда сложить результат машинно")
    ap.add_argument("--repeat", type=int, default=1, help="повторов каждого уровня")
    args = ap.parse_args()

    source = filler_source()
    salt = f"measure-speed-{int(time.time())}-{random.randint(1000, 9999)}"
    rows = []
    for rep in range(args.repeat):
        for lv in args.levels:
            row = measure_level(source, lv, f"{salt}-r{rep}-{lv}")
            row["repeat"] = rep
            rows.append(row)

    print("\n| занято, ток | prefill, ток/с | decode, ток/с | чужая нагрузка |")
    print("|---|---|---|---|")
    for r in rows:
        dirty = "да" if (r["prefill_contended"] or r["decode_contended"]) else "нет"
        print(f"| {r['prompt_tokens']} | {r['prefill_tokens_per_second']:.0f} | "
              f"{r['decode_tokens_per_second']:.1f} | {dirty} |")
    if args.json:
        json.dump({"salt": salt, "model": MODEL, "rows": rows},
                  open(args.json, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"\nмашинный вывод: {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
