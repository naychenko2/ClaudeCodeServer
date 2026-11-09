#!/usr/bin/env python3
"""QA3 — живой исполнитель: запуск из чата-оркестратора, кнопка «Стоп», отказ исполнителю.

Режимы:
  stop <ключ_фичи>   — запуск исполнителя шага и «Стоп» человека через боевую ручку доски.
  run  <ключ_фичи>   — запуск исполнителя шага; ждёт конца хода (без «Стоп»).
  refuse <ключ_фичи> — MCP-вызов tasks_update с featureNote ИЗ СЕССИИ ИСПОЛНИТЕЛЯ (ожидаем отказ).

Запуск:  python3 .qa/qa3-live.py stop stopped
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("QA_BASE", "http://127.0.0.1:5563")
HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = None

PROMPT = "Ответь одним словом: готово. Файлы не трогай, задач не создавай."


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


def brief_of(fid):
    st, page = call("GET", f"/api/tasks/features/{fid}")
    brief = page.get("brief") or {}
    run = page.get("run") or {}
    active = [s.get("key") for s in (run.get("stages") or []) if s.get("state") == "active"]
    return {
        "state": run.get("state"), "kind": brief.get("kind"), "activeStage": active,
        "now": brief.get("now"), "reason": brief.get("reason"),
        "note": brief.get("note"), "queueWaitMinutes": brief.get("queueWaitMinutes"),
    }


def mcp_voice(session_id, feature_id, text, rpc_id=1):
    body = {"jsonrpc": "2.0", "id": rpc_id, "method": "tools/call",
            "params": {"name": "tasks_update",
                       "arguments": {"id": feature_id, "featureNote": text}}}
    return call("POST", f"/mcp/tasks/{session_id}", body)


def main():
    login()
    mode = sys.argv[1]
    key = sys.argv[2]
    ids = json.load(open(os.path.join(HERE, "qa3-fixture-ids.json")))
    f = ids["features"]
    chat = ids["orchestrator_chat"]
    persona = ids["persona"]["id"]

    if mode == "refuse":
        # Отказ исполнителю: голос пишет СЕССИЯ ИСПОЛНИТЕЛЯ (linkedSessionId задачи).
        fid = f[key]
        st, t = call("GET", f"/api/tasks/{f[key + '_step']}")
        ex_session = t.get("linkedSessionId")
        print(f"сессия исполнителя: {ex_session}")
        st, body = mcp_voice(ex_session, fid, "Голос исполнителя: попытка записи")
        res = (body or {}).get("result") or {}
        print(f"статус {st} isError={res.get('isError')}")
        for c in (res.get("content") or []):
            if c.get("type") == "text":
                print("текст:", c.get("text", "")[:300])
        st, page = call("GET", f"/api/tasks/features/{fid}")
        print("note после попытки:", json.dumps((page.get("brief") or {}).get("note"), ensure_ascii=False))
        return

    step = f[key + "_step"]
    fid = f[key]
    # Исполнитель: персна, слот strong, задача под claude.
    st, t = call("PUT", f"/api/tasks/{step}", {
        "personaId": persona, "assignee": "claude", "modelTier": "strong", "description": PROMPT})
    if st != 200:
        raise SystemExit(f"подготовка шага не удалась: {st} {t}")

    st, res = call("POST", f"/api/tasks/{step}/execute", None, {"X-Caller-Session-Id": chat})
    print(f"запуск исполнителя: статус {st}")
    if st != 200:
        print(json.dumps(res, ensure_ascii=False)[:500])
        return
    print("  executorLaunchedBySessionId:", res.get("executorLaunchedBySessionId"))

    session_id = None
    for _ in range(40):
        st, t = call("GET", f"/api/tasks/{step}")
        if t and t.get("linkedSessionId"):
            session_id = t["linkedSessionId"]
            break
        time.sleep(1)
    print("  сессия исполнителя:", session_id)
    if not session_id:
        print("  сессия не поднялась")
        return

    time.sleep(5)
    print("  сводка во время хода:", json.dumps(brief_of(fid), ensure_ascii=False))

    if mode == "stop":
        st, _ = call("POST", f"/api/board/agents/{session_id}/interrupt")
        print(f"  «Стоп»: статус {st}")
        time.sleep(3)
        st, t = call("GET", f"/api/tasks/{step}")
        print("  задача:", {k: t.get(k) for k in
                             ("status", "executorStopReason", "executorStoppedAt", "claudeStartedAt")})
        print("  сводка после «Стоп»:", json.dumps(brief_of(fid), ensure_ascii=False))
    elif mode == "launch":
        print("  исполнитель идёт; сводка:", json.dumps(brief_of(fid), ensure_ascii=False))
    else:
        for _ in range(60):
            st, t = call("GET", f"/api/tasks/{step}")
            if t.get("claudeResult") or t.get("executorStoppedAt"):
                break
            time.sleep(2)
        st, t = call("GET", f"/api/tasks/{step}")
        print("  задача:", {k: t.get(k) for k in ("status", "claudeStartedAt", "executorStoppedAt")})
        print("  сводка после хода:", json.dumps(brief_of(fid), ensure_ascii=False))


if __name__ == "__main__":
    main()
