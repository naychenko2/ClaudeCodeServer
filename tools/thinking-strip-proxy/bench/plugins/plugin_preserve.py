"""Схема `preserve`: новая схема сжатия прокси при COMPACT_PRESERVE=on (шаги 3–5).

Сборка — как в proxy.py (`preserve_for_compact`, `ВставкаБлока`), compactor.py берётся из
worktree фичи (`COMPACTOR_DIR`), копий кода нет:
  1. блок = compactor.build_preserved(история БЕЗ инструкции сжатия) — в стенде инструкция
     добавляется в запрос отдельно, поэтому блок считается по `item["history"]` напрямую;
  2. к тексту инструкции сжатия дописывается "\\n\\n" + compactor.instruction_addendum();
  3. запрос уходит в MiniMax-M3 (как схема old), ответ модели + "\\n\\n" + блок — в конец текста
     со сводкой (после </summary>); дальше run_bench разбирает как CLI.
"""
import os
import subprocess
import sys

BENCH_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROXY_DIR = os.environ.get(
    "COMPACTOR_DIR", "/home/an/Sources/ClaudeCodeServer-compactor-preserve/tools/thinking-strip-proxy")
sys.path.insert(0, PROXY_DIR)
sys.path.insert(0, BENCH_DIR)
import compactor  # noqa: E402
import common  # noqa: E402


def _rev():
    try:
        return subprocess.run(["git", "-C", PROXY_DIR, "rev-parse", "--short", "HEAD"],
                              capture_output=True, text=True, timeout=10).stdout.strip()
    except Exception:  # noqa: BLE001
        return ""


REV = _rev()


def request_messages(item, addendum):
    """История + инструкция сжатия (с дополнением в тот же текстовый блок) — как после правки прокси."""
    msgs = [{"role": m["role"], "content": [dict(b) for b in m["content"]]} for m in item["history"]]
    ins = {"type": "text",
           "text": common.compact_instruction(item.get("variant", "up_to")) + "\n\n" + addendum}
    if msgs and msgs[-1]["role"] == "user":
        msgs[-1]["content"].append(ins)
    else:
        msgs.append({"role": "user", "content": [ins]})
    return msgs


def summarize(item):
    block = compactor.build_preserved({"messages": item["history"]})
    if block is None:
        addendum, tail = "", ""
        msgs = request_messages(item, "").copy()
    else:
        addendum, tail = compactor.instruction_addendum(), "\n\n" + block
        msgs = request_messages(item, addendum)
    raw, usage = common.call_messages(
        common.minimax_endpoint(), item["system"], msgs,
        max_tokens=common.COMPACT_MAX_TOKENS, timeout=1500)
    return raw + tail, {"in_tokens": (usage.get("input_tokens") or 0) + (usage.get("cache_read_input_tokens") or 0),
                        "out_tokens": usage.get("output_tokens"), "block_chars": len(block or ""),
                        "model_chars": len(raw), "compactor_rev": REV}
