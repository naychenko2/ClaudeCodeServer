#!/usr/bin/env python3
"""Шаг 6 стенда: корпус «как в новых чатах» + синтетические поправки человека.

Из `corpus/` строит `corpus-mod/` (корпус и вопросы), одинаковый для всех схем:
  1. Каждая реплика человека в истории получает маркер `<!-- ccs:human-text chars=N sha=… -->`
     (формат — ClaudeSession.WithHumanTextMarker): так выглядят все новые чаты. Служебные вставки
     (остановка хода, task-notification, «Продолжай», «Output token limit…») маркера не получают —
     ShouldMarkHumanText не метит auto/systemDirective. Настоящие поправки, по которым уже есть
     вопросы корпуса, метятся всегда.
  2. В середину истории (≈35 % и ≈65 %) вставляется короткая синтетическая реплика человека с
     конкретным ограничением или сменой решения (тоже с маркером) и вопрос-проверка к ней.
     Реплику и вопрос пишет локальная модель по хвосту истории перед точкой вставки; вопрос и
     эталон получают пометку synthetic — стенд считает их отдельно от настоящих поправок.

Реплика вставляется текстовым блоком в сообщение человека (в конец, после tool_result, если он
там есть): ассистент в истории на неё не отвечает — это ограничение стенда, оно одинаково для
всех схем. Данные пишутся вне git (`$BENCH_HOME/corpus-mod`).
"""
import argparse
import json
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

PROXY_DIR = os.environ.get("COMPACTOR_DIR",
                           "/home/an/Sources/ClaudeCodeServer-compactor-preserve/tools/thinking-strip-proxy")
sys.path.insert(0, PROXY_DIR)
import compactor as C  # noqa: E402

MARKER = "<!-- ccs:human-text chars={0} sha={1} -->"
SERVICE = ("[", "<task-notification", "Another Claude session", "Your ", "Output token limit",
           "Твой ход закончился", "Продолжай", "Делай  [ЦИКЛ")


def is_service(raw):
    r = raw.lstrip()
    return r.startswith(SERVICE)


def marked(raw):
    return MARKER.format(len(raw), C._sha8(raw)) + "\n" + raw


def norm(s):
    return re.sub(r"\s+", " ", re.sub(r"[^\w\s]", " ", s.lower())).strip()


def mark_history(history, quotes):
    """Метит реплики человека на месте; возвращает число помеченных."""
    n = 0
    for m in history:
        if m["role"] != "user" or any(b["type"] == "tool_result" for b in m["content"]):
            continue
        for b in m["content"]:
            if b["type"] != "text" or C._MARKER_RE.search(b["text"]):
                continue
            text = b["text"]
            raw = C._strip_system_reminder(C._old_chat_reply(text))
            if not raw:
                continue
            force = any(q and norm(q) in norm(raw) for q in quotes)
            if is_service(raw) and not force:
                continue
            i = text.rfind(raw)
            if i < 0:
                continue
            b["text"] = text[:i] + marked(raw) + text[i + len(raw):]
            n += 1
    return n


def insert_points(history, count):
    """Индексы user-сообщений для вставки: ближайшие к 35 % и 65 % длины, не соседние."""
    n = len(history)
    cands = [i for i, m in enumerate(history) if m["role"] == "user" and i > 0.2 * n and i < 0.8 * n
             and not any(b["type"] == "text" and C._MARKER_RE.search(b["text"]) for b in m["content"])]
    out = []
    for frac in (0.35, 0.65)[:count]:
        free = [i for i in cands if all(abs(i - o) > 5 for o in out)]
        if free:
            out.append(min(free, key=lambda i: abs(i - frac * n)))
    return sorted(out)


GEN_PROMPT = """Ты помогаешь собрать проверочный набор для сжатия длинных рабочих сессий.
Ниже — начало и фрагмент сессии агента-программиста с человеком (до точки вставки).

=== НАЧАЛО СЕССИИ ===
{head}

=== ФРАГМЕНТ ПЕРЕД ТОЧКОЙ ВСТАВКИ ===
{tail}

Придумай ОДНУ короткую реплику человека (1–2 предложения, по-русски, живая речь, без вежливых
оборотов), которую он мог бы написать в этот момент. Реплика — конкретное ограничение или смена
решения, например «не трогай <конкретный файл/папку из фрагмента>», «вместо <A из фрагмента>
делаем <B>», «имена новых файлов начинай с префикса <…>». Требования:
- упомяни конкретные сущности, реально встречающиеся во фрагменте (файл, папка, функция, технология);
  новое решение B/префикс/число придумай сам, оно должно быть конкретным и проверяемым;
- реплика не должна совпадать по смыслу ни с чем, что человек уже говорил в этой сессии;
- не пиши «продолжай», извинений и объяснений.
Затем составь вопрос-проверку: он спрашивает, какое указание дал человек, и называет ТЕМУ
(о чём указание), но НЕ раскрывает содержание. И эталонный ответ: что именно человек потребовал.

Ответь ТОЛЬКО JSON: {{"reply": "...", "question": "...", "reference_answer": "..."}}"""


def gen_one(item, pos, local):
    hist = item["history"]
    head = common.render_text(hist[:2], tool_limit=1500)[:2500]
    tail = common.render_text(hist[max(0, pos - 14):pos], tool_limit=600)[-6000:]
    last = None
    for _ in range(4):
        out, _u = common.call_messages(local, None, [{"role": "user", "content": GEN_PROMPT.format(
            head=head, tail=tail)}], max_tokens=2000, timeout=300, temperature=0.7)
        try:
            j = common.extract_json(out)
            reply, q, ref = (str(j[k]).strip() for k in ("reply", "question", "reference_answer"))
            if 20 <= len(reply) <= 400 and len(q) > 20 and len(ref) > 10 and not is_service(reply) \
                    and "продолж" not in reply.lower()[:12]:
                return reply, q, ref
        except (ValueError, KeyError, TypeError) as e:
            last = e
    raise RuntimeError(f"{item['id']}: модель не дала реплику: {last!r}")


def build(item, qs, local):
    quotes = [q.get("human_quote", "") for q in qs["questions"] if q["type"] == "correction"]
    n_marked = mark_history(item["history"], quotes)
    pts = insert_points(item["history"], 2 if len(item["history"]) > 100 else 1)
    new_q, synth = [], []
    for pos in pts:
        reply, q, ref = gen_one(item, pos, local)
        item["history"][pos]["content"].append({"type": "text", "text": marked(reply)})
        synth.append({"pos": pos, "of": len(item["history"]), "reply": reply})
        new_q.append({"type": "correction", "question": q, "reference_answer": ref,
                      "evidence": reply, "human_quote": reply, "synthetic": True})
    item["synthetic"] = synth
    return n_marked, dict(qs, questions=qs["questions"] + new_q)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", default=os.path.join(common.BENCH_HOME, "corpus"))
    ap.add_argument("--dst", default=os.path.join(common.BENCH_HOME, "corpus-mod"))
    ap.add_argument("--workers", type=int, default=2)
    a = ap.parse_args()
    os.makedirs(os.path.join(a.dst, "questions"), exist_ok=True)
    local = common.local_endpoint()

    def work(fn):
        item = json.load(open(os.path.join(a.src, fn), encoding="utf-8"))
        qs = json.load(open(os.path.join(a.src, "questions", fn), encoding="utf-8"))
        n, qs2 = build(item, qs, local)
        # самопроверка: все вставленные реплики разбираются как маркерные и дословно
        raws = C._marked_replies(item["history"]) or []
        for s in item["synthetic"]:
            assert s["reply"] in raws, f"{fn}: вставка не прошла проверку маркера"
        for p, obj in ((os.path.join(a.dst, fn), item), (os.path.join(a.dst, "questions", fn), qs2)):
            with open(p, "w", encoding="utf-8") as f:
                json.dump(obj, f, ensure_ascii=False)
            os.chmod(p, 0o600)
        print(f"{item['id']}: помечено реплик {n}, вставок {len(item['synthetic'])}: "
              + "; ".join(f"[{s['pos']}/{s['of']}] {s['reply'][:90]}" for s in item["synthetic"]), flush=True)

    fns = sorted(f for f in os.listdir(a.src) if f.endswith(".json"))
    with ThreadPoolExecutor(a.workers) as ex:
        list(ex.map(work, fns))


if __name__ == "__main__":
    main()
