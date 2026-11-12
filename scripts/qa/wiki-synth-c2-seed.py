"""Посев чатов эксперимента для QA С2 (ночной синтез) — подготовка истории для стенда.

Собирает в .cc-attachments/qa-wiki-synth-c2/:
  manifest.json      — состав чатов (юниты S1..S5, ключи, ожидаемые счётчики реплик);
  history-<key>.json — история чата в формате history.json сервера (kind user_message|text).

Источник — сырые выдержки переписок эксперимента (личные данные, вне git):
  <raw>/s1.txt, s2a.txt, s2b.txt, s3.txt, s4.txt, s5.txt
Формат исходника: строки «[N] USER: …» / «[N] ASSIST: …» и продолжения блока ниже.

Запуск:
  python3 -I scripts/qa/wiki-synth-c2-seed.py <путь-к-raw> [путь-к-выходу]

Префиксы «[N] USER:» / «[N] ASSIST:» убираются (в исходной переписке их не было), текст
реплик не трогается — дословность цитат проверяется по нему.
"""
import json
import os
import re
import sys

UNITS = {"S1": ["s1"], "S2": ["s2a", "s2b"], "S3": ["s3"], "S4": ["s4"], "S5": ["s5"]}
LABEL = {"s1": "96c78832", "s2a": "5bb6162b", "s2b": "083bcd9d",
         "s3": "ab14d837", "s4": "08394c9c", "s5": "8985d59a"}
TITLE = {
    "s1": "Фича: ускорение тестов · продолжение 2",
    "s2a": "Фича: честный прогресс фичи (процент и гант)",
    "s2b": "Ускорение тестирования разработки",
    "s3": "Посмотри новый файл сна",
    "s4": "Разбор вчерашних анализов",
    "s5": "Обновилась информация во встречах, посмотри их и…",
}
MESSAGE = re.compile(r"^\[(\d+)\] (USER|ASSIST):(.*)$")
# Отметки времени: Unix-мс UTC. У исходных чатов годы не значимы — важна длина дельты.
BASE_TS = 1760000000000
STEP_MS = 60000


def parse_messages(text: str):
    """Блоки переписки: [(кто, текст)] — по метке «[N] USER|ASSIST:», продолжения — строки ниже."""
    out, cur = [], None
    for line in text.split("\n"):
        m = MESSAGE.match(line)
        if m:
            if cur is not None:
                out.append(cur)
            cur = [m.group(2), m.group(3)]
        elif cur is not None:
            cur[1] += "\n" + line
    if cur is not None:
        out.append(cur)
    return out


def build(key: str, raw_dir: str) -> list[dict]:
    src = open(os.path.join(raw_dir, f"{key}.txt"), encoding="utf-8").read()
    out, ts = [], BASE_TS
    for who, text in parse_messages(src):
        text = text.strip()
        if not text:
            continue
        ts += STEP_MS
        out.append({"kind": "user_message" if who == "USER" else "text", "text": text, "timestamp": ts})
    return out


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    raw_dir = sys.argv[1]
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        ".cc-attachments", "qa-wiki-synth-c2")
    os.makedirs(out_dir, exist_ok=True)
    manifest = []
    for unit, keys in UNITS.items():
        for key in keys:
            msgs = build(key, raw_dir)
            path = os.path.join(out_dir, f"history-{key}.json")
            with open(path, "w", encoding="utf-8") as f:
                json.dump(msgs, f, ensure_ascii=False, indent=1)
            manifest.append({
                "unit": unit, "key": key, "label": LABEL[key], "title": TITLE[key],
                "messages": len(msgs),
                "user": sum(1 for m in msgs if m["kind"] == "user_message"),
                "assist": sum(1 for m in msgs if m["kind"] == "text"),
                "chars": sum(len(m["text"]) for m in msgs),
            })
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)
    # Уменьшенное окно для пробы размера: первые 12 реплик s3 (~15 КБ).
    small = build("s3", raw_dir)[:12]
    with open(os.path.join(out_dir, "history-s3-small.json"), "w", encoding="utf-8") as f:
        json.dump(small, f, ensure_ascii=False, indent=1)
    for m in manifest:
        print(f"{m['unit']:3} {m['key']:4} реплик={m['messages']:4} человек={m['user']:3} "
              f"агент={m['assist']:3} символов={m['chars']}")
    print("малый окно s3:", len(small), "реплик")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
