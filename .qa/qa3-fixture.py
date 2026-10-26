#!/usr/bin/env python3
"""QA3 — фикстуры для перепроверки голоса оркестратора на фичах без идущих задач (после B14).

Стенд: бэк из worktree на :5563, дев-витэ на :5193 (BACKEND_PORT=5563).
Скрипт создаёт проект, чат-оркестратор и фичи-фикстуры в состояниях
waiting / paused / queued / not_started, ставит им связь «оркестратор»
(ExecutorLaunchedBySessionId эпика = чат) и печатает id в .qa/qa3-fixture-ids.json.

Запуск:  python3 .qa/qa3-fixture.py
"""

import json
import os
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = os.environ.get("QA_BASE", "http://127.0.0.1:5563")
HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = None


def call(method, path, body=None, headers=None):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
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
    # Слот моделей владельца: без них ход уходит в родной Claude и падает auth_failed.
    st, _ = call("PUT", "/api/me/model-tiers", {
        "strong": "MiniMax-M3.1-Flash[1m]",
        "medium": "MiniMax-M3.1-Flash[1m]",
        "weak": "MiniMax-M3.1-Flash[1m]"})
    out["tiers"] = st

    st, proj = call("POST", "/api/projects", {
        "name": "QA3 голос фичи", "rootPath": "/home/an/qa-fs3/proj", "createDirectory": True})
    out["project"] = {"status": st, "id": (proj or {}).get("id")}
    pid = (proj or {}).get("id")
    if not pid:
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return

    st, personas = call("GET", "/api/personas")
    persona = personas[0]
    out["persona"] = {"id": persona["id"], "name": persona["name"]}

    # Чат-оркестратор: он же запускает исполнителя (связь «orchestrator»).
    st, chat = call("POST", f"/api/projects/{pid}/sessions",
                    {"name": "Оркестратор QA3", "personaId": persona["id"]})
    if st not in (200, 201):
        raise SystemExit(f"чат оркестратора не создался: {st} {chat}")
    out["orchestrator_chat"] = chat["id"]

    def task(title, **kw):
        body = {"title": title}
        body.update(kw)
        st, t = call("POST", f"/api/projects/{pid}/tasks", body)
        if st != 200:
            raise SystemExit(f"задача «{title}» не создалась: {st} {t}")
        return t["id"]

    def feature(title, step_title, step_ref, lane="фронт"):
        """Фича + её шаг (стадия «prod» активна: есть задача, стадия не пройдена)."""
        fid = task(title, role="feature")
        sid = task(step_title, role="step", featureId=fid, stepRef=step_ref, lane=lane)
        return fid, sid

    f = {}

    # 1. Основная фича: not_started → running → idle; на ней же смена стадии и пауза.
    f["main"], f["main_step"] = feature("Фича: голос без идущих задач", "Шаг основной фичи", "S1")

    # 2. «Ждёт вас»: у задачи отметка ожидания человека (AwaitingHumanSince).
    f["waiting"], f["waiting_step"] = feature("Фича: ждёт вас", "Шаг ждущей фичи", "S2", "бэк")

    # 3. Остановленная: живой исполнитель + кнопка «Стоп» (сценарий — qa3-live.py).
    f["stopped"], f["stopped_step"] = feature("Фича: исполнитель встал", "Шаг остановленной фичи", "S3")

    # 4. На паузе (удержание manual).
    f["paused"], f["paused_step"] = feature("Фича: на паузе", "Шаг фичи на паузе", "S4")

    # 5. В очереди (удержание queue).
    f["queued"], f["queued_step"] = feature("Фича: в очереди", "Шаг фичи в очереди", "S5", "бэк")

    # Связь «оркестратор» на всех фичах: чат запускал исполнителя фичи → право писать голос.
    for key in ("main", "waiting", "stopped", "paused", "queued"):
        st, t = call("PUT", f"/api/tasks/{f[key]}", {"executorLaunchedBySessionId": chat["id"]})
        if st != 200:
            raise SystemExit(f"связь оркестратора не поставилась ({key}): {st} {t}")

    # «Ждёт вас»: отметка ожидания человека на шаге (живая ручка трекера).
    since = (datetime.now(timezone.utc) - timedelta(minutes=12)).isoformat().replace("+00:00", "Z")
    st, t = call("PUT", f"/api/tasks/{f['waiting_step']}",
                 {"awaitingHumanSince": since, "awaitingHumanReason": "нужен выбор направления"})
    if st != 200:
        raise SystemExit(f"отметка ожидания не поставилась: {st} {t}")
    out["waiting_since"] = since

    # Пауза и очередь — удержание человека (hold).
    st, t = call("POST", f"/api/tasks/features/{f['paused']}/hold", {"reason": "manual"})
    out["hold_paused"] = {"status": st, "body": t}
    st, t = call("POST", f"/api/tasks/features/{f['queued']}/hold", {"reason": "queue"})
    out["hold_queued"] = {"status": st, "body": t}

    out["features"] = f
    out["project_id"] = pid
    with open(os.path.join(HERE, "qa3-fixture-ids.json"), "w") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=2)
    print(json.dumps(out, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
