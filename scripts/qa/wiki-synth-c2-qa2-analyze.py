"""Разбор результатов ночи QA-2 (повтор после доработки): таблица по шести чатам,
критерии 1–2, проверка исправленных дефектов и новых правил.

Только stdlib. Запускается после первой ночи:
  python3 -I scripts/qa/wiki-synth-c2-qa2-analyze.py <путь-к-атт-каталогу> [night-файл]

Каталог данных стенда берётся из appsettings.Local.json (DataPath), как в спеке посева.
"""
import json, os, re, glob, sys, datetime

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ATT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, ".cc-attachments", "qa-wiki-synth-c2-qa2")
SEED_DIR = os.path.join(ROOT, ".cc-attachments", "qa-wiki-synth-c2")

local = json.load(open(os.path.join(ROOT, "backend", "ClaudeHomeServer", "appsettings.Local.json"), encoding="utf-8"))
DATA = os.path.dirname(os.path.abspath(local["DataPath"]))

SEED = json.load(open(os.path.join(SEED_DIR, "seed.json"), encoding="utf-8"))
OWNER = SEED["ownerId"]
WIKI = os.path.join(DATA, "wiki", OWNER)
KEEPER = os.path.join(WIKI, "keeper")
PAGES = os.path.join(WIKI, "pages")

out = {}

# ── итог ночи ───────────────────────────────────────────────────────────────
night_path = os.path.join(KEEPER, "nightly-last.json")
out["night"] = json.load(open(night_path, encoding="utf-8")) if os.path.exists(night_path) else None

# ── стор отбора (исходы разбора по чатам) ───────────────────────────────────
triage = {}
tp = os.path.join(KEEPER, "chat-triage.json")
if os.path.exists(tp):
    for e in json.load(open(tp, encoding="utf-8")):
        triage[e["sessionId"]] = e

# ── пакеты ──────────────────────────────────────────────────────────────────
packages = []
for p in sorted(glob.glob(os.path.join(KEEPER, "packages", "*.json"))):
    try:
        packages.append(json.load(open(p, encoding="utf-8")))
    except Exception as exc:  # noqa: BLE001
        print("пакет не прочитан:", p, exc)

def chat_of(pkg):
    for s in pkg.get("sources") or []:
        m = re.match(r"session:([0-9a-f-]+)", s.get("ref") or "")
        if m:
            return m.group(1)
    return None

# ── страницы ────────────────────────────────────────────────────────────────
def read_pages():
    res = []
    for path in glob.glob(os.path.join(PAGES, "**", "*.md"), recursive=True):
        text = open(path, encoding="utf-8").read()
        fm = {}
        m = re.match(r"^---\n(.*?)\n---\n", text, re.S)
        body = text
        if m:
            body = text[m.end():]
            for line in m.group(1).split("\n"):
                if ":" in line:
                    k, v = line.split(":", 1)
                    fm[k.strip()] = v.strip().strip('"')
        res.append({"path": os.path.relpath(path, PAGES), "fm": fm, "body": body})
    return res

pages = read_pages()

# ── история чатов ───────────────────────────────────────────────────────────
def chat_msgs(session_id):
    p = os.path.join(DATA, "sessions", session_id, "history.json")
    if not os.path.exists(p):
        return []
    try:
        return json.load(open(p, encoding="utf-8"))
    except Exception:
        return []

CHATS = {c["sessionId"]: c for c in SEED["chats"]}
CHAT_TEXT = {sid: "\n".join(m.get("text") or "" for m in chat_msgs(sid)) for sid in CHATS}

MARKUP = re.compile(r"[*`~\\]")
LINE_MARK = re.compile(r"(?m)^[ \t]*(?:#{1,6}\s*|[-+]\s+|\d+\.\s+|>\s*)")

def canon(s: str) -> str:
    """Канонизация «как рендер» — как QuoteMatcher.NormalizeForMatch: маркеры эмфазиса/кода,
    построчные структурные маркеры (#, -, +, 1., >) и схлопывание whitespace."""
    s = LINE_MARK.sub("", s or "")
    s = MARKUP.sub("", s)
    s = s.replace('"', "'")
    return re.sub(r"\s+", " ", s).strip()

NUM = re.compile(r"\d+(?:[.,]\d+)?")

def numbers(s):
    return {n.replace(",", ".") for n in NUM.findall(s or "")}

PROCESS_MARKER = "[процесс] служебная реплика"

# ── сбор по чатам ───────────────────────────────────────────────────────────
rows = {}
for c in SEED["chats"]:
    rows[c["sessionId"]] = {
        "unit": c["unit"], "key": c["key"], "title": c["title"],
        "messages": len(chat_msgs(c["sessionId"])),
        "packages": [], "claims": 0, "quotes": 0, "inbox": 0, "auto": 0,
        "pages": [], "reasons": [], "kinds": {}, "asof": [],
        "bad_quote": [], "process_quote": [],
    }

for pkg in packages:
    sid = chat_of(pkg)
    if sid is None or sid not in rows:
        continue
    r = rows[sid]
    r["packages"].append(pkg.get("id"))
    txt = CHAT_TEXT.get(sid, "")
    for op in pkg.get("operations") or []:
        r["claims"] += 1
        kind = op.get("kind")
        if kind:
            r["kinds"][kind] = r["kinds"].get(kind, 0) + 1
        if op.get("decision") == "inbox":
            r["inbox"] += 1
            if op.get("reason"):
                r["reasons"].append(op["reason"])
        else:
            r["auto"] += 1
        if op.get("op") == "create":
            r["pages"].append(op.get("page"))
        for q in op.get("quotes") or []:
            qq = q.strip()
            r["quotes"] += 1
            if qq and canon(qq) not in canon(txt):
                r["bad_quote"].append(qq[:80])
            if PROCESS_MARKER in qq:
                r["process_quote"].append(qq[:80])

# ── страницы: вид, as_of против даты последней реплики окна, протечка процесса ──
pages_report = []
for p in pages:
    fm = p["fm"]
    entry = {"path": p["path"], "type": fm.get("type"), "as_of": fm.get("as_of"),
            "process_leak": PROCESS_MARKER in p["body"]}
    # as_of state-страницы: дата последней реплики разобранного окна (cursor) seeded-чата.
    if fm.get("type") == "state" and fm.get("as_of"):
        # источник страницы — сноска [^sN]: [[qa-c2-<key>-...]]; найдём чат по заголовку источника.
        m = re.search(r"\[\[\s*(qa-c2-[a-z0-9]+)-", p["body"])
        sid = None
        if m:
            for c in SEED["chats"]:
                if m.group(1) == f"qa-c2-{c['key']}":
                    sid = c["sessionId"]
                    break
        if sid:
            cur = triage.get(sid, {}).get("cursor") or 0
            msgs = chat_msgs(sid)
            if cur and cur <= len(msgs):
                ts = msgs[cur - 1].get("timestamp")
                if ts:
                    expect = datetime.datetime.fromtimestamp(ts / 1000, datetime.timezone.utc).strftime("%Y-%m-%d")
                    entry["as_of_expected"] = expect
                    entry["as_of_ok"] = fm["as_of"] == expect
    pages_report.append(entry)

# ── критерии ────────────────────────────────────────────────────────────────
# Страница чата = фактический файл знания, чья сноска ссылается на источник чата;
# пакет во «Входящих» = файл пакета со status proposed.
def page_of_chat(key):
    out = []
    for p in pages:
        if p["fm"].get("type") not in ("decision", "rule", "term", "state"):
            continue
        if re.search(r"\[\[\s*qa-c2-%s-" % re.escape(key), p["body"]):
            out.append(p["path"])
    return out

def inbox_of_chat(key):
    sid = next(c["sessionId"] for c in SEED["chats"] if c["key"] == key)
    return [p["id"] for p in packages if chat_of(p) == sid and p.get("status") == "proposed"]

chats_with = []
for c in SEED["chats"]:
    r = rows[c["sessionId"]]
    r["pages_exist"] = page_of_chat(c["key"])
    r["inbox_exist"] = inbox_of_chat(c["key"])
    if r["pages_exist"] or r["inbox_exist"]:
        chats_with.append(c["key"])
accepted_ops = sum(r["claims"] for r in rows.values())
dropped = (out["night"] or {}).get("dropped", 0)
total_ops = accepted_ops + dropped

out["table"] = list(rows.values())
out["pages"] = pages_report
out["criterion1"] = {"chats_with_result": len(chats_with), "keys": chats_with, "need": 4}
out["criterion2"] = {
    "accepted_ops": accepted_ops, "dropped_ops": dropped, "total_ops": total_ops,
    "accepted_share": round(accepted_ops / total_ops, 3) if total_ops else None,
}
print(json.dumps(out, ensure_ascii=False, indent=1))
json.dump(out, open(os.path.join(ATT, "analysis.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
