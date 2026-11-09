#!/usr/bin/env python3
# coding: utf-8
"""
Разовая миграция: подключённые вручную LoRA в манифестах персонажей -> ccs/<ownerKey>/<прежнее имя>.

После фильтра видимости (LoraNamespace.VisibleTo) LoRA вне ccs/<ownerKey>/ не видны и не подключаются.
Парный скрипт для файлов на gpuhost — deploy/comfyui/lora/migrate-manual-loras.sh; применять
ОБА вместе (иначе манифест укажет на несуществующее имя).

Запуск:
  python3 scripts/migrate-lora-manifests.py --username andrey            # dry-run (по умолчанию)
  python3 scripts/migrate-lora-manifests.py --username andrey --apply    # запись + бэкап

Что делает: по data/projects.json берёт серверные проекты ВЛАДЕЛЬЦА (проекты с DeviceId пропускает),
в <RootPath>/characters/*/character.json ищет providers.local.lora без префикса ccs/ и заменяет
значение на ccs/<ownerKey>/<имя>. Запись атомарная (tmp + rename), рядом лежит
character.json.bak-<дата>. Правится только значение поля, остальной текст файла не трогается.
Идемпотентно: уже мигрированное (префикс ccs/) не затрагивается.
"""
import argparse
import datetime
import json
import os
import re
import sys
import uuid

SEGMENT_RE = re.compile(r"^[A-Za-z0-9._-]+$")


def owner_key(owner_id):
    return uuid.UUID(owner_id).hex


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def find_owner(data_dir, username=None, owner_id=None):
    users = load_json(os.path.join(data_dir, "users.json"))
    users = users["users"] if isinstance(users, dict) else users
    if owner_id:
        found = [u for u in users if u["Id"].lower() == owner_id.lower()]
    else:
        found = [u for u in users if u["Username"].lower() == username.lower()]
    if len(found) != 1:
        cands = ", ".join(f'{u["Username"]}={u["Id"]} ({u["Role"]})' for u in users)
        raise SystemExit(f"Владелец не определён однозначно. Кандидаты: {cands}")
    return found[0]["Id"]


def new_lora_name(old, key):
    """None — трогать нельзя (уже в ccs/ или имя недопустимо); иначе новое имя."""
    if old.startswith("ccs/"):
        return None
    parts = old.split("/")
    if not all(p not in ("", ".", "..") and SEGMENT_RE.match(p) for p in parts):
        return None
    return f"ccs/{key}/{old}"


def plan(data_dir, owner_id):
    key = owner_key(owner_id)
    projects = load_json(os.path.join(data_dir, "projects.json"))
    projects = projects["projects"] if isinstance(projects, dict) else projects
    items = []
    for p in projects:
        if p.get("OwnerId", "").lower() != owner_id.lower():
            continue
        if p.get("DeviceId"):
            items.append({"project": p["Name"], "status": "skip-local", "path": p["RootPath"]})
            continue
        chars = os.path.join(p["RootPath"], "characters")
        if not os.path.isdir(chars):
            continue
        for slug in sorted(os.listdir(chars)):
            path = os.path.join(chars, slug, "character.json")
            if not os.path.isfile(path):
                continue
            with open(path, encoding="utf-8") as f:
                text = f.read()
            try:
                old = json.loads(text)["providers"]["local"]["lora"]
            except (KeyError, TypeError, ValueError):
                continue
            if not old:
                continue
            new = new_lora_name(old, key)
            items.append({"project": p["Name"], "character": slug, "path": path, "old": old,
                          "new": new, "status": "replace" if new else "keep"})
    return items


def apply_item(item):
    path = item["path"]
    with open(path, encoding="utf-8") as f:
        text = f.read()
    old_lit = json.dumps(item["old"], ensure_ascii=False)
    new_lit = json.dumps(item["new"], ensure_ascii=False)
    pattern = re.compile(r'("lora"\s*:\s*)' + re.escape(old_lit))
    new_text, n = pattern.subn(lambda m: m.group(1) + new_lit, text, count=1)
    if n != 1:
        raise RuntimeError(f"{path}: поле lora не найдено в тексте")
    if json.loads(new_text)["providers"]["local"]["lora"] != item["new"]:
        raise RuntimeError(f"{path}: после замены манифест не совпал с ожиданием")
    stamp = datetime.date.today().strftime("%Y%m%d")
    bak = f"{path}.bak-{stamp}"
    n = 1
    while os.path.exists(bak):
        n += 1
        bak = f"{path}.bak-{stamp}-{n}"
    with open(bak, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="") as f:
        f.write(new_text)
    os.replace(tmp, path)
    return bak


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data-dir", default="/srv/ccs/data")
    who = ap.add_mutually_exclusive_group(required=True)
    who.add_argument("--username")
    who.add_argument("--owner-id")
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", help="только план (по умолчанию)")
    mode.add_argument("--apply", action="store_true", help="записать изменения с бэкапом")
    args = ap.parse_args(argv)

    owner_id = find_owner(args.data_dir, args.username, args.owner_id)
    key = owner_key(owner_id)
    print(f"Режим: {'apply' if args.apply else 'dry-run'}; владелец: {owner_id}; ownerKey: {key}")
    items = plan(args.data_dir, owner_id)
    changed = 0
    for it in items:
        if it["status"] == "skip-local":
            print(f'ПРОПУСК (локальный проект устройства): {it["project"]}')
        elif it["status"] == "keep":
            print(f'БЕЗ ИЗМЕНЕНИЙ: {it["project"]}/{it["character"]}: {it["old"]}')
        else:
            print(f'ЗАМЕНА: {it["project"]}/{it["character"]}: {it["old"]} -> {it["new"]}\n    {it["path"]}')
            if args.apply:
                print(f"    бэкап: {apply_item(it)}")
            changed += 1
    print(f"--- к замене: {changed} ---")
    if not args.apply:
        print("Ничего не записано (dry-run). Для применения добавь --apply.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
