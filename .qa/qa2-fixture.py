#!/usr/bin/env python3
"""QA2 — фикстуры фичи «Сводка состояния фичи» для повторной приёмки на стенде.

Стенд: бэк из worktree на :5563, дев-витэ на :5193 (BACKEND_PORT=5563).
Скрипт создаёт проект и набор фич-фикстур REST'ом и печатает их id в JSON
(файл .qa/qa2-fixture-ids.json) — по нему потом ставятся правки времени
(правка tasks.json при остановленном бэке) и проверяется DOM.

Запуск:  python3 .qa/qa2-fixture.py
"""

import json
import os
import urllib.error
import urllib.request

BASE = os.environ.get("QA_BASE", "http://127.0.0.1:5563")
HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = None


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        req.add_header("Content-Type", "application/json")
    req.data = data
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read().decode("utf-8")
            return r.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8")
        try:
            parsed = json.loads(raw) if raw.strip() else None
        except json.JSONDecodeError:
            parsed = raw
        return e.code, parsed


def login():
    global TOKEN
    req = urllib.request.Request(
        BASE + "/api/auth/login", method="POST",
        data=json.dumps({"username": "admin", "password": "12345"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        TOKEN = json.loads(r.read().decode())["token"]


def main():
    login()
    out = {}

    # Фиче нужен флаг (dark launch) — иначе маршрут схлопывается.
    st, _ = call("PUT", "/api/feature-flags/features-overview", {"enabled": True})
    out["flag"] = st
    # Слоты моделей владельца: без них ход уходит в родной Claude и падает auth_failed.
    st, _ = call("PUT", "/api/me/model-tiers", {
        "strong": "MiniMax-M3.1-Flash[1m]",
        "medium": "MiniMax-M3.1-Flash[1m]",
        "weak": "MiniMax-M3.1-Flash[1m]"})
    out["tiers"] = st

    st, proj = call("POST", "/api/projects", {
        "name": "QA сводка фичи", "rootPath": "/home/an/qa-fs/proj", "createDirectory": True})
    out["project"] = {"status": st, "id": (proj or {}).get("id")}
    pid = (proj or {}).get("id")
    if not pid:
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return

    def task(title, **kw):
        body = {"title": title}
        body.update(kw)
        st, t = call("POST", f"/api/projects/{pid}/tasks", body)
        if st != 200:
            raise SystemExit(f"задача «{title}» не создалась: {st} {t}")
        return t["id"]

    def feature(title):
        return task(title, role="feature")

    # ── Фикстуры ────────────────────────────────────────────────────────────────
    f = {}

    # 1. Свежая фича: события только что → «обновлено только что».
    f["fresh"] = feature("Фича: свежая")
    f["fresh_step"] = task("Шаг свежей фичи", role="step", featureId=f["fresh"],
                           stepRef="F1", lane="фронт")

    # 2. Фича «3 ч назад»: те же события, но сдвинутые назад (правка tasks.json).
    f["old3h"] = feature("Фича: три часа назад")
    f["old3h_step"] = task("Шаг фичи трёхчасовой", role="step", featureId=f["old3h"],
                           stepRef="F2", lane="бэк")

    # 3. Закрытая фича (давно) — самая длинная строка «обновлено 24.10, 18:06».
    f["closed"] = feature("Фича: закрыта давно")
    f["closed_step"] = task("Шаг закрытой фичи", role="step", featureId=f["closed"], stepRef="F3")

    # 4. Стадии для строки «Дальше» (F16): активная стадия + следующая.
    stages = [
        ("cut", "cut", "Разрез", "cut"),
        ("plan", "plan", "План", "plan"),
        ("prod", "step", "Производство", "prod"),
        ("review", "review", "Ревью", "review"),
        ("qa", "qa", "Проверка", "qa"),
    ]
    for key, role, title, _ in stages:
        fid = feature(f"Фича: стадия {title}")
        f[f"stage_{key}"] = fid
        f[f"stage_{key}_cur"] = task(f"Задача стадии {title}", role=role, featureId=fid,
                                     stepRef="S1", lane="фронт")
        # Следующая стадия — чтобы строка «Дальше» была непустой.
        nxt = {"cut": "plan", "plan": "step", "prod": "review", "review": "qa", "qa": "final"}[key]
        nrole = {"plan": "plan", "step": "step", "review": "review", "qa": "qa", "final": "final"}[nxt]
        f[f"stage_{key}_next"] = task(f"Задача после {title}", role=nrole, featureId=fid,
                                      stepRef="S2", lane="бэк")

    # 5. Остановка исполнителя: задача с признаком «исполнитель встал».
    f["stopped"] = feature("Фича: исполнитель встал")
    f["stopped_step"] = task("Задача с остановленным исполнителем", role="step", featureId=f["stopped"],
                              stepRef="S3", lane="фронт")

    # 6. Документы и макеты (регресс M7/D2).
    f["docs"] = feature("Фича: документы и макеты")
    f["docs_design"] = task("Задача дизайна", role="design", featureId=f["docs"],
                            description="# Макет карточки сводки\n\nТекст макета для проверки раздела.")
    f["docs_plan"] = task("Задача плана", role="plan", featureId=f["docs"],
                          description="# План реализации сводки\n\nТекст плана.",
                          linkedFiles=[
                              "docs/mockups/feature-summary-1.html",
                              "docs/adr/ADR-031.md",
                              "notes/design-note.md",
                          ])

    out["features"] = f
    with open(os.path.join(HERE, "qa2-fixture-ids.json"), "w") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=2)
    print(json.dumps(out, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
