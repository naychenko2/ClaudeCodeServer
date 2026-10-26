#!/usr/bin/env python3
"""QA2, фаза 2 — статусы, персоны, живой исполнитель и «Стоп» человека.

Ставит задачам-стадиям статус inProgress (иначе фича не «идёт» и строка «Дальше»
пуста), назначает персону-исполнителя и поднимает ЖИВОГО исполнителя фичи
«остановлена» из чата-оркестратора: запуск задачи с заголовком вызывающей сессии
делает чат оркестратором фичи, а чат исполнителя — исполнителем. Тот же запуск
даёт живую сессию, которую потом прерывает кнопка «Стоп».

Запуск:  python3 .qa/qa2-phase2.py
"""

import json
import os
import time
import urllib.error
import urllib.request

BASE = os.environ.get("QA_BASE", "http://127.0.0.1:5563")
HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = None


def call(method, path, body=None, headers=None):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    if body is not None:
        req.data = json.dumps(body).encode("utf-8")
        req.add_header("Content-Type", "application/json")
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


def main():
    global TOKEN
    req = urllib.request.Request(
        BASE + "/api/auth/login", method="POST",
        data=json.dumps({"username": "admin", "password": "12345"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        TOKEN = json.loads(r.read().decode())["token"]

    ids = json.load(open(os.path.join(HERE, "qa2-fixture-ids.json")))
    f = ids["features"]
    pid = ids["project"]["id"]

    st, personas = call("GET", "/api/personas")
    persona = personas[0]
    out = {"persona": {"id": persona["id"], "name": persona["name"]}}

    def put_task(tid, **kw):
        st, t = call("PUT", f"/api/tasks/{tid}", kw)
        if st != 200:
            raise SystemExit(f"PUT {tid} {kw} → {st} {t}")
        return t

    # Идущие фичи: шаг в работе → kind=running, стадия активна, «Дальше» непустая.
    for key in ("cut", "plan", "prod", "review", "qa"):
        put_task(f[f"stage_{key}_cur"], status="inProgress", personaId=persona["id"])
        put_task(f[f"stage_{key}_next"], personaId=persona["id"])

    # Свежая и «3 ч назад»: шаг в работе (время старта ставит правка времени).
    put_task(f["fresh_step"], status="inProgress", personaId=persona["id"])
    put_task(f["old3h_step"], status="inProgress", personaId=persona["id"])

    # ── Живой исполнитель фичи «остановлена» ────────────────────────────────────
    put_task(f["stopped_step"], personaId=persona["id"], modelTier="strong",
             assignee="claude", stepRef="S3", lane="фронт")
    st, chat = call("POST", f"/api/projects/{pid}/sessions",
                    {"name": "Оркестратор сводки", "personaId": persona["id"]})
    if st not in (200, 201):
        raise SystemExit(f"чат оркестратора не создался: {st} {chat}")
    out["orchestrator_chat"] = chat["id"]

    st, res = call("POST", f"/api/tasks/{f['stopped_step']}/execute", None,
                   {"X-Caller-Session-Id": chat["id"]})
    out["execute"] = {"status": st, "result": res}
    if st != 200:
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return

    # Ждём сессию исполнителя и первые признаки хода.
    session_id = None
    for _ in range(40):
        st, t = call("GET", f"/api/tasks/{f['stopped_step']}")
        if t and t.get("linkedSessionId"):
            session_id = t["linkedSessionId"]
            break
        time.sleep(1)
    out["executor_session"] = session_id
    if not session_id:
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return

    # «Стоп» человека — боевая ручка доски.
    time.sleep(6)
    st, _ = call("POST", f"/api/board/agents/{session_id}/interrupt")
    out["interrupt"] = st
    time.sleep(2)
    st, t = call("GET", f"/api/tasks/{f['stopped_step']}")
    out["stopped_task"] = {k: t.get(k) for k in
                            ("status", "executorStopReason", "executorStoppedAt",
                             "claudeStartedAt", "executorLaunchedBySessionId")}

    with open(os.path.join(HERE, "qa2-fixture-ids.json"), "w") as fh:
        json.dump({**ids, "phase2": out}, fh, ensure_ascii=False, indent=2)
    print(json.dumps(out, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
