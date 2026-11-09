#!/usr/bin/env python3
"""QA3 — снимок сводки по фичам: state/kind/note(stale, stageAtWrite) + строка «Сейчас».

Запуск:  python3 .qa/qa3-brief.py [ключ_фичи ...]
"""

import json
import os
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


def main():
    login()
    ids = json.load(open(os.path.join(HERE, "qa3-fixture-ids.json")))
    feats = ids["features"]
    wanted = sys.argv[1:] or list(feats.keys())
    for key in wanted:
        if key not in feats:
            continue
        fid = feats[key]
        st, page = call("GET", f"/api/tasks/features/{fid}")
        if st != 200:
            print(f"{key}: GET {st} {page}")
            continue
        brief = page.get("brief") or {}
        note = brief.get("note")
        now = brief.get("now")
        run = page.get("run") or {}
        stages = [(s.get("key"), s.get("state")) for s in (run.get("stages") or [])]
        active = [k for k, s in stages if s == "active"]
        print(f"--- {key} ({fid})")
        print(f"    state={run.get('state')} kind={brief.get('kind')} "
              f"now={bool(now)} activeStage={active}")
        print(f"    now={json.dumps(now, ensure_ascii=False)}")
        print(f"    note={json.dumps(note, ensure_ascii=False)}")
        print(f"    reason={brief.get('reason')!r} queueWaitMinutes={brief.get('queueWaitMinutes')}")


if __name__ == "__main__":
    import sys
    main()
