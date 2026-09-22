#!/usr/bin/env python3
"""Замер прунинга: проигрываем реальный транскрипт как шаги агента и смотрим prefix cache vLLM.

Каждый «шаг» — запрос с историей, наросшей ещё на одну пару tool_use/tool_result, ровно как
это делает CLI. Считаем по /metrics движка hits/queries за прогон и время до ответа.
Соль в первом сообщении делает каждый прогон холодным для кэша — иначе второй прогон
паразитирует на блоках первого.
"""
import json, sys, time, urllib.request

TRANSCRIPT = sys.argv[1]
LABEL = sys.argv[2]
SALT = sys.argv[3]
STEPS = int(sys.argv[4]) if len(sys.argv) > 4 else 12
PORT = sys.argv[5] if len(sys.argv) > 5 else "18021"
MAX_CHARS = 700_000  # ~175k токенов: вся история чата в окно 262k не влезает
PROXY = f"http://127.0.0.1:{PORT}/v1/messages"
METRICS = "http://127.0.0.1:18020/metrics"


def load_messages(path):
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
                      and b.get("type") in ("text", "tool_use", "tool_result")]
            blocks = [b for b in blocks if b.get("type") != "text" or (b.get("text") or "").strip()]
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


def normalize_block(b):
    """Транскрипт CLI хранит блоки со своими полями (caller и пр.) — vLLM их отвергает 400."""
    t = b.get("type")
    if t == "text":
        return {"type": "text", "text": b.get("text") or ""}
    if t == "tool_use":
        return {"type": "tool_use", "id": b["id"], "name": b.get("name") or "tool",
                "input": b.get("input") if isinstance(b.get("input"), dict) else {}}
    if t == "tool_result":
        c = b.get("content")
        if isinstance(c, list):
            c = "\n".join(x.get("text") or "" for x in c if isinstance(x, dict) and x.get("type") == "text")
        return {"type": "tool_result", "tool_use_id": b["tool_use_id"],
                "content": c if isinstance(c, str) else ""}
    return None


def sanitize(msgs):
    """Оставляем только пары tool_use → tool_result: без парного блока vLLM отвергает тело."""
    out, pending = [], set()
    for m in msgs:
        c = m["content"]
        if isinstance(c, list):
            c = [x for x in (normalize_block(b) for b in c) if x]
            if not c:
                continue
            m = {"role": m["role"], "content": c}
            if m["role"] == "assistant":
                pending = {b["id"] for b in c if b.get("type") == "tool_use"}
            else:
                c = [b for b in c if b.get("type") != "tool_result" or b.get("tool_use_id") in pending]
                if not c:
                    continue
                m = {"role": "user", "content": c}
        out.append(m)
    return out


def metrics():
    text = urllib.request.urlopen(METRICS, timeout=10).read().decode()
    got = {}
    for line in text.splitlines():
        for key in ("vllm:prefix_cache_queries_total", "vllm:prefix_cache_hits_total"):
            if line.startswith(key + "{"):
                got[key.split(":")[1]] = float(line.rsplit(" ", 1)[1])
    return got


def ask(msgs):
    body = json.dumps({"model": "qwen3.8-27b", "max_tokens": 1, "messages": msgs}).encode()
    req = urllib.request.Request(PROXY, data=body,
                                 headers={"Content-Type": "application/json"})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=600) as r:
        r.read()
    return time.time() - t0, len(body)


msgs = sanitize(load_messages(TRANSCRIPT))
# соль: делает прогон уникальным с первого токена, кэш прошлого прогона не помогает
msgs[0] = {"role": "user", "content": f"[{SALT}] " + (msgs[0]["content"] if isinstance(
    msgs[0]["content"], str) else json.dumps(msgs[0]["content"], ensure_ascii=False))}

# обрезаем историю до окна модели: берём наибольший префикс под лимитом
acc, cut = 0, len(msgs)
for i, m in enumerate(msgs):
    acc += len(json.dumps(m, ensure_ascii=False))
    if acc > MAX_CHARS:
        cut = i
        break
msgs = msgs[:cut]

# шаги: последние STEPS ходов истории, каждый следующий длиннее предыдущего
stops = [i for i, m in enumerate(msgs) if m["role"] == "user" and isinstance(m["content"], list)]
stops = stops[-STEPS:]
before = metrics()
times = []
for k, i in enumerate(stops):
    dt, size = ask(msgs[:i + 1])
    times.append(dt)
    print(f"  шаг {k + 1:2d}: сообщений {i + 1:4d}, тело {size / 1024:7.0f} КБ, {dt:6.2f} с", flush=True)
after = metrics()
q = after["prefix_cache_queries_total"] - before["prefix_cache_queries_total"]
h = after["prefix_cache_hits_total"] - before["prefix_cache_hits_total"]
print(f"{LABEL}: шагов {len(times)}, сумма {sum(times):6.1f} с, медиана {sorted(times)[len(times) // 2]:5.2f} с, "
      f"prefix cache {h / q * 100 if q else 0:5.1f}% ({h:.0f}/{q:.0f} токенов)")
