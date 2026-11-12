#!/usr/bin/env python3
# Замер Ш3: угадывает ли локальная LLM категорию промпта чаще, чем правила Ш2 (PromptHints).
# Набор — tools/image-bench/cases.json из ветки feat/image-bench (у каждого промпта есть категория).
# Запуск: python3 -I measure.py --cases cases.json --url http://localhost:18020 --model qwen3.8-27b [--repeat 3]
import argparse
import json
import re
import statistics
import time
import urllib.request

# Регэкспы — копия PromptHints (LocalImageRouting.cs); (?<!\p{L}) → (?<![^\W\d_])
NB = r"(?<![^\W\d_])"
QUOTED = re.compile(r"«[^»]*[^\W_][^»]*»|\"[^\"]*[^\W_][^\"]*\"|“[^”]*[^\W_][^”]*”")
SIGN = re.compile(NB + r"(вывеск|афиш|слайд|плакат|баннер|постер|надпис|signboard|poster|slide|banner|billboard|caption)", re.I)
PEOPLE = re.compile(NB + r"(человек|людей|люди|портрет|девушк|парен|мужчин|женщин|ребён|ребен|дет[ие]|лиц[оа]|селфи|person|people|portrait|woman|man\b|girl|boy|face)", re.I)
PHOTO = re.compile(NB + r"(фото|реалистич|фотореал|photo|realistic)", re.I)
FAST = re.compile(NB + r"(фон|аксессуар|очки|очков|шляп|background|accessor)", re.I)
PRECISE = re.compile(NB + r"(одежд|футболк|куртк|платье|плать|рубашк|штан|костюм|пальто|clothes|clothing|shirt|dress|jacket|точн)", re.I)

GEN_CATS = ["люди", "товар", "арт", "надписи", "состав", "пейзаж"]
EDIT_CATS = ["фон", "одежда", "добавить", "убрать", "стиль", "текст"]
# Модель иногда отвечает по-английски — те же категории
ALIASES = {"people": "люди", "product": "товар", "art": "арт", "text": "текст", "composition": "состав",
           "landscape": "пейзаж", "background": "фон", "clothes": "одежда", "clothing": "одежда", "add": "добавить",
           "remove": "убрать", "style": "стиль"}

GEN_INSTR = (
    "Определи категорию запроса на генерацию картинки. Категории: "
    "люди — главное в кадре человек или группа людей; "
    "товар — предметная или рекламная съёмка одного продукта; "
    "арт — рисунок, живопись, иллюстрация, аниме, 3D-графика или другая стилизация, не фото; "
    "надписи — в кадре должен быть читаемый текст; "
    "состав — важны точное число, цвета и расположение нескольких предметов; "
    "пейзаж — природа, город или интерьер без главного героя. "
    "Ответь JSON вида {\"category\":\"…\"}. Ничего, кроме JSON."
)
EDIT_INSTR = (
    "Определи вид правки фотографии по запросу. Виды: "
    "фон — заменить или изменить фон; одежда — переодеть человека; "
    "добавить — добавить предмет или аксессуар; убрать — убрать предмет; "
    "стиль — перерисовать всю картинку в другом стиле; текст — написать или заменить надпись. "
    "Ответь JSON вида {\"category\":\"…\"}. Ничего, кроме JSON."
)


# Категория по правилам: только то, что таблица Ш2 различает; None — правила молчат (gen-default / edit-default)
def rules_gen(p):
    if QUOTED.search(p) or SIGN.search(p):
        return "надписи"
    if PEOPLE.search(p) or PHOTO.search(p):
        return "люди"
    return None


def rules_edit(p):
    if QUOTED.search(p) or SIGN.search(p):
        return "текст"
    if PRECISE.search(p):
        return "одежда"
    m = FAST.search(p)
    if m:
        w = m.group(1).lower()
        return "фон" if w.startswith(("фон", "background")) else "добавить"
    return None


def ask(url, model, instr, prompt, timeout):
    body = {
        "model": model,
        "messages": [{"role": "user", "content": f"{instr}\n\nТЕКСТ ЗАПРОСА:\n{prompt}"}],
        "max_tokens": 32, "temperature": 0,
        "response_format": {"type": "json_object"},
        "chat_template_kwargs": {"enable_thinking": False}, "reasoning_format": "none",
    }
    req = urllib.request.Request(url.rstrip("/") + "/v1/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    t0 = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = json.load(r)["choices"][0]["message"]["content"]
        cat = json.loads(raw).get("category")
        cat = cat.strip().lower() if isinstance(cat, str) else None
        cat = ALIASES.get(cat, cat)
    except Exception as e:  # noqa: BLE001 — в замере любой сбой = «нет ответа»
        cat, raw = None, f"ошибка: {type(e).__name__}"
    return cat, time.monotonic() - t0, raw


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", required=True)
    ap.add_argument("--url", default="http://localhost:18020")
    ap.add_argument("--model", default="qwen3.8-27b")
    ap.add_argument("--repeat", type=int, default=1)
    ap.add_argument("--cap", type=float, default=2.0, help="потолок ожидания в бою, с")
    ap.add_argument("--timeout", type=float, default=30.0)
    a = ap.parse_args()
    cases = json.load(open(a.cases, encoding="utf-8"))
    rows = []
    for kind, instr, rules, cats in (("generate", GEN_INSTR, rules_gen, GEN_CATS), ("edit", EDIT_INSTR, rules_edit, EDIT_CATS)):
        for c in cases[kind]:
            r = rules(c["prompt"])
            for i in range(a.repeat):
                llm, dt, raw = ask(a.url, a.model, instr, c["prompt"], a.timeout)
                if kind == "generate" and llm == "текст":
                    llm = "надписи"
                rows.append(dict(kind=kind, id=c["id"], cat=c["category"], rules=r, llm=llm if llm in cats else None,
                                 raw=raw, sec=round(dt, 3), rep=i))
                print(f"{c['id']} {c['category']:9} правила={r or '—':9} llm={llm or '—':9} {dt:5.2f} с", flush=True)

    def acc(sub, key, cap=None):
        ok = sum(1 for x in sub if x[key] == x["cat"] and (cap is None or key != "llm" or x["sec"] <= cap))
        return ok, len(sub)

    print("\nИТОГ (доля верной категории)")
    for kind in ("generate", "edit"):
        sub = [x for x in rows if x["kind"] == kind]
        amb = [x for x in sub if x["rules"] is None]
        for name, s in (("все", sub), ("правила молчат", amb)):
            ro, n = acc(s, "rules")
            lo, _ = acc(s, "llm")
            lc, _ = acc(s, "llm", a.cap)
            print(f"{kind:8} {name:15} n={n:3}  правила {ro}/{n}  LLM {lo}/{n}  LLM≤{a.cap:.0f}с {lc}/{n}")
    secs = sorted(x["sec"] for x in rows)
    p95 = secs[min(len(secs) - 1, int(len(secs) * 0.95))]
    over = sum(1 for s in secs if s > a.cap)
    print(f"время LLM: медиана {statistics.median(secs):.2f} с, p95 {p95:.2f} с, макс {secs[-1]:.2f} с, дольше {a.cap:.0f} с — {over}/{len(secs)}")
    json.dump(rows, open("/tmp/s3-measure-rows.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
