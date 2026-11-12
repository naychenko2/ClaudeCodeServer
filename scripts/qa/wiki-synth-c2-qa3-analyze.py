"""Разбор результатов QA-3 (перепроверка четырёх дефектов QA-2, задача 597ed55a, эпик 66b5e075).

Только stdlib. Запускается после каждой ночи:
  python3 -I scripts/qa/wiki-synth-c2-qa3-analyze.py <атт-каталог>

Меряет по диску стенда:
1. 2d9df07a — хроника: строка «Что выяснили» несёт утверждение, цитаты — в сносках 8–300;
   ни одного применения, отвергнутого хроникой (apply_failed с причиной про хронику).
2. 03a50f53 — по одному окну не больше одного открытого пакета без человеческого решения;
   пакет с решением человека не погашен («stale»).
3. f68d3d82 — в причинах «Входящих» и отказов нет шума вида «число X», где X — кусок hex-id
   или номер после слова-метки; настоящие метрики (проценты, «N из M») по-прежнему сверяются.
4. Критерий ночи: чатов с результатом (страницы или открытый пакет) ≥ 4 из 6.
"""
import json, os, re, glob, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ATT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, ".cc-attachments", "qa-wiki-synth-c2-qa3")

local = json.load(open(os.path.join(ROOT, "backend", "ClaudeHomeServer", "appsettings.Local.json"), encoding="utf-8"))
DATA = os.path.dirname(os.path.abspath(local["DataPath"]))

SEED = json.load(open(os.path.join(ROOT, ".cc-attachments", "qa-wiki-synth-c2", "seed.json"), encoding="utf-8"))
OWNER = SEED["ownerId"]
WIKI = os.path.join(DATA, "wiki", OWNER)
KEEPER = os.path.join(WIKI, "keeper")
PAGES = os.path.join(WIKI, "pages")

out = {}

# ── итог ночи ───────────────────────────────────────────────────────────────
out["night"] = json.load(open(os.path.join(KEEPER, "nightly-last.json"), encoding="utf-8")) \
    if os.path.exists(os.path.join(KEEPER, "nightly-last.json")) else None

# ── стор отбора ─────────────────────────────────────────────────────────────
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

def window_refs(pkg):
    return [s["ref"] for s in pkg.get("sources") or [] if s.get("origin") == "session"]

def chat_of(pkg):
    m = re.match(r"session:([0-9a-f-]+)", (window_refs(pkg) or [""])[0])
    return m.group(1) if m else None

OPEN = ("checking", "proposed", "accepted", "partially_accepted", "applying")

# ── страницы и хроника ──────────────────────────────────────────────────────
def read_md(path):
    text = open(path, encoding="utf-8").read()
    m = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    return (text[m.end():] if m else text), text

MARKUP = re.compile(r"[*`~\\]")
LINE_MARK = re.compile(r"(?m)^[ \t]*(?:#{1,6}\s*|[-+]\s+|\d+\.\s+|>\s*)")

def canon(s: str) -> str:
    """Канонизация «как рендер» — как QuoteMatcher.NormalizeForMatch."""
    s = LINE_MARK.sub("", s or "")
    s = MARKUP.sub("", s)
    s = s.replace('"', "'")
    return re.sub(r"\s+", " ", s).strip()

def chat_msgs(session_id):
    p = os.path.join(DATA, "sessions", session_id, "history.json")
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else []

CHATS = {c["sessionId"]: c for c in SEED["chats"]}
CHAT_TEXT = {sid: "\n".join(m.get("text") or "" for m in chat_msgs(sid)) for sid in CHATS}

FOOTNOTE = re.compile(r"\[\^s\d+\]:\s*\[\[[^\]]+\]\]\s*—\s*«(.+?)»", re.S)
# Строка «Что выяснили»: строка этого раздела, заканчивающаяся сносками (формат хроники
# строчный, см. AppendChronicleAsync). Сноски (строки, начинающиеся с «[^s») — не строки.
STATEMENT = re.compile(r"(?m)^(?!\[\^s)(.+?)((?:\s*\[\^s\d+\])+)[ \t]*$")

chronicles = []
for path in glob.glob(os.path.join(PAGES, "**", "Хроники", "*.md"), recursive=True):
    body, _ = read_md(path)
    entry = {"path": os.path.relpath(path, PAGES), "footnotes": [], "statements": [],
             "footnote_out_of_contract": [], "statement_is_quote": []}
    for q in FOOTNOTE.findall(body):
        q = " ".join(q.split())
        entry["footnotes"].append(len(q))
        if len(q) < 8 or len(q) > 300:
            entry["footnote_out_of_contract"].append(len(q))
    sid = None
    m = re.search(r"\[\^s\d+\]:\s*\[\[qa-c2-([a-z0-9]+)-", body)
    if m:
        for c in SEED["chats"]:
            if c["key"] == m.group(1):
                sid = c["sessionId"]
                break
    src = canon(CHAT_TEXT.get(sid, "")) if sid else None
    sec = re.search(r"(?ms)^## Что выяснили\n(.*?)(?=^## |\Z)", body)
    for st, refs in (STATEMENT.findall(sec.group(1)) if sec else []):
        st = " ".join(st.split())
        entry["statements"].append(len(st))
        entry.setdefault("refs_per_statement", []).append(len(refs.split()) if refs else 0)
        # Строка «Что выяснили» — утверждение, а не цитата: дословная цитата источника
        # длиной больше меры (то, что сломало QA-2) в строке быть не должно.
        if src and len(st) > 300 and canon(st) in src:
            entry["statement_is_quote"].append(len(st))
    chronicles.append(entry)
out["chronicles"] = chronicles

# ── применения, отвергнутые хроникой (дефект 2d9df07a) ──────────────────────
chronic_rejects = [
    {"id": p["id"], "reason": p.get("statusReason")}
    for p in packages
    if p.get("status") == "apply_failed" and "роника" in (p.get("statusReason") or "")
]
out["chronic_rejects"] = chronic_rejects
out["apply_failed_any"] = [
    {"id": p["id"], "reason": (p.get("statusReason") or "")[:120]}
    for p in packages if p.get("status") == "apply_failed"
]

# ── дубли открытых пакетов одного окна (дефект 03a50f53) ────────────────────
by_window = {}
for p in packages:
    for ref in window_refs(p):
        by_window.setdefault(ref, []).append(
            {"id": p["id"], "status": p["status"],
             "human": any(o.get("humanDecision") for o in p.get("operations") or [])})
dupes = {}
for ref, ps in by_window.items():
    open_undecided = [x for x in ps if x["status"] in OPEN and not x["human"]]
    dupes[ref] = {"open_undecided": len(open_undecided),
                 "open_decided": [x["id"] for x in ps if x["status"] in OPEN and x["human"]],
                 "stale": [x["id"] for x in ps if x["status"] == "stale"],
                 "terminal": [x["status"] for x in ps if x["status"] not in OPEN and x["status"] != "stale"]}
out["windows"] = dupes

# ── шум в причинах (дефект f68d3d82) ────────────────────────────────────────
NUM_REASON = re.compile(r"числ(?:о|а|ел) не (?:подтверждено|найдено)[^:]*: ([0-9.,\s-]+)")
MARKER_WORD = re.compile(r"(?i)(этап|шаг|id|номер|пункт|№|#|m)\s*[-:]?\s*$")
HEX_TOKEN = re.compile(r"[0-9a-fA-F]*[0-9a-fA-F]{4,}")

def occ_is_noise(claim: str, num: str) -> bool:
    """X — шум, если в утверждении он стоит внутри слова-идентификатора (hex-id)
    или сразу после слова-метки («этап 2»), а не как самостоятельное число."""
    for m in re.finditer(re.escape(num), claim or ""):
        before = claim[:m.start()]
        after = claim[m.end():]
        glued_word = re.search(r"[0-9a-zA-Z_-]*$", before) and re.match(r"^[0-9a-zA-Z]", after)
        if re.search(r"[0-9a-zA-Z_-]{2,}$", before) and re.match(r"^[a-fA-F]", after):
            return True  # кусок внутри слова-идентификатора
        if glued_word and MARKER_WORD.search(before[-14:]):
            return True
    return False

reasons_noise, reasons_real = [], []
for p in packages:
    for op in p.get("operations") or []:
        r = op.get("reason") or ""
        m = NUM_REASON.search(r)
        if not m:
            continue
        nums = [n.strip() for n in m.group(1).split(",") if n.strip()]
        claim = op.get("claim") or op.get("text") or ""
        noisy = [n for n in nums if occ_is_noise(claim, n)]
        rec = {"pkg": p["id"][:8], "reason": r[:160], "nums": nums, "noise": noisy}
        (reasons_noise if noisy else reasons_real).append(rec)
for e in triage.values():
    r = e.get("reason") or ""
    if NUM_REASON.search(r):
        reasons_noise.append({"pkg": "triage", "reason": r[:160]})
out["reasons_noise"] = reasons_noise
out["reasons_real_metrics"] = [
    r for r in reasons_real if re.search(r"\d+(\s*%|%)|\d+\s+из\s+\d+", r["reason"])]
out["reasons_real_other"] = [r for r in reasons_real if r not in out["reasons_real_metrics"]]

# ── чаты с результатом (критерий ≥4) ───────────────────────────────────────
def page_of_chat(key):
    res = []
    for path in glob.glob(os.path.join(PAGES, "**", "*.md"), recursive=True):
        body, _ = read_md(path)
        if not re.search(r"^type:\s*(decision|rule|term|state)\s*$", body, re.M):
            head = open(path, encoding="utf-8").read()[:400]
            if not re.search(r"^type:\s*(decision|rule|term|state)", head, re.M):
                continue
        if re.search(r"\[\[\s*qa-c2-%s-" % re.escape(key), body):
            res.append(os.path.relpath(path, PAGES))
    return res

table = []
chats_with = []
for c in SEED["chats"]:
    sid = c["sessionId"]
    pk = [p for p in packages if chat_of(p) == sid]
    open_pk = [p["id"] for p in pk if p.get("status") in OPEN]
    applied_pk = [p["id"] for p in pk if p.get("status") == "applied"]
    pages = page_of_chat(c["key"])
    if pages or open_pk or applied_pk:
        chats_with.append(c["key"])
    table.append({"key": c["key"], "packages": [(p["id"][:8], p["status"]) for p in pk],
                  "pages": len(pages), "open": open_pk,
                  "triage": {"zone": triage.get(sid, {}).get("zone"),
                              "cursor": triage.get(sid, {}).get("cursor")}})
out["table"] = table
out["criterion_chats_with_result"] = {"count": len(chats_with), "keys": chats_with, "need": 4}

print(json.dumps(out, ensure_ascii=False, indent=1))
os.makedirs(ATT, exist_ok=True)
json.dump(out, open(os.path.join(ATT, "analysis.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
