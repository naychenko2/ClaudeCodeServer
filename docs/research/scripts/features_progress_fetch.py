#!/usr/bin/env python3
"""Выгрузка данных о фичах из REST трекера в каталог-кэш (JSON).

Источник — HTTP API сервера (а не data/*.json): GET /api/tasks/features,
GET /api/tasks/features/{id}?eventsLimit=all и GET /api/tasks/{id} для задач-планов
(в ответе фичи у плана planSteps обнулены облегчением, полная карточка — только по id).

Переменные окружения:
  CCS_API    адрес сервера (по умолчанию $CCS_HOOK_API или http://localhost:80)
  CCS_TOKEN  JWT/служебный токен (по умолчанию $CCS_HOOK_TOKEN)

Запуск:  python3 features_progress_fetch.py [--out /tmp/fpa-cache]
Дальше:  python3 features_progress_analyze.py --cache /tmp/fpa-cache --report ../features-progress-accuracy-2026-11.md
"""
import argparse
import json
import os
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="/tmp/fpa-cache")
    args = ap.parse_args()

    api = (os.environ.get("CCS_API") or os.environ.get("CCS_HOOK_API") or "http://localhost:80").rstrip("/")
    token = os.environ.get("CCS_TOKEN") or os.environ.get("CCS_HOOK_TOKEN")
    if not token:
        print("нужен CCS_TOKEN (или CCS_HOOK_TOKEN)", file=sys.stderr)
        return 2

    def get(path: str):
        req = urllib.request.Request(api + path, headers={"Authorization": f"Bearer {token}"})
        with urllib.request.urlopen(req, timeout=120) as r:
            return json.load(r)

    os.makedirs(os.path.join(args.out, "features"), exist_ok=True)
    os.makedirs(os.path.join(args.out, "plans"), exist_ok=True)

    index = get("/api/tasks/features")
    with open(os.path.join(args.out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False)
    ids = [x["featureId"] for x in index]
    print(f"фич в индексе: {len(ids)}")

    def one(fid: str):
        full = get(f"/api/tasks/features/{fid}?eventsLimit=all")
        with open(os.path.join(args.out, "features", fid + ".json"), "w", encoding="utf-8") as f:
            json.dump(full, f, ensure_ascii=False)
        plan_ids = [p["id"] for p in full["run"].get("plan", [])]
        for pid in plan_ids:
            card = get(f"/api/tasks/{pid}")
            with open(os.path.join(args.out, "plans", pid + ".json"), "w", encoding="utf-8") as f:
                json.dump(card, f, ensure_ascii=False)
        return fid, len(plan_ids)

    with ThreadPoolExecutor(max_workers=4) as pool:
        total_plans = 0
        for fid, n in pool.map(one, ids):
            total_plans += n
    print(f"планов выгружено: {total_plans}; каталог: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
