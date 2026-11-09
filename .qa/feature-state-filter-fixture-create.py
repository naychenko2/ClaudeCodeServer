#!/usr/bin/env python3
"""Фаза 1 фикстуры приёмки «Фильтр фич по состоянию» (живой бэк, REST).

Создаёт 4 проекта:
  P1 — девять фич, по одной на каждое значение оси featureKinds;
  P2 — открытая + закрытая фича (сценарий «Под фильтр ничего не попало»);
  P3 — только закрытые фичи (сценарий «Открытых фич нет»);
  P4 — без фич (сценарий «Фич пока нет»).

Результат — /home/an/qa-filter/fixture.json; состояния добивает patch_fixture.py
(фаза 2, при остановленном бэке).
"""
import json
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5580"
OUT = "/home/an/qa-filter/fixture.json"
WORKTREE = "/home/an/Sources/ClaudeCodeServer-feature-state-filter"


def api(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read().decode()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        print(f"!! {method} {path} -> {e.code}: {raw[:300]}", flush=True)
        return e.code, raw


st, res = api("POST", "/api/auth/login", {"username": "admin", "password": "12345"})
if st != 200:
    raise SystemExit(f"вход не удался: {st} {res}")
TOKEN = res["token"]
st, res = api("PUT", "/api/feature-flags/features-overview", {"enabled": True}, TOKEN)
print(f"флаг features-overview -> {st}", flush=True)


def make_project(name, root):
    st, res = api("GET", "/api/projects", None, TOKEN)
    existing = res if isinstance(res, list) else res.get("projects", [])
    for p in existing:
        if p.get("name") == name:
            print(f"проект «{name}» уже есть: {p['id']}", flush=True)
            return p["id"]
    st, p = api("POST", "/api/projects",
                {"name": name, "rootPath": root, "createDirectory": True}, TOKEN)
    if st not in (200, 201):
        raise SystemExit(f"проект не создан: {st} {p}")
    return p["id"]


def make_chat(name):
    st, c = api("POST", "/api/chats", {"name": name, "model": "claude-sonnet-5-5"}, TOKEN)
    if st not in (200, 201):
        raise SystemExit(f"чат не создан: {st} {c}")
    return c["id"]


def make_task(pid, body):
    st, t = api("POST", f"/api/projects/{pid}/tasks", body, TOKEN)
    if st not in (200, 201):
        raise SystemExit(f"задача «{body.get('title')}» не создана: {st} {t}")
    return t["id"]


def close_task(tid, verdict=None):
    body = {"status": "done"}
    if verdict:
        body["verification"] = {"verdict": verdict, "notes": "Фикстура QA (фильтр фич)"}
    st, res = api("PUT", f"/api/tasks/{tid}", body, TOKEN)
    if st not in (200, 204):
        print(f"!! закрытие {tid}: {st} {res}", flush=True)


fixture = {"projects": {}, "features": []}

P1 = make_project("QA — Фильтр фич: все состояния", "/home/an/qa-filter/proj1")
P2 = make_project("QA — Фильтр фич: пусто под фильтром", "/home/an/qa-filter/proj2")
P3 = make_project("QA — Фильтр фич: только закрытые", "/home/an/qa-filter/proj3")
P4 = make_project("QA — Фильтр фич: без фич", "/home/an/qa-filter/proj4")
fixture["projects"] = {"all": P1, "mixed": P2, "closed": P3, "empty": P4}
print(f"проекты: {fixture['projects']}", flush=True)


def feature(pid, key, title, kind, extra_children=None, epic_done=False):
    """Эпик + чат + дочерняя задача; kind — ожидаемое значение оси (для сверки)."""
    st, epic = api("POST", f"/api/projects/{pid}/tasks",
                   {"title": title, "description": "Фикстура приёмки «Фильтр фич по состоянию»",
                    "role": "feature", "status": "done" if epic_done else "inProgress"}, TOKEN)
    if st not in (200, 201):
        raise SystemExit(f"эпик «{title}» не создан: {st} {epic}")
    eid = epic["id"]
    chat = make_chat(f"{title} — чат")
    step = make_task(pid, {
        "title": "Шаг фикстуры", "description": "Шаг фикстуры", "role": "step",
        "featureId": eid, "linkedSessionId": chat, "status": "inProgress",
        "stepRef": "S1", "lane": "front", "worktreePath": WORKTREE,
    })
    rec = {"key": key, "kind": kind, "projectId": pid, "epicId": eid,
           "chatId": chat, "stepId": step, "title": title, "children": []}
    for child in (extra_children or []):
        child_chat = make_chat(f"{title} — {child['title']}")
        cid = make_task(pid, {
            "title": child["title"], "description": "Шаг фикстуры", "role": child["role"],
            "featureId": eid, "linkedSessionId": child_chat, "status": "inProgress",
            "stepRef": child.get("stepRef", "S2"), "lane": "front",
            "worktreePath": WORKTREE, "modelTier": "strong",
        })
        rec["children"].append({"role": child["role"], "taskId": cid, "chatId": child_chat,
                                "title": child["title"], "verdict": child.get("verdict"),
                                "close": child.get("close", False)})
        # Порядок важен: роль merge создаётся только после принятого финала,
        # поэтому закрываем ребёнка сразу (не откладывая на конец).
        if child.get("verdict"):
            close_task(cid, child["verdict"])
        elif child.get("close"):
            close_task(cid)
    fixture["features"].append(rec)
    print(f"фича {key} «{title}» ({kind}) эпик={eid}", flush=True)


# ── P1: девять состояний ────────────────────────────────────────────────────
feature(P1, "W", "Фича: ждут вас", "waiting_you")
feature(P1, "E", "Фича: остановлена", "error")
feature(P1, "R", "Фича: идёт", "running")
feature(P1, "Q", "Фича: в очереди", "queued")
feature(P1, "P", "Фича: на паузе", "paused")
feature(P1, "D", "Фича: черновик", "draft")
feature(P1, "I", "Фича: не начата", "idle")
feature(P1, "M", "Фича: смержена (эпик открыт)", "merged", extra_children=[
    {"role": "review", "title": "Ревью", "verdict": "accepted", "close": True},
    {"role": "final", "title": "Финал", "verdict": "accepted", "close": True},
    {"role": "merge", "title": "Мерж в мастер", "close": True},
])
feature(P1, "C", "Фича: закрыта", "closed", epic_done=True)

# ── P2: открытая + закрытая (сценарий «ничего не попало») ───────────────────
feature(P2, "R2", "Фича: идёт (проект 2)", "running")
feature(P2, "C2", "Фича: закрыта (проект 2)", "closed", epic_done=True)

# ── P3: только закрытые (сценарий «Открытых фич нет») ───────────────────────
feature(P3, "C3", "Фича: закрыта (проект 3)", "closed", epic_done=True)
feature(P3, "C4", "Фича: закрыта тоже (проект 3)", "closed", epic_done=True)

json.dump(fixture, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"фикстура записана: {OUT}", flush=True)
