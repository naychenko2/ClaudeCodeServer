#!/usr/bin/env python3
"""Шаг 3 стенда: вопросы-проверки и эталонные ответы по ПОЛНОЙ истории каждого запроса.

Образец — оценка сжатия у Factory: после сжатия модель отвечает на вопросы о прошлой работе,
ответы сверяют с эталоном. Типы вопросов:
  fact          — точное значение (текст ошибки, число, имя, команда);
  artifact      — какие файлы изменены/созданы и что в них сделано;
  continuation  — какой следующий шаг, что осталось;
  decision      — что решили и что отвергли (и почему);
  correction    — поправка ЧЕЛОВЕКА из середины истории: что он потребовал изменить.
Главный провал нынешней схемы — поправки: MiniMax пропускает раздел «All user messages».

Вопросы строит сильная модель (по умолчанию MiniMax-M3: ей нужно окно в сотни тысяч токенов).
Цитата-доказательство каждого вопроса проверяется ПОДСТРОКОЙ по тексту истории: вопросы с
выдуманной цитатой отбрасываются. Результат — `questions/<id>.json` рядом с корпусом.
"""
import argparse
import json
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

TYPES = ["fact", "artifact", "continuation", "decision", "correction"]

PROMPT = """Ты составляешь экзамен для проверки качества СЖАТИЯ длинной рабочей сессии.

Ниже — полная история сессии агента-программиста с человеком (реплики с пометкой
[ЧЕЛОВЕК/СИСТЕМА] бывают двух видов: настоящие реплики человека и служебные вставки — блоки
«Возможно релевантные заметки…», <task-notification>, <system-reminder>; служебные вставки
человеческими репликами НЕ считаются, но настоящая реплика человека иногда идёт ПОСЛЕ такого
блока). После истории — хвост сессии, который при сжатии сохраняется дословно: про него
вопросов не задавай.

Составь РОВНО {n} вопросов, по которым можно проверить, сохранила ли сводка нужное. Вопросы
должны быть такими, чтобы ответ однозначно проверялся по истории, и такими, чтобы без сводки
на них нельзя было ответить угадыванием. Типы (поле type):
- fact — точное значение: дословный текст ошибки, число, имя сущности, команда, порог;
- artifact — какие файлы изменены или созданы и что именно в них сделано;
- continuation — какой следующий шаг на момент конца истории и что осталось сделать;
- decision — что было решено и что отвергли, и почему;
- correction — ПОПРАВКА ЧЕЛОВЕКА из СЕРЕДИНЫ истории (не из начала и не из конца): человек
  сказал сделать иначе, отверг подход, добавил ограничение или потребовал изменить результат.

Распределение: {dist}. Обязательно ОДИН вопрос типа correction про реальную реплику человека
из середины истории (позиция примерно 15–85 % по длине) — спрашивай, что человек потребовал
и как это повлияло на дальнейшую работу. В тексте самого вопроса ДОСЛОВНО процитируй реплику
человека в кавычках (human_quote) — иначе вопрос неоднозначен; эталон описывает, что именно человек
требовал, что ассистент после этого изменил и какие конкретные результаты это дало. Поправкой НЕ считаются: «Продолжай»/«continue», служебное «Output token limit hit…»,
<task-notification>, блоки заметок — это не слова человека о сути работы. Если в середине
истории настоящей поправки человека нет совсем, вместо неё задай correction про любую
настоящую просьбу или ограничение человека не из первого сообщения; если и таких нет — верни
correction_missing=true и задай вместо неё ещё один вопрос типа decision (тогда correction не
выдумывай).

Для каждого вопроса дай:
- type, question (самодостаточный: без «в этой сессии/выше» без уточнения предмета),
- reference_answer — эталон, 1–4 предложения, с точными значениями из истории,
- evidence — ДОСЛОВНАЯ цитата из истории: ОДНА строка 20–90 символов, без переносов строк,
  скопированная символ в символ (не пересказ, не склейка), подтверждающая эталон,
- для correction ещё human_quote — ДОСЛОВНАЯ цитата из реплики человека (15–90 символов, одна строка).

Вопросы на русском. Ответь ТОЛЬКО JSON-объектом:
{{"correction_missing": false, "questions": [{{"type": "...", "question": "...",
"reference_answer": "...", "evidence": "...", "human_quote": "..."}}]}}

=== ИСТОРИЯ СЕССИИ ===
{history}

=== ХВОСТ (сохраняется дословно, вопросов про него не задавать) ===
{tail}
"""

DISTS = {8: "fact — 2, artifact — 2, continuation — 1, decision — 2, correction — 1",
         6: "fact — 1, artifact — 1, continuation — 1, decision — 2, correction — 1",
         10: "fact — 3, artifact — 2, continuation — 1, decision — 2, correction — 2"}


def norm(s):
    return re.sub(r"\s+", " ", s).strip().lower()


def quote_ok(q, hist_norm, strict=False):
    """Цитата есть в истории: целиком; для длинных допускаем мелкие расхождения (≥85 % окон по 16 симв.)."""
    e = norm(q or "")
    if not e:
        return False
    if e in hist_norm:
        return True
    if strict or len(e) < 40:
        return False
    wins = [e[i:i + 16] for i in range(0, len(e) - 15, 4)]
    return sum(1 for w in wins if w in hist_norm) >= 0.85 * len(wins)


def make_for_item(item, n, endpoint, tool_limit):
    hist = common.render_text(item["history"], tool_limit=tool_limit)
    tail = common.render_text(item["preserved"], tool_limit=tool_limit)
    prompt = PROMPT.format(n=n, dist=DISTS.get(n, DISTS[8]), history=hist, tail=tail)
    hist_norm = norm(hist)
    best = None
    for attempt in range(3):
        text, usage = common.call_messages(
            endpoint, None, [{"role": "user", "content": prompt}], max_tokens=16000, timeout=1800,
            temperature=0.3 * attempt)
        try:
            data = common.extract_json(text)
        except ValueError as e:
            print(f"  {item['id']}: {e}", file=sys.stderr)
            continue
        good, bad = [], 0
        for q in data.get("questions", []):
            ok = q.get("type") in TYPES and q.get("question") and q.get("reference_answer")
            ok = ok and quote_ok(q.get("evidence"), hist_norm)
            if ok and q["type"] == "correction":
                hq = norm(q.get("human_quote", ""))
                ok = len(hq) >= 15 and not re.match(r"(продолжай|continue|output token limit)", hq)
                ok = ok and quote_ok(q.get("human_quote"), hist_norm, strict=True)
            if ok:
                good.append(q)
            else:
                bad += 1
        print(f"  {item['id']}: принято {len(good)}, отброшено {bad} (цитата не нашлась), "
              f"вход ~{usage.get('input_tokens', '?')} ток", file=sys.stderr)
        has_c = any(q["type"] == "correction" for q in good)
        if best is None or (has_c, len(good)) > (any(q["type"] == "correction" for q in best["questions"]), len(best["questions"])):
            best = {"correction_missing": bool(data.get("correction_missing")), "questions": good}
        if len(good) >= max(6, n - 2) and any(q["type"] == "correction" for q in good):
            break
    return best or {"correction_missing": True, "questions": []}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--corpus", default=os.path.join(common.BENCH_HOME, "corpus"))
    ap.add_argument("--n", type=int, default=8, help="вопросов на запрос (6–10)")
    ap.add_argument("--ids", nargs="*", help="только эти запросы (по умолчанию все)")
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--tool-limit", type=int, default=4000, help="обрезка вывода инструмента в тексте для модели")
    ap.add_argument("--force", action="store_true", help="пересоздать существующие")
    a = ap.parse_args()

    endpoint = (os.environ.get("BENCH_JUDGE_URL"), os.environ.get("BENCH_JUDGE_KEY"),
                os.environ.get("BENCH_JUDGE_MODEL"))
    if not all(endpoint):
        endpoint = common.minimax_endpoint()
    qdir = os.path.join(a.corpus, "questions")
    os.makedirs(qdir, exist_ok=True)
    items = []
    for fn in sorted(os.listdir(a.corpus)):
        if fn.endswith(".json"):
            it = json.load(open(os.path.join(a.corpus, fn), encoding="utf-8"))
            if (not a.ids or it["id"] in a.ids) and (a.force or not os.path.exists(os.path.join(qdir, it["id"] + ".json"))):
                items.append(it)

    def work(it):
        print(f"{it['id']}: ~{it['est_tokens'] // 1000}k ток…", file=sys.stderr, flush=True)
        res = make_for_item(it, a.n, endpoint, a.tool_limit)
        fp = os.path.join(qdir, it["id"] + ".json")
        with open(fp, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=1)
        os.chmod(fp, 0o600)
        return it["id"], res

    with ThreadPoolExecutor(a.workers) as ex:
        for cid, res in ex.map(work, items):
            c = sum(1 for q in res["questions"] if q["type"] == "correction")
            print(f"{cid}: вопросов {len(res['questions'])}, поправок {c}, correction_missing={res['correction_missing']}")


if __name__ == "__main__":
    main()
