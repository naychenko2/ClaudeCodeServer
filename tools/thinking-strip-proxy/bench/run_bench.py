#!/usr/bin/env python3
"""Стенд качества сжатия: один запуск -> таблица по запросам и по типам вопросов.

Для каждого запроса корпуса:
  1. СХЕМА сжатия даёт сырой текст сводки (`--scheme old` — запрос как есть в MiniMax-M3, как
     делает прокси: compact_body/open_compact). Сводка обрабатывается как в CLI: <analysis>
     вырезается, <summary> -> «Summary:», текст вне тегов сохраняется.
  2. Локальная модель (стенд vLLM через прокси) отвечает на вопросы-проверки по контексту
     «после сжатия» = сводка + сохранённый CLI сегмент (дословно).
  3. Сильная модель-судья сверяет ответ с эталоном (построен по ПОЛНОЙ истории): шкала 0–5.
Всё кэшируется в каталоге прогона: повторный запуск доделывает недостающее.

Схемы: old | actual | dir:<каталог с <id>.txt> | plugin:<файл.py с summarize(item) -> str>
"""
import argparse
import importlib.util
import json
import os
import re
import sys
import time
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

# correction_syn — вставленные make_synthetic.py поправки (вопрос помечен synthetic), correction —
# настоящие реплики человека из корпуса: считаются раздельно.
TYPES = ["fact", "artifact", "continuation", "decision", "correction", "correction_syn"]
TYPE_RU = {"fact": "факты", "artifact": "артефакты", "continuation": "продолжение",
           "decision": "решения", "correction": "поправки (настоящие)",
           "correction_syn": "поправки (синтетические)"}


def qtype(q):
    return "correction_syn" if q.get("synthetic") else q["type"]
GOOD = 4  # оценка, с которой ответ считаем «сохранённым»

ANSWER_SYSTEM = ("Ты — ассистент, продолжающий работу после сжатия контекста. В начале диалога — "
                 "сводка предыдущей работы, затем идут последние сообщения дословно. Инструментов у "
                 "тебя нет: отвечай только по контексту.")
ANSWER_ASK = ("Вопрос по предыдущей работе:\n{q}\n\nОтветь кратко и точно (1–4 предложения), "
              "опираясь только на контекст выше. Если нужных данных в контексте нет — так и напиши "
              "(«в контексте нет данных»), ничего не выдумывай.")

JUDGE_PROMPT = """Ты проверяешь ответ на вопрос о прошлой работе ассистента. Сверяй с ЭТАЛОНОМ,
который построен по полной истории; свои знания не подмешивай.

Вопрос ({type}): {question}

ЭТАЛОН: {reference}

ОТВЕТ: {answer}

Шкала 0–5:
5 — все ключевые факты эталона названы верно, ошибок нет;
4 — верно по существу, пропущена или неточна мелкая деталь;
3 — верна примерно половина ключевого, или главное верно, но важная деталь ошибочна;
2 — лишь намёк на верное, остальное расплывчато или неверно;
1 — по существу неверно, но есть случайные совпадения;
0 — «данных нет», противоречит эталону или выдумка.
Уверенный неверный ответ — не выше 1. Лишние верные подробности не штрафуются.

Ответь ТОЛЬКО JSON: {{"score": <0-5>, "reason": "<одно-два предложения: что верно, что пропущено или неверно>"}}"""


# --- Схемы сжатия ----------------------------------------------------------------------------
def request_messages(item):
    """История + инструкция сжатия CLI последним user-сообщением (см. build_corpus)."""
    msgs = [{"role": m["role"], "content": list(m["content"])} for m in item["history"]]
    ins = {"type": "text", "text": common.compact_instruction(item.get("variant", "up_to"))}
    if msgs and msgs[-1]["role"] == "user":
        msgs[-1]["content"].append(ins)
    else:
        msgs.append({"role": "user", "content": [ins]})
    return msgs


def scheme_old(item, args):
    """Нынешняя схема: тело запроса сжатия уходит в MiniMax-M3 как есть."""
    raw, usage = common.call_messages(
        common.minimax_endpoint(), item["system"], request_messages(item),
        max_tokens=common.COMPACT_MAX_TOKENS, timeout=1500)
    return raw, {"in_tokens": (usage.get("input_tokens") or 0) + (usage.get("cache_read_input_tokens") or 0),
                 "out_tokens": usage.get("output_tokens")}


def load_scheme(spec):
    """-> (имя, функция(item, args) -> (сырой текст, метаданные), готовая_ли_сводка_целиком)."""
    if spec == "old":
        return "old", scheme_old, False
    if spec == "actual":
        return "actual", (lambda item, args: (item["actual_summary"], {})), True
    if spec.startswith("dir:"):
        d = spec[4:]

        def from_dir(item, args):
            return open(os.path.join(d, item["id"] + ".txt"), encoding="utf-8").read(), {}
        return "dir-" + os.path.basename(d.rstrip("/")), from_dir, False
    if spec.startswith("plugin:"):
        path = spec[7:]
        m = importlib.util.spec_from_file_location("bench_plugin", path)
        mod = importlib.util.module_from_spec(m)
        m.loader.exec_module(mod)

        def from_plugin(item, args):
            r = mod.summarize(item)
            return (r if isinstance(r, tuple) else (r, {}))
        return "plugin-" + os.path.splitext(os.path.basename(path))[0], from_plugin, False
    sys.exit(f"неизвестная схема: {spec}")


# --- Контекст «после сжатия» и ответы ---------------------------------------------------------
def flatten(msgs, tool_limit=6000):
    """Хвост с tool_use/tool_result -> простой текст (у отвечающего нет описаний инструментов)."""
    out = []
    for m in msgs:
        parts = []
        for b in m["content"]:
            t = b["type"]
            if t == "text":
                parts.append(b["text"])
            elif t == "tool_use":
                parts.append(f"[вызов {b.get('name')}] {json.dumps(b.get('input'), ensure_ascii=False)[:tool_limit]}")
            elif t == "tool_result":
                c = b.get("content")
                if isinstance(c, list):
                    c = "\n".join(x.get("text", "") for x in c if isinstance(x, dict))
                parts.append("[результат] " + str(c or "")[:tool_limit])
        if parts:
            blk = {"type": "text", "text": "\n".join(parts)}
            if out and out[-1]["role"] == m["role"]:
                out[-1]["content"].append(blk)
            else:
                out.append({"role": m["role"], "content": [blk]})
    return out


def after_compact_messages(item, summary_text):
    msgs = [{"role": "user", "content": [{"type": "text", "text": summary_text}]}]
    for m in flatten(item["preserved"]):
        if msgs[-1]["role"] == m["role"]:
            msgs[-1]["content"].extend(m["content"])
        else:
            msgs.append(m)
    return msgs


def answer_question(item, summary_text, q, local):
    msgs = after_compact_messages(item, summary_text)
    ask = {"type": "text", "text": ANSWER_ASK.format(q=q["question"])}
    if msgs[-1]["role"] == "user":
        msgs[-1]["content"] = msgs[-1]["content"] + [ask]
    else:
        msgs.append({"role": "user", "content": [ask]})
    out, _ = common.call_messages(local, ANSWER_SYSTEM, msgs, max_tokens=6000, timeout=900)
    return out.strip()


def judge(q, answer, endpoint):
    prompt = JUDGE_PROMPT.format(type=q["type"], question=q["question"],
                                 reference=q["reference_answer"], answer=answer)
    for _ in range(3):
        out, _ = common.call_messages(endpoint, None, [{"role": "user", "content": prompt}],
                                      max_tokens=3000, timeout=300)
        try:
            j = common.extract_json(out)
            return max(0, min(5, int(j["score"]))), str(j.get("reason", ""))
        except (ValueError, KeyError, TypeError):
            continue
    return None, "судья не вернул оценку"


def norm(s):
    return re.sub(r"\s+", " ", re.sub(r"[^\w\s]", " ", s.lower())).strip()


def verbatim_share(quote, summary_text):
    """Доля 3-словных окон реплики человека, дословно встречающихся в сводке (1.0 — вся реплика)."""
    w = norm(quote).split()
    hay = " " + norm(summary_text) + " "
    if len(w) < 3:
        return 1.0 if (" " + " ".join(w) + " ") in hay else 0.0
    wins = [" ".join(w[i:i + 3]) for i in range(len(w) - 2)]
    return sum(1 for x in wins if (" " + x + " ") in hay) / len(wins)


# --- Прогон ----------------------------------------------------------------------------------
def jload(p, default=None):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else default


def jsave(p, obj):
    with open(p, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=1)
    os.chmod(p, 0o600)


def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else None


def fmt(x, nd=2):
    return "—" if x is None else f"{x:.{nd}f}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--scheme", default="old")
    ap.add_argument("--corpus", default=os.path.join(common.BENCH_HOME, "corpus"))
    ap.add_argument("--out", default=os.path.join(common.BENCH_HOME, "runs"), help="каталог прогонов (вне git)")
    ap.add_argument("--ids", nargs="*", help="только эти запросы")
    ap.add_argument("--sum-workers", type=int, default=3)
    ap.add_argument("--ans-workers", type=int, default=2)
    ap.add_argument("--fresh", action="store_true", help="игнорировать кэш прогона")
    ap.add_argument("--tag", default="", help="суффикс каталога прогона (повторы для оценки шума)")
    a = ap.parse_args()

    name, summarize, whole = load_scheme(a.scheme)
    run = os.path.join(a.out, name + (("-" + a.tag) if a.tag else ""))
    os.makedirs(run, exist_ok=True)
    local = common.local_endpoint()
    # Судья: BENCH_JUDGE=local — та же локальная модель, что отвечает; BENCH_JUDGE_URL/KEY/MODEL —
    # любой Anthropic-совместимый эндпоинт; иначе MiniMax-M3 (как в базовой линии шага 2).
    judge_ep = (os.environ.get("BENCH_JUDGE_URL"), os.environ.get("BENCH_JUDGE_KEY"),
                os.environ.get("BENCH_JUDGE_MODEL"))
    if os.environ.get("BENCH_JUDGE") == "local":
        judge_ep = (os.environ.get("BENCH_JUDGE_LOCAL_URL", local[0]), "x",
                    os.environ.get("BENCH_JUDGE_LOCAL_MODEL", local[2]))
    elif not all(judge_ep):
        judge_ep = common.minimax_endpoint()

    items = []
    for fn in sorted(os.listdir(a.corpus)):
        if fn.endswith(".json"):
            it = jload(os.path.join(a.corpus, fn))
            qs = jload(os.path.join(a.corpus, "questions", fn))
            if qs and qs["questions"] and (not a.ids or it["id"] in a.ids):
                it["questions"] = qs["questions"]
                items.append(it)
    if not items:
        sys.exit("нет запросов с вопросами: build_corpus.py, затем make_questions.py")
    print(f"схема {name}: {len(items)} запросов, {sum(len(i['questions']) for i in items)} вопросов -> {run}",
          file=sys.stderr)

    # 1. сводки
    def do_summary(it):
        fp = os.path.join(run, it["id"] + ".summary.json")
        c = None if a.fresh else jload(fp)
        if c:
            return it["id"], c
        t0 = time.time()
        raw, meta = summarize(it, a)
        text = raw if whole else common.summary_message(raw)
        c = {"raw": raw, "text": text, "sec": round(time.time() - t0, 1), "meta": meta}
        jsave(fp, c)
        print(f"  {it['id']}: сводка {len(text)} симв., {c['sec']} с", file=sys.stderr, flush=True)
        return it["id"], c

    with ThreadPoolExecutor(a.sum_workers) as ex:
        sums = dict(ex.map(do_summary, items))

    # 2+3. ответы и оценки (по запросу последовательно — общий префикс кэшируется vLLM)
    def do_answers(it):
        fp = os.path.join(run, it["id"] + ".eval.json")
        ev = ({} if a.fresh else jload(fp, {})) or {}
        for i, q in enumerate(it["questions"]):
            key = str(i)
            if key in ev and ev[key].get("score") is not None:
                continue
            try:
                ans = ev.get(key, {}).get("answer") or answer_question(it, sums[it["id"]]["text"], q, local)
                score, reason = judge(q, ans, judge_ep)
            except RuntimeError as e:
                ans, score, reason = "", None, f"сбой: {e}"
            ev[key] = {"type": q["type"], "answer": ans, "score": score, "reason": reason}
            jsave(fp, ev)
        print(f"  {it['id']}: оценено {len(ev)} вопросов", file=sys.stderr, flush=True)
        return it["id"], ev

    with ThreadPoolExecutor(a.ans_workers) as ex:
        evals = dict(ex.map(do_answers, items))

    # --- отчёт
    rows, by_type, all_scores = [], defaultdict(list), []
    per_q = []
    corr_good = corr_total = 0
    verb, rows_corr = [], []
    for it in items:
        ev = evals[it["id"]]
        sc = defaultdict(list)
        for i, q in enumerate(it["questions"]):
            s = ev.get(str(i), {}).get("score")
            t = qtype(q)
            sc[t].append(s)
            v = None
            if s is not None:
                by_type[t].append(s)
                all_scores.append(s)
            if t in ("correction", "correction_syn"):
                v = verbatim_share(q.get("human_quote", ""), sums[it["id"]]["text"])
            if t == "correction":
                corr_total += 1
                corr_good += 1 if (s or 0) >= GOOD else 0
                verb.append(v)
                rows_corr.append((it["id"], s, v))
            per_q.append({"id": it["id"], "i": i, "type": t, "score": s, "verbatim": v})
        allq = [x for v in sc.values() for x in v]
        rows.append((it["id"], f"{it['est_tokens'] // 1000}k", len(it["questions"]), mean(allq),
                     {t: mean(sc[t]) for t in TYPES}, len(sums[it["id"]]["text"]),
                     sums[it["id"]]["sec"]))

    L = []
    L.append(f"# Прогон стенда сжатия — схема `{name}`{(' (' + a.tag + ')') if a.tag else ''}\n")
    L.append(f"Запросов: {len(items)}, вопросов: {len(all_scores)}; ответчик — {local[2]} (стенд), "
             f"судья — {judge_ep[2]}.\n")
    L.append("## По запросам (средняя оценка 0–5)\n")
    L.append("| запрос | история | вопросов | среднее | " + " | ".join(TYPE_RU[t] for t in TYPES) + " | сводка, симв. | сводка, с |")
    L.append("|---|---|---|---|" + "---|" * len(TYPES) + "---|---|")
    for r in rows:
        L.append(f"| {r[0]} | {r[1]} | {r[2]} | {fmt(r[3])} | " + " | ".join(fmt(r[4][t]) for t in TYPES)
                 + f" | {r[5]} | {r[6]} |")
    L.append("\n## По типам вопросов\n")
    L.append("| тип | вопросов | среднее (0–5) | доля ≥4 |")
    L.append("|---|---|---|---|")
    for t in TYPES:
        xs = by_type[t]
        L.append(f"| {TYPE_RU[t]} | {len(xs)} | {fmt(mean(xs))} | "
                 f"{fmt(sum(1 for x in xs if x >= GOOD) / len(xs) if xs else None)} |")
    L.append(f"| **все** | {len(all_scores)} | **{fmt(mean(all_scores))}** | "
             f"{fmt(sum(1 for x in all_scores if x >= GOOD) / len(all_scores) if all_scores else None)} |")
    L.append("\n## Поправки человека (настоящие)\n")
    L.append(f"- Сохранено (ответ оценён ≥{GOOD}): **{corr_good} из {corr_total}** "
             f"({fmt(corr_good / corr_total if corr_total else None)})")
    L.append(f"- Реплика человека дословно в сводке (доля 3-словных окон, среднее): **{fmt(mean(verb))}**; "
             f"целиком (≥0,95): {sum(1 for v in verb if v >= 0.95)} из {len(verb)}")
    L.append("\n| запрос | оценка ответа | дословность в сводке |")
    L.append("|---|---|---|")
    for cid, s, v in rows_corr:
        L.append(f"| {cid} | {s} | {fmt(v)} |")
    report = "\n".join(L)
    with open(os.path.join(run, "report.md"), "w", encoding="utf-8") as f:
        f.write(report + "\n")
    jsave(os.path.join(run, "results.json"), {
        "scheme": name, "tag": a.tag, "judge": judge_ep[2], "mean": mean(all_scores),
        "per_question": per_q,
        "per_item": {it["id"]: {"chars": len(sums[it["id"]]["text"]),
                                "est_tokens": common.est_tokens(sums[it["id"]]["text"]),
                                "meta": sums[it["id"]]["meta"]} for it in items},
        "by_type": {t: mean(by_type[t]) for t in TYPES},
        "corrections": {"good": corr_good, "total": corr_total, "verbatim_mean": mean(verb)}})
    print(report)


if __name__ == "__main__":
    main()
