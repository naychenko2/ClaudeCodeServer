#!/usr/bin/env python3
"""Протокол замера хода по транскрипту claude CLI: паузы, сжатия, стоп-мир, мерка контекста.

Один прогон агента = один файл `*.jsonl` в профиле CLI
(`data/claude-profiles/{провайдер}/projects/{проект}/{sessionId}.jsonl`). Скрипт считает по нему
то, на чём стоят критерии приёмки мер из плана «снизить цену автосжатия локальной модели»:

* **паузы между шагами** (p50/p90/макс) — сколько ход молчит перед каждым ответом модели;
* **сжатия**: сколько их, `compactMetadata.preTokens/postTokens`, длительность каждого
  стоп-мира (и по `durationMs` самого CLI, и по расстоянию между соседними записями);
* **размер пост-компактного остатка** — `postTokens` плюс фактическая занятость окна в
  первом запросе после границы (по `usage` ответа API);
* **суммарное время прогона** и доля, которую в нём занимают сжатия;
* **сторож формулы порога CLI**: расчётный порог против фактического `preTokens`. Формула
  недокументированная (`min(окно, CLAUDE_CODE_AUTO_COMPACT_WINDOW) − min(лимит вывода, 20000)
  − 13000`, версия CLI 2.1.276) — обновление CLI может сменить её молча, и тогда все меры,
  завязанные на порог, перестают работать без единой ошибки в логах.

Опционально (`--tokenize`, нужен живой стенд) — сверка грубой мерки «символы ÷ 4», на которой
стоят пороги прокси, с токенизатором самой модели.

Wall-clock всего прогона как критерий приёмки НЕ годится (доля сжатий в нём ≈ 10 %, разброс
одного сжатия больше эффекта) — сравнивать надо суммарное время сжатий и p90/макс паузы.

Примеры:
    python3 tools/local-llm-measure/measure_turns.py <транскрипт.jsonl>
    python3 tools/local-llm-measure/measure_turns.py --declared-window 253440 --max-output 8192 \\
        --tokenize --json out.json <транскрипт.jsonl>
"""
import argparse
import json
import os
import sys
import urllib.request
from datetime import datetime, timezone

BASE = os.environ.get("VLLM_BASE", "http://127.0.0.1:18020")
MODEL = os.environ.get("VLLM_MODEL", "qwen3.8-27b")
# Константы порога из бинарника CLI 2.1.276 (см. докстроку): вычет сводки и потолок резерва.
COMPACT_BUFFER = 13000
OUTPUT_RESERVE_CAP = 20000
# Нижний и верхний клампы значения CLAUDE_CODE_AUTO_COMPACT_WINDOW там же.
AUTO_WINDOW_MIN = 100000
AUTO_WINDOW_MAX = 1000000


def parse_ts(value):
    if not value:
        return None
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00")).astimezone(timezone.utc)
    except ValueError:
        return None


def load(path):
    rows = []
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except ValueError:
            continue
    return rows


def context_tokens(usage):
    """Занятость окна в запросе: то, что реально ушло в модель (кэш читается, но местом занят)."""
    if not isinstance(usage, dict):
        return None
    return (usage.get("input_tokens") or 0) + (usage.get("cache_read_input_tokens") or 0) \
        + (usage.get("cache_creation_input_tokens") or 0)


def occupancy(events):
    """Занятость окна по ходу прогона — по одному числу на ОТВЕТ API, а не на запись.

    Один ответ CLI пишет несколькими записями `assistant` (блоки), и у всех у них одинаковый
    `usage`: без дедупликации по `message.id` прирост между соседями оказывается нулевым.
    """
    seen, out = set(), []
    for _, row in events:
        if row.get("type") != "assistant":
            continue
        msg = row.get("message") or {}
        mid = msg.get("id")
        if mid in seen:
            continue
        seen.add(mid)
        ctx = context_tokens(msg.get("usage"))
        if ctx:
            out.append(ctx)
    return out


def percentile(values, p):
    if not values:
        return 0.0
    ordered = sorted(values)
    k = (len(ordered) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(ordered) - 1)
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (k - lo)


def timeline(rows, include_sidechains):
    """Записи с временем, по возрастанию: основа и пауз, и длительности стоп-мира."""
    out = []
    for r in rows:
        if not include_sidechains and r.get("isSidechain"):
            continue
        ts = parse_ts(r.get("timestamp"))
        if ts is None:
            continue
        out.append((ts, r))
    out.sort(key=lambda x: x[0])
    return out


def gaps(events):
    """Пауза шага — молчание перед НАЧАЛОМ ответа модели (первый блок ответа после чужой записи).

    Ответ CLI пишет несколькими записями `assistant` подряд (блоки одного ответа), поэтому
    дельты внутри ответа паузой шага не считаются: это уже генерация, а не ожидание.
    """
    result = []
    prev_ts, prev_row = None, None
    for ts, row in events:
        if prev_ts is not None and row.get("type") == "assistant" \
                and (prev_row or {}).get("type") != "assistant":
            result.append({
                "seconds": (ts - prev_ts).total_seconds(),
                "at": ts.isoformat(),
                "after": (prev_row or {}).get("type"),
            })
        prev_ts, prev_row = ts, row
    return result


def compactions(events):
    """Сжатия: метаданные CLI, стоп-мир по времени и прыжок контекста на последнем шаге.

    Стоп-мир по меткам — расстояние от ПОСЛЕДНЕГО ответа модели до записи границы: всё
    остальное вокруг границы (новый промпт, вложения) CLI пишет уже после сжатия, одним и тем
    же временем, и по соседним записям стоп-мир вышел бы нулевым. Сверка с `durationMs` самого
    CLI сходится до секунды (354.1 против 354.0; 453.5 против 450.6 на транскриптах 22.09) —
    она и нужна: `durationMs` есть не во всех версиях формата, метки есть всегда.

    Занятость двух последних ответов нужна сторожу формулы. Порог CLI проверяет МЕЖДУ шагами
    и по своей копии истории, поэтому `preTokens` законно оказывается выше расчётного порога
    (у прогонов 22.09 — на 25–30k: это невидимый в `usage` вывод последнего инструмента, а
    однажды и +161k одним жирным `Read`). Признаков смены формулы два, и оба однозначные:
    сжатие РАНЬШЕ расчётного порога, либо пропущенная возможность сжать — предпоследний ответ
    уже был выше порога, а CLI молчал.
    """
    out = []
    for i, (ts, row) in enumerate(events):
        meta = row.get("compactMetadata")
        if row.get("type") != "system" or row.get("subtype") != "compact_boundary" or not meta:
            continue
        last_answer = next((t for t, r in reversed(events[:i]) if r.get("type") == "assistant"),
                           None)
        # остаток после сжатия: postTokens самого CLI и первая фактическая занятость окна
        first_ctx = None
        for _, nxt in events[i + 1:]:
            if nxt.get("type") == "assistant":
                first_ctx = context_tokens((nxt.get("message") or {}).get("usage"))
                if first_ctx:
                    break
        before_ctx = occupancy(events[:i])
        out.append({
            "at": ts.isoformat(),
            "trigger": meta.get("trigger"),
            "pre_tokens": meta.get("preTokens"),
            "post_tokens": meta.get("postTokens"),
            "duration_seconds_cli": round((meta.get("durationMs") or 0) / 1000, 1),
            "stop_world_seconds_by_timestamps":
                round((ts - last_answer).total_seconds(), 1) if last_answer else None,
            "context_before_compact": before_ctx[-1] if before_ctx else None,
            "context_two_steps_before": before_ctx[-2] if len(before_ctx) >= 2 else None,
            "context_after_compact": first_ctx,
        })
    return out


def expected_threshold(declared_window, max_output, auto_compact_window):
    """Порог автосжатия по формуле CLI 2.1.276 — вход сторожа расхождения."""
    window = declared_window
    if auto_compact_window:
        clamped = min(max(auto_compact_window, AUTO_WINDOW_MIN), AUTO_WINDOW_MAX)
        window = min(window, clamped)
    return window - min(max_output, OUTPUT_RESERVE_CAP) - COMPACT_BUFFER


def history_chars(rows):
    """Символы истории — та самая величина, которую прокси делит на 4, оценивая контекст."""
    total = 0
    for r in rows:
        if r.get("type") not in ("user", "assistant"):
            continue
        msg = r.get("message")
        if msg is None:
            continue
        total += len(json.dumps(msg.get("content"), ensure_ascii=False))
    return total


def history_text(rows, limit_chars):
    parts, total = [], 0
    for r in rows:
        if r.get("type") not in ("user", "assistant"):
            continue
        msg = r.get("message")
        if msg is None:
            continue
        chunk = json.dumps(msg.get("content"), ensure_ascii=False)
        if total + len(chunk) > limit_chars:
            break
        parts.append(chunk)
        total += len(chunk)
    return "\n".join(parts)


def tokenize(text):
    req = urllib.request.Request(
        BASE + "/tokenize", data=json.dumps({"model": MODEL, "prompt": text}).encode("utf-8"),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))["count"]


def report(path, args):
    rows = load(path)
    events = timeline(rows, args.include_sidechains)
    if not events:
        print(f"{path}: нет записей со временем", file=sys.stderr)
        return None

    pauses = [g["seconds"] for g in gaps(events)]
    comps = compactions(events)
    started, finished = events[0][0], events[-1][0]
    wall = (finished - started).total_seconds()
    compact_total = sum(c["duration_seconds_cli"] for c in comps)
    ctx_peak = max((context_tokens((r.get("message") or {}).get("usage")) or 0
                    for _, r in events if r.get("type") == "assistant"), default=0)
    threshold = expected_threshold(args.declared_window, args.max_output, args.auto_compact_window)

    result = {
        "file": os.path.abspath(path),
        "messages": len(rows),
        "steps": len(pauses),
        "wall_seconds": round(wall, 1),
        "pause_p50": round(percentile(pauses, 0.5), 1),
        "pause_p90": round(percentile(pauses, 0.9), 1),
        "pause_max": round(max(pauses), 1) if pauses else 0.0,
        "pause_sum": round(sum(pauses), 1),
        "context_peak_tokens": ctx_peak,
        "compactions": comps,
        "compact_total_seconds": round(compact_total, 1),
        "compact_share_of_wall": round(compact_total / wall * 100, 1) if wall else 0.0,
        "expected_threshold": threshold,
        "history_chars": history_chars(rows),
    }

    print(f"== {os.path.basename(path)}")
    print(f"  записей {result['messages']}, шагов {result['steps']}, "
          f"прогон {wall / 60:.1f} мин ({started:%H:%M:%S} → {finished:%H:%M:%S} UTC)")
    print(f"  паузы шага: p50 {result['pause_p50']:.1f} с, p90 {result['pause_p90']:.1f} с, "
          f"макс {result['pause_max']:.1f} с, сумма {result['pause_sum'] / 60:.1f} мин")
    print(f"  пиковая занятость окна: {ctx_peak} ток "
          f"(расчётный порог сжатия {threshold})")
    if not comps:
        print("  сжатий нет")
    for c in comps:
        delta = (c["pre_tokens"] or 0) - threshold
        pct = delta / threshold * 100 if threshold else 0
        slack = threshold * args.drift_tolerance / 100
        prev_ctx = c["context_two_steps_before"]
        early = delta < -slack                                  # сжали раньше расчёта
        late = prev_ctx is not None and prev_ctx > threshold + slack  # прозевали возможность
        drift = early or late
        print(f"  сжатие {c['at'][11:19]} ({c['trigger']}): "
              f"{c['pre_tokens']} → {c['post_tokens']} ток, стоп-мир "
              f"{c['duration_seconds_cli']:.0f} с (по меткам "
              f"{c['stop_world_seconds_by_timestamps']} с), "
              f"остаток в следующем запросе {c['context_after_compact']} ток")
        reason = ("сжатие РАНЬШЕ расчётного порога" if early
                  else "порог был пройден ещё шагом раньше, а CLI молчал" if late
                  else "в норме")
        print(f"    сторож формулы: preTokens против расчётного порога {delta:+d} ток "
              f"({pct:+.1f} %), занятость двух последних ответов "
              f"{prev_ctx} → {c['context_before_compact']} — {reason}"
              + ("  РАСХОЖДЕНИЕ, проверь формулу порога CLI" if drift else ""))
        c["threshold_drift"] = drift
    if comps:
        print(f"  сжатий {len(comps)}, суммарно {compact_total / 60:.1f} мин "
              f"({result['compact_share_of_wall']:.1f} % прогона)")

    if args.tokenize:
        text = history_text(rows, args.tokenize_limit)
        exact = tokenize(text)
        rough = len(text) // 4
        result["tokenize"] = {
            "chars": len(text), "tokens": exact, "rough_div4": rough,
            "chars_per_token": round(len(text) / exact, 3) if exact else None,
            "rough_of_exact_percent": round(rough / exact * 100, 1) if exact else None,
        }
        print(f"  мерка: {len(text)} симв = {exact} ток по токенизатору "
              f"({len(text) / exact:.2f} симв/ток); «÷ 4» даёт {rough} — "
              f"{rough / exact * 100:.1f} % от факта")
    return result


def main():
    ap = argparse.ArgumentParser(description="Разбор транскрипта CLI: паузы, сжатия, мерка")
    ap.add_argument("transcripts", nargs="+", help="файлы *.jsonl профиля CLI")
    ap.add_argument("--declared-window", type=int, default=253440,
                    help="окно, объявленное CLI (CLAUDE_CODE_MAX_CONTEXT_TOKENS)")
    ap.add_argument("--max-output", type=int, default=8192,
                    help="CLAUDE_CODE_MAX_OUTPUT_TOKENS прогона")
    ap.add_argument("--auto-compact-window", type=int, default=0,
                    help="CLAUDE_CODE_AUTO_COMPACT_WINDOW прогона (0 — не задавалась)")
    ap.add_argument("--drift-tolerance", type=float, default=2.0,
                    help="допустимое расхождение preTokens с расчётным порогом, %%")
    ap.add_argument("--include-sidechains", action="store_true",
                    help="учитывать ходы сабагентов (по умолчанию только основная ветка)")
    ap.add_argument("--tokenize", action="store_true",
                    help="сверить мерку «символы ÷ 4» с токенизатором стенда")
    ap.add_argument("--tokenize-limit", type=int, default=700000,
                    help="сколько символов истории отдать токенизатору")
    ap.add_argument("--json", help="куда сложить результат машинно")
    args = ap.parse_args()

    results = [r for r in (report(p, args) for p in args.transcripts) if r]
    if args.json:
        json.dump(results, open(args.json, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"\nмашинный вывод: {args.json}")
    return 0 if results else 1


if __name__ == "__main__":
    sys.exit(main())
