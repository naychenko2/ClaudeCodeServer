#!/usr/bin/env python3
# coding: utf-8
"""
Разовая миграция: заполнить пустой DatasetId в persona-memory.json
из существующих датасетов Dify (снимает CreateDatasetAsync 409).

Запуск:
  # сухой прогон — план в файл, без записи в store
  python persona-memory-fill-dataset-ids.py --dry-run

  # применить: backup + запись + отчёт
  python persona-memory-fill-dataset-ids.py --apply

Идемпотентно: записи с уже заполненным DatasetId пропускаются.
Бэкап: C:\\ClaudeData\\prod\\persona-memory.json.bak-YYYYMMDD-HHMMSS
План/отчёт: persona-memory-fill-dataset-ids-plan.json рядом со скриптом.

Правила резолва (см. задачу 35470f1e):
1. Точное совпадение {user}:persona:{handle} предпочитается всегда.
2. Если точного нет — ищем {user}:persona:{handle}-N (суффикс -цифра);
   выбираем тот, у кого (document_count DESC, updated_at DESC).
3. Дубли в смысле «одно имя — несколько датасетов» в Dify не наблюдаются
   (проверено dry-run-ом), но если появятся — берём по тому же правилу.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

DATA_DIR = Path(r"C:\ClaudeData\prod")
PROD_CONFIG = Path(r"C:\ClaudeServer\prod\appsettings.Local.json")
PERSONA_MEMORY = DATA_DIR / "persona-memory.json"
PERSONAS = DATA_DIR / "personas.json"
USERS = DATA_DIR / "users.json"

# {user}:persona:{handle}  →  ожидаемая логика имени датасета Dify.
# Handle может содержать буквы/цифры/дефис/подчёркивание; суффикс дубликата
# добавлялся как "-2", "-3" и т.п. — учитываем при матчинге.
HANDLE_RE = re.compile(r"^[A-Za-z0-9_\-]+$")
SUFFIX_RE = re.compile(r"^(?P<base>.+)-(?P<n>\d+)$")


def load_json(path: Path) -> object:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def save_json(path: Path, data: object) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def fetch_dify_datasets(api_url: str, api_key: str) -> list[dict]:
    """GET {api}/datasets с пагинацией."""
    datasets: list[dict] = []
    page = 1
    while True:
        url = f"{api_url.rstrip('/')}/datasets?page={page}&limit=100"
        req = urllib.request.Request(url, headers={"Authorization": f"Bearer {api_key}"})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            raise SystemExit(f"Dify API {url} → HTTP {exc.code}: {exc.read().decode('utf-8', 'replace')}") from exc
        data = payload.get("data") or []
        if not data:
            break
        datasets.extend(data)
        if not payload.get("has_more"):
            break
        page += 1
    return datasets


def build_dify_index(datasets: list[dict]) -> dict:
    """
    Возвращает индекс:
      exact[name]                 = dataset
      suffix_groups[base_name]    = list[dataset] (только persona-имена с -N)
      person_by_user[user]        = set(dataset_id) — для sanity-check.
    """
    exact: dict[str, dict] = {}
    suffix_groups: dict[str, list[dict]] = defaultdict(list)
    person_by_user: dict[str, set[str]] = defaultdict(set)
    for d in datasets:
        name = d["name"]
        exact[name] = d
        parts = name.split(":", 2)
        if len(parts) != 3:
            continue
        user, kind, handle = parts
        if kind != "persona":
            continue
        person_by_user[user].add(d["id"])
        m = SUFFIX_RE.match(handle)
        if m:
            base = f"{user}:persona:{m.group('base')}"
            suffix_groups[base].append(d)
    return {
        "exact": exact,
        "suffix_groups": suffix_groups,
        "person_by_user": person_by_user,
    }


def resolve_for_persona(
    user: str,
    handle: str,
    index: dict,
) -> tuple[dict | None, str]:
    """
    Возвращает (dataset, reason):
      reason ∈ {"exact", "suffix-max-docs-updated", "none"}.
    """
    target = f"{user}:persona:{handle}"
    d = index["exact"].get(target)
    if d:
        return d, "exact"
    candidates = index["suffix_groups"].get(target, [])
    if candidates:
        best = max(
            candidates,
            key=lambda x: (x.get("document_count") or 0, x.get("updated_at") or 0),
        )
        return best, "suffix-max-docs-updated"
    return None, "none"


def build_plan(
    pm: dict,
    personas: list[dict],
    users: list[dict],
    datasets: list[dict],
    index: dict,
) -> dict:
    users_by_id = {u["Id"]: u for u in users}
    personas_by_id = {p["Id"]: p for p in personas}

    will_update: list[dict] = []
    skipped_already_filled: list[dict] = []
    no_owner_user: list[dict] = []
    no_match_in_dify: list[dict] = []
    not_in_personas: list[dict] = []

    for persona_id, entry in pm.items():
        current = entry.get("DatasetId") or ""
        if current:
            skipped_already_filled.append({"persona_id": persona_id, "dataset_id": current})
            continue
        persona = personas_by_id.get(persona_id)
        if persona is None:
            not_in_personas.append({"persona_id": persona_id})
            continue
        owner = users_by_id.get(persona["OwnerId"])
        if owner is None:
            no_owner_user.append(
                {
                    "persona_id": persona_id,
                    "owner_id": persona["OwnerId"],
                    "handle": persona["Handle"],
                }
            )
            continue
        user = owner["Username"]
        handle = persona["Handle"]
        if not HANDLE_RE.match(handle):
            no_match_in_dify.append(
                {
                    "persona_id": persona_id,
                    "handle": handle,
                    "reason": "handle-содержит-символы-вне-белого-списка",
                }
            )
            continue
        dataset, reason = resolve_for_persona(user, handle, index)
        if dataset is None:
            no_match_in_dify.append(
                {
                    "persona_id": persona_id,
                    "user": user,
                    "handle": handle,
                    "expected_name": f"{user}:persona:{handle}",
                }
            )
            continue
        will_update.append(
            {
                "persona_id": persona_id,
                "user": user,
                "handle": handle,
                "dataset_id": dataset["id"],
                "dataset_name": dataset["name"],
                "match_rule": reason,
                "document_count": dataset.get("document_count"),
                "word_count": dataset.get("word_count"),
                "updated_at": dataset.get("updated_at"),
            }
        )

    return {
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "totals": {
            "persona_memory_entries": len(pm),
            "datasets_fetched": len(datasets),
            "will_update": len(will_update),
            "skipped_already_filled": len(skipped_already_filled),
            "no_match_in_dify": len(no_match_in_dify),
            "not_in_personas": len(not_in_personas),
            "no_owner_user": len(no_owner_user),
        },
        "by_rule": {
            rule: [r for r in will_update if r["match_rule"] == rule]
            for rule in ("exact", "suffix-max-docs-updated")
        },
        "will_update": will_update,
        "skipped_already_filled": skipped_already_filled,
        "no_match_in_dify": no_match_in_dify,
        "not_in_personas": not_in_personas,
        "no_owner_user": no_owner_user,
    }


def print_summary(plan: dict) -> None:
    t = plan["totals"]
    print("=== DRY-RUN SUMMARY ===", flush=True)
    print(f"persona-memory.json entries:        {t['persona_memory_entries']}")
    print(f"Dify datasets fetched:              {t['datasets_fetched']}")
    print(f"will update:                        {t['will_update']}")
    print(f"skipped (already filled):           {t['skipped_already_filled']}")
    print(f"no match in Dify:                   {t['no_match_in_dify']}")
    print(f"not found in personas.json:         {t['not_in_personas']}")
    print(f"owner user not in users.json:       {t['no_owner_user']}")
    print()
    by_rule = plan["by_rule"]
    print(f"  by rule 'exact':                  {len(by_rule.get('exact', []))}")
    print(f"  by rule 'suffix-max-docs-updated':{len(by_rule.get('suffix-max-docs-updated', []))}")
    if by_rule.get("suffix-max-docs-updated"):
        print()
        print("Suffix resolutions (проверь руками — это артефакты прошлых миграций):")
        for r in by_rule["suffix-max-docs-updated"]:
            print(
                f"  {r['user']}:persona:{r['handle']} → {r['dataset_name']} "
                f"(docs={r['document_count']}, updated={r['updated_at']})"
            )


def apply_plan(plan: dict, pm: dict, backup_path: Path) -> dict:
    """Записывает новые DatasetId, сохраняя всё остальное. Идемпотентно."""
    applied: list[dict] = []
    for row in plan["will_update"]:
        persona_id = row["persona_id"]
        new_id = row["dataset_id"]
        entry = pm[persona_id]
        if entry.get("DatasetId"):
            continue  # перестраховка
        entry["DatasetId"] = new_id
        applied.append(row)
    return {"applied": applied}


def backup_store(path: Path) -> Path:
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    dst = path.with_suffix(path.suffix + f".bak-{stamp}")
    shutil.copy2(path, dst)
    return dst


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dry-run", action="store_true", help="Только план и сводка, ничего не пишем.")
    ap.add_argument("--apply", action="store_true", help="Backup + запись + отчёт.")
    args = ap.parse_args()
    if not args.dry_run and not args.apply:
        args.dry_run = True  # безопасный дефолт

    cfg = load_json(PROD_CONFIG)
    dify = cfg["Dify"]
    api_url = dify["ApiUrl"]
    api_key = dify["ApiKey"]

    print(f"Reading {PERSONA_MEMORY} …", flush=True)
    pm = load_json(PERSONA_MEMORY)
    print(f"Reading {PERSONAS} …", flush=True)
    personas = load_json(PERSONAS)
    print(f"Reading {USERS} …", flush=True)
    users_obj = load_json(USERS)
    users = users_obj["users"] if isinstance(users_obj, dict) and "users" in users_obj else users_obj
    print(f"Fetching Dify datasets from {api_url} …", flush=True)
    datasets = fetch_dify_datasets(api_url, api_key)
    print(f"Fetched {len(datasets)} datasets.", flush=True)

    index = build_dify_index(datasets)
    plan = build_plan(pm, personas, users, datasets, index)
    print_summary(plan)

    here = Path(__file__).resolve().parent
    plan_path = here / "persona-memory-fill-dataset-ids-plan.json"
    save_json(plan_path, plan)
    print(f"\nPlan written to: {plan_path}", flush=True)

    if args.dry_run:
        return 0

    # ---- APPLY ----
    backup_path = backup_store(PERSONA_MEMORY)
    print(f"\nBackup created: {backup_path}", flush=True)
    summary = apply_plan(plan, pm, backup_path)
    save_json(PERSONA_MEMORY, pm)
    print(f"Updated store written: {PERSONA_MEMORY}", flush=True)
    print(f"Applied changes: {len(summary['applied'])}", flush=True)

    # Пересчитать «пустые после»
    still_empty = sum(1 for e in pm.values() if not e.get("DatasetId"))
    print(f"Empty DatasetId after migration: {still_empty}", flush=True)

    report_path = here / "persona-memory-fill-dataset-ids-report.json"
    save_json(
        report_path,
        {
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "backup": str(backup_path),
            "applied": summary["applied"],
            "totals": plan["totals"],
            "still_empty_after": still_empty,
        },
    )
    print(f"Report written to: {report_path}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
