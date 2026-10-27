#!/usr/bin/env python3
"""QA3 — запись голоса фичи оркестратором (MCP tasks_update) и человеком (REST PUT note).

Пишет голос в каждую переданную фичу и печатает ответ API: brief.note (stale, stageAtWrite).

Запуск:  python3 .qa/qa3-voice.py <ключ_фичи> [текст] [--rest]
"""

import json
import os
import sys
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


def mcp_voice(session_id, feature_id, text, rpc_id=1):
    """MCP-over-HTTP: POST /mcp/tasks/{sessionId}, JSON-RPC tools/call tasks_update."""
    body = {
        "jsonrpc": "2.0",
        "id": rpc_id,
        "method": "tools/call",
        "params": {
            "name": "tasks_update",
            "arguments": {"id": feature_id, "featureNote": text},
        },
    }
    return call("POST", f"/mcp/tasks/{session_id}", body)


def main():
    login()
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    use_rest = "--rest" in sys.argv
    key = args[0]
    text = args[1] if len(args) > 1 else f"Голос оркестратора: {key}"

    ids = json.load(open(os.path.join(HERE, "qa3-fixture-ids.json")))
    fid = ids["features"][key]
    chat = ids["orchestrator_chat"]

    if use_rest:
        st, body = call("PUT", f"/api/tasks/features/{fid}/note", {"text": text})
        print(f"[REST PUT note] {key}: статус {st}")
        print("  ответ:", json.dumps(body, ensure_ascii=False)[:300])
    else:
        st, body = mcp_voice(chat, fid, text)
        print(f"[MCP tasks_update] {key}: статус {st}")
        res = (body or {}).get("result") or {}
        is_error = res.get("isError")
        content = res.get("content") or []
        texts = [c.get("text", "") for c in content if c.get("type") == "text"]
        print(f"  isError={is_error}")
        for t in texts:
            print("  текст:", t[:400])

    st, page = call("GET", f"/api/tasks/features/{fid}")
    brief = page.get("brief") or {}
    run = page.get("run") or {}
    active = [s.get("key") for s in (run.get("stages") or []) if s.get("state") == "active"]
    print(f"  → state={run.get('state')} kind={brief.get('kind')} activeStage={active}")
    print(f"  → note={json.dumps(brief.get('note'), ensure_ascii=False)}")


if __name__ == "__main__":
    main()
