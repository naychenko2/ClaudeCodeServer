"""Проба проверки цитат QA С2: доказательство, что проверка не зелёная на пустоте.

Берёт цитаты применённой страницы (вида state) и историю посевного чата, проверяет их
дословность по нормализации продукта (QuoteMatcher: маркеры разметки выброшены,
whitespace схлопнут, двойные кавычки свёрнуты) — потом портит одну цитату в копии и
требует, чтобы проверка её опровергла. Зелёная проверка на испорченной цитате означала бы,
что проверка ничего не проверяет.

Запуск:
  python3 -I scripts/qa/wiki-synth-c2-quote-probe.py <путь-к-history-s3-small.json>
"""
import json
import re
import sys

QUOTES = [
    "Семь ночей подряд T90 от 16.5 до 39%. Ни одной ночи ниже порога плана за всю неделю. "
    "Средняя SpO₂ держится 90.1–91.1%.",
    "Седьмые сутки без движения по сомнологу, HCO₃⁻ и Холтеру. Неделя данных подтвердила: "
    "это не эпизод, а устойчивое состояние, которое режимом сна можно смягчить, но не убрать.",
    "Брадикардия исчезла: 1 эпизод 16 секунд, минимум 46, ниже 45 — ноль. После 21 эпизода "
    "в ночь 20 это возврат к норме.",
]
MARKUP = re.compile(r"[*`~\\]")


def canon(text: str) -> str:
    """Канонизация «как рендер» — как у QuoteMatcher.NormalizeForMatch."""
    text = MARKUP.sub("", text or "").replace('"', "'")
    return re.sub(r"\s+", " ", text).strip()


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    history = json.load(open(sys.argv[1], encoding="utf-8"))
    source = canon("\n".join(m.get("text") or "" for m in history))

    missing = [q for q in QUOTES if canon(q) not in source]
    print(f"исходных цитат: {len(QUOTES)}, не найдено: {len(missing)}")
    for q in missing:
        print("  НЕ НАЙДЕНА:", q[:70])
    if missing:
        print("ПРОВАЛ: цитаты не дословны в посеянной истории")
        return 1

    # Мутация: одна цитата испорчена (число изменено) — проверка обязана её опровергнуть.
    mutated = list(QUOTES)
    mutated[0] = mutated[0].replace("27.55", "27.56").replace("16.5", "16.4")
    flagged = [q for q in mutated if canon(q) not in source]
    print(f"после мутации: опровергнуто цитат: {len(flagged)}")
    for q in flagged:
        print("  ОПРОВЕРГНУТА:", q[:70])
    if not flagged:
        print("ПРОВАЛ: проверка не заметила испорченную цитату")
        return 1
    print("OK: проверка зелёная на дословных и красная на испорченной")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
