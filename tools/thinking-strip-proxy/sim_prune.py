#!/usr/bin/env python3
"""Симулятор прунинга: проигрывает транскрипт CLI ход за ходом и считает цену схемы.

Зачем отдельно от `measure_prune.py`: тот меряет ЖИВОЙ стенд (время ответа, prefix cache
движка) и занимает его на десятки минут, а вопрос «сколько сдвигов границы даст схема и
успеет ли она до автосжатия CLI» решается арифметикой по телу. Симулятор отвечает на него за
секунды, на любом числе транскриптов и до того, как новая схема поедет в бой.

Что считает на каждом ходу: оценку контекста прокси, решение `prune_tool_results` (сколько
блоков, сколько освободили) и `usage` — промпт, который увидел бы CLI. Итог прогона: число
СДВИГОВ границы (каждый = полный пересчёт префикса, 56–90 с живьём), момент первого
срабатывания и максимальный `usage` — дошёл ли он до порога автосжатия CLI.

Системной части в транскрипте нет (CLI собирает её при запуске), поэтому её размер задаётся
ключом: 75229 символов — голый CLI, 295111 — боевой тулсет с MCP (замеры 2026-09-22).

    python3 sim_prune.py <транскрипт.jsonl> [--система 295111] [--гейт 190000] [--ступень 121000]
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import proxy  # noqa: E402

# Порог автосжатия CLI при незаданной CLAUDE_CODE_AUTO_COMPACT_WINDOW.
CLI_COMPACT_TOKENS = 232_000


def загрузить(path):
    """Сообщения из транскрипта CLI в форме тела запроса (как это делает measure_prune)."""
    msgs = []
    for line in open(path, encoding="utf-8"):
        try:
            r = json.loads(line)
        except ValueError:
            continue
        if r.get("type") not in ("user", "assistant"):
            continue
        m = r.get("message") or {}
        role, content = m.get("role"), m.get("content")
        if role not in ("user", "assistant") or content is None:
            continue
        if isinstance(content, list):
            blocks = [b for b in content if isinstance(b, dict)
                      and b.get("type") in ("text", "tool_use", "tool_result", "thinking")]
            if not blocks:
                continue
            content = blocks
        elif not str(content).strip():
            continue
        if msgs and msgs[-1]["role"] == role and isinstance(msgs[-1]["content"], list) \
                and isinstance(content, list):
            msgs[-1]["content"] += content  # CLI шлёт подряд идущие блоки одной ролью
            continue
        msgs.append({"role": role, "content": content})
    return msgs


def прогон(msgs, система, ручки):
    """Ход за ходом: (номер, ctx, блоков, usage). Ход — каждое сообщение пользователя."""
    шаги = []
    for i, m in enumerate(msgs):
        if m.get("role") != "user":
            continue
        тело = msgs[:i + 1]
        ctx = proxy.estimate_tokens(proxy._context_chars(тело), система,
                                    proxy._signature_chars(тело))
        _, freed, blocks = proxy.prune_tool_results(тело, extra_chars=система, **ручки)
        шаги.append((len(шаги) + 1, ctx, blocks, ctx - proxy.estimate_tokens(freed)))
    return шаги


def отчёт(имя, шаги):
    сдвиги = [ш for k, ш in enumerate(шаги) if ш[2] != (шаги[k - 1][2] if k else 0)]
    сжатие = next((ш for ш in шаги if ш[3] >= CLI_COMPACT_TOKENS), None)
    первый = сдвиги[0] if сдвиги else None
    print(f"{имя}: ходов {len(шаги)}, сдвигов границы {len(сдвиги)}"
          + (f", первый при ctx {первый[1] // 1000}k (ход {первый[0]}, {первый[2]} блоков)"
             if первый else ", прунинг не сработал ни разу")
          + f", максимум usage {max((ш[3] for ш in шаги), default=0) // 1000}k"
          + (f", АВТОСЖАТИЕ CLI на ходу {сжатие[0]} (ctx {сжатие[1] // 1000}k)" if сжатие
             else ", автосжатие CLI не наступило"))
    if сдвиги:
        print("   сдвиги: " + ", ".join(f"ход {ш[0]} ctx {ш[1] // 1000}k → {ш[2]} бл, "
                                        f"usage {ш[3] // 1000}k" for ш in сдвиги))
    return len(сдвиги), (первый[1] if первый else None), max((ш[3] for ш in шаги), default=0), сжатие


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("транскрипты", nargs="+")
    p.add_argument("--система", type=int, default=295_111, help="символов системной части")
    p.add_argument("--гейт", type=int, default=proxy.PRUNE_MIN_CONTEXT_TOKENS)
    p.add_argument("--ступень", type=int, default=proxy.PRUNE_STEP_TOKENS)
    p.add_argument("--хвост", type=int, default=proxy.PRUNE_KEEP_TAIL_TOKENS)
    p.add_argument("--первая", type=int, default=None, help="первая ступень (старая схема)")
    p.add_argument("--доли", type=int, default=None, help="на сколько долей дробить цель в дефиците")
    а = p.parse_args()
    if а.доли is not None:
        proxy.PRUNE_DEFICIT_PARTS = а.доли
    ручки = dict(keep_tail_tokens=а.хвост, step_tokens=а.ступень,
                 min_context_tokens=а.гейт, prune_inputs=True, prune_thinking=True)
    if а.первая is not None:
        ручки["first_step_tokens"] = а.первая
    print(f"системная часть {а.система} симв, гейт {а.гейт // 1000}k, ступень "
          f"{а.ступень // 1000}k, хвост {а.хвост // 1000}k"
          + (f", первая ступень {а.первая // 1000}k" if а.первая is not None else ""))
    for путь in а.транскрипты:
        отчёт(os.path.basename(путь)[:8], прогон(загрузить(путь), а.система, ручки))
