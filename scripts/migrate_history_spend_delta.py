#!/usr/bin/env python3
"""Разовая идемпотентная миграция history.json: накопленные итоги cost-state → дельты хода.

Продолжение миграций migrate_spend_delta.py (v1) и migrate_spend_delta_v2.py (v2),
которые пересчитывали журналы spend/turns-*.jsonl. Здесь — те же данные, но в
history.json (data/sessions/{csid}/history.json), откуда шапка чата (ChatPanel.tsx,
costStats) складывает totalCostUsd и usage.

Проблема: с 24.09 Claude CLI в result.modelUsage / total_cost_usd возвращает
накопленный итог всей сессии (cost-state в транскрипте), а не расход одного хода.
Шапка складывает все result → цифра завышена в десятки раз.

Правило (то же, что v2):
  * внутри одного чата, по порядку, для result с 24.09 (CUTOFF) до выкатки фикса
    (DEPLOY): значение = разность с предыдущим result;
  * уменьшение (гонка двух процессов CLI) → max(0, …) по каждому полю и по стоимости;
  * базой первого result в окне служит последний result до CUTOFF (если чат начался
    до cutoff); если чат начался после cutoff — база = 0 (первый result = весь снимок);
  * contextTokens, durationMs, numTurns и прочие поля не трогаем;
  * переписываем только записи, созданные ДО выкатки d3be0959e: новые уже дельты,
    второй вычет их обнулит. Граница — по таймстампу записи (effective_ts), не по дате
    коммита.

Идемпотентность: маркер history-spend-delta.done в каталоге data/sessions/.
Повторный запуск — no-op. До правки — копия .bak каждого изменяемого файла.

Запуск:
  python3 scripts/migrate_history_spend_delta.py [SESSIONS_DIR] [--dry] [--deploy 2026-10-13T12:00:00]

  SESSIONS_DIR  — каталог data/sessions/ (по умолчанию /srv/ccs/data/sessions)
  --dry         — только отчёт, без записи
  --deploy      — ISO-время выкатки d3be0959e (граница: записи после не трогаем)
"""
import json
import os
import sys
import shutil
from datetime import datetime, timezone

# ── Параметры ──────────────────────────────────────────────────────────────────

SESSIONS_DIR = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") \
    else "/srv/ccs/data/sessions"
DRY = "--dry" in sys.argv
DEPLOY_ARG = None
for i, a in enumerate(sys.argv):
    if a == "--deploy" and i + 1 < len(sys.argv):
        DEPLOY_ARG = sys.argv[i + 1]

CUTOFF = datetime(2026, 9, 24, tzinfo=timezone.utc)
# Граница выкатки: по умолчанию — «очень далеко» (все записи в окне),
# переопределяется --deploy
DEPLOY = datetime.fromisoformat(DEPLOY_ARG) if DEPLOY_ARG \
    else datetime(2099, 1, 1, tzinfo=timezone.utc)

# Нормализуем timezone: если CUTOFF/DEPLOY naive — делаем aware UTC
if CUTOFF.tzinfo is None:
    CUTOFF = CUTOFF.replace(tzinfo=timezone.utc)
if DEPLOY.tzinfo is None:
    DEPLOY = DEPLOY.replace(tzinfo=timezone.utc)

CUTOFF_MS = int(CUTOFF.timestamp() * 1000)
DEPLOY_MS = int(DEPLOY.timestamp() * 1000)

FIELDS = ["inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens"]
MARKER = os.path.join(SESSIONS_DIR, "history-spend-delta.done")


# ── Утилиты ────────────────────────────────────────────────────────────────────

def getf(v, k):
    """Числовое поле или 0."""
    val = v.get(k) if isinstance(v, dict) else None
    return val if isinstance(val, (int, float)) else 0


def get_usage(r, k):
    """Поле из usage-объекта result-записи."""
    u = r.get("usage")
    if not isinstance(u, dict):
        return 0
    v = u.get(k)
    return v if isinstance(v, (int, float)) else 0


def effective_ts(data, idx):
    """Эффективный таймстамп записи: ближайший предшествующий entry с timestamp."""
    for j in range(idx, -1, -1):
        t = data[j].get("timestamp")
        if t is not None:
            return t
    return None


def load_history(path):
    """Читает history.json (JSON-массив)."""
    if not os.path.exists(path):
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_history(path, data):
    """Атомарная запись: tmp + rename (как JsonFileStore.Save)."""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


# ── Основная логика ────────────────────────────────────────────────────────────

def migrate_one(history_path, csid):
    """
    Мигрирует один history.json. Возвращает (changed_count, new_total_cost, old_total_cost)
    или None, если файл не найден / не изменён.
    """
    data = load_history(history_path)
    if data is None or not isinstance(data, list):
        return None

    # Собираем result-записи с их индексами
    results = []
    for i, entry in enumerate(data):
        if isinstance(entry, dict) and entry.get("kind") == "result":
            ts = effective_ts(data, i)
            results.append((i, entry, ts))

    if not results:
        return None

    # Проверяем: есть ли result в окне [CUTOFF, DEPLOY)?
    in_window = [(i, r, ts) for i, r, ts in results if ts is not None and CUTOFF_MS <= ts < DEPLOY_MS]
    if not in_window:
        return None

    # База: последний result ДО CUTOFF (накопленный итог, с которого CLI пошёл копировать)
    pre_cutoff_last = None
    for i, r, ts in results:
        if ts is not None and ts < CUTOFF_MS:
            pre_cutoff_last = r

    # Проходим по result в хронологическом порядке
    prev_cost = None
    prev_usage = None
    changed = 0
    old_total_cost = 0.0
    new_total_cost = 0.0

    for i, r, ts in results:
        oc = r.get("totalCostUsd")
        ou = {k: get_usage(r, k) for k in FIELDS}

        if ts is None:
            # Нет таймстампа — не трогаем, но обновляем prev (только если oc не None)
            if oc is not None:
                prev_cost = oc
            if any(ou.values()):
                prev_usage = ou
            continue

        old_total_cost += oc or 0.0

        if CUTOFF_MS <= ts < DEPLOY_MS:
            # В окне миграции
            if prev_usage is not None:
                # Дельта от предыдущего
                for k in FIELDS:
                    delta = max(0, ou[k] - prev_usage[k])
                    if delta != ou[k]:
                        if "usage" not in r or not isinstance(r.get("usage"), dict):
                            r["usage"] = {}
                        r["usage"][k] = delta
                if oc is not None and prev_cost is not None:
                    delta_cost = max(0.0, oc - prev_cost)
                    if delta_cost != oc:
                        r["totalCostUsd"] = delta_cost
                    new_total_cost += delta_cost
                elif oc is not None:
                    new_total_cost += oc
                changed += 1
            else:
                # Первый в окне: база = pre-cutoff (если есть) или 0
                if pre_cutoff_last is not None:
                    pc = pre_cutoff_last.get("totalCostUsd")
                    pu = {k: get_usage(pre_cutoff_last, k) for k in FIELDS}
                    if oc is not None and pc is not None:
                        delta_cost = max(0.0, oc - pc)
                        r["totalCostUsd"] = delta_cost
                        new_total_cost += delta_cost
                    else:
                        new_total_cost += oc or 0.0
                    for k in FIELDS:
                        r.setdefault("usage", {})[k] = max(0, ou[k] - pu[k])
                else:
                    # Чат начался после cutoff: первый result = весь снимок, не трогаем
                    new_total_cost += oc or 0.0
                changed += 1
        else:
            # Вне окна (до cutoff или после deploy): не трогаем
            new_total_cost += oc or 0.0

        # Обновляем prev только если oc не None (None = нет данных, не сбрасываем)
        if oc is not None:
            prev_cost = oc
        if any(ou.values()):
            prev_usage = ou

    if changed == 0:
        return None

    # Бэкап + запись
    if not DRY:
        bak = history_path + ".bak"
        if not os.path.exists(bak):
            shutil.copy2(history_path, bak)
        save_history(history_path, data)

    return changed, round(new_total_cost, 4), round(old_total_cost, 4)


def main():
    print(f"Каталог: {SESSIONS_DIR}")
    print(f"CUTOFF:  {CUTOFF.isoformat()}  ({CUTOFF_MS} ms)")
    print(f"DEPLOY:  {DEPLOY.isoformat()}  ({DEPLOY_MS} ms)")
    print(f"Режим:   {'dry-run' if DRY else 'запись'}")
    print()

    if os.path.exists(MARKER) and not DRY:
        print(f"Маркер {MARKER} уже есть — миграция no-op")
        return

    dirs = sorted(d for d in os.listdir(SESSIONS_DIR)
                   if os.path.isdir(os.path.join(SESSIONS_DIR, d)))

    total_changed_files = 0
    total_changed_records = 0
    total_old_cost = 0.0
    total_new_cost = 0.0
    per_file = []

    for d in dirs:
        hp = os.path.join(SESSIONS_DIR, d, "history.json")
        if not os.path.exists(hp):
            continue
        result = migrate_one(hp, d)
        if result is not None:
            changed, new_cost, old_cost = result
            total_changed_files += 1
            total_changed_records += changed
            total_old_cost += old_cost
            total_new_cost += new_cost
            per_file.append((d, changed, old_cost, new_cost))

    # Отчёт
    print(f"Файлов изменено: {total_changed_files}")
    print(f"Записей переписано: {total_changed_records}")
    print(f"Суммарная стоимость: ${total_old_cost:.2f} → ${total_new_cost:.2f}"
          f"  (снижение {total_old_cost / total_new_cost:.0f}x)" if total_new_cost > 0 else "")
    print()

    if per_file:
        print("По файлам (топ-20 по стоимости после):")
        for d, changed, old_c, new_c in sorted(per_file, key=lambda x: -x[3])[:20]:
            print(f"  {d}: {changed} записей, ${old_c:.2f} → ${new_c:.2f}")

    # Маркер
    if not DRY and total_changed_files > 0:
        with open(MARKER, "w", encoding="utf-8") as f:
            f.write(f"history-spend-delta done {datetime.now(timezone.utc).isoformat()}\n")
        print(f"\nМаркер: {MARKER}")


if __name__ == "__main__":
    main()
