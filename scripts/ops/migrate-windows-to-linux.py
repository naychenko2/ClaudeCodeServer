#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Перенос данных прода с Windows-хоста на Linux: пути, транскрипты, git.

Запускается над КОПИЕЙ данных (сервер остановлен). Идемпотентен: повторный прогон
ничего не меняет. По умолчанию — сухой прогон (только отчёт); --apply пишет.

  migrate-windows-to-linux.py --data /srv/ccs/data --transcripts ~/.claude/projects \
      --repos /srv/ccs/home /srv/ccs/sandbox ~/Sources [--apply]

Что делает:
 1. JSON-сторы в data: строка, которая ЦЕЛИКОМ является путём от известного корня
    (C:/ClaudeHome/…, C:\\ClaudeSandbox\\…), переписывается на Linux-путь со слешами «/».
    Пути внутри текста (описания задач, память) не трогаются. Правка — на уровне
    исходного текста файла, форматирование и \\uXXXX-экранирование сохраняются.
 2. Папки транскриптов claude CLI ({профиль}/projects/{уплощённый cwd}) переименовываются
    по тем же корням (уплощение: всё, кроме [A-Za-z0-9], → «-»; см. TranscriptMigrator).
 3. Git-репозитории: указатели worktree (gitdir:) на новые пути; core.autocrlf=true
    (рабочие деревья пришли из Windows с CRLF); биты +x по индексу (SMB их теряет).
 4. Проверка: RootPath проектов существует на диске (с поправкой регистра).
"""
import argparse, json, os, re, subprocess, sys
from pathlib import Path

HOME = str(Path.home())
# Windows-корень → Linux-корень. Порядок важен: длинные префиксы раньше.
ROOTS = [
    ("C:/ClaudeData/prod", "/srv/ccs/data"),
    ("C:/ClaudeServer/prod", "/opt/ccs/app"),
    ("C:/ClaudeHome", "/srv/ccs/home"),
    ("C:/ClaudeSandbox", "/srv/ccs/sandbox"),
    ("C:/Sources", f"{HOME}/Sources"),
]

def flatten(p):
    return "".join(c if (c.isascii() and c.isalnum()) else "-" for c in p)

# ---------- 1. JSON ----------
# ЛЮБОЙ строковый литерал JSON. Перебор всех литералов подряд от начала файла держит
# границы выровненными: иначе экранированная кавычка внутри текста (\"C:/…\")
# принималась за начало литерала и «путём» считался хвост чужой строки
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')
DRIVE = re.compile(r'^[A-Za-z]:(?:/|\\\\)')

def map_win_path(p):
    """Windows-путь (уже без JSON-экранирования обратных слешей) → Linux или None."""
    norm = p.replace("\\", "/")
    for win, lin in ROOTS:
        if norm.lower() == win.lower() or norm.lower().startswith(win.lower() + "/"):
            return lin + norm[len(win):]
    return None

def rewrite_json_text(text, unknown):
    changed = 0
    def sub(m):
        nonlocal changed
        raw = m.group(1)
        if not DRIVE.match(raw):
            return m.group(0)
        try:
            plain = json.loads('"' + raw + '"')
        except ValueError:
            return m.group(0)
        new = map_win_path(plain)
        if new is None:
            # корень для отчёта: «C:/Users», «D:/llm» …
            parts = re.split(r"[\\/]", plain)
            key = "/".join(parts[:2])
            unknown[key] = unknown.get(key, 0) + 1
            return m.group(0)
        changed += 1
        # стиль исходника: .NET по умолчанию экранирует не-ASCII как \uXXXX
        return json.dumps(new, ensure_ascii="\\u" in raw)
    return LIT.sub(sub, text), changed

def iter_json_files(data):
    skip = {"claude-profiles", "sandbox-profiles", "archived-transcripts", "backups", "forgejo",
            "logs", "prompt-snapshots"}
    for root, dirs, files in os.walk(data):
        rel = os.path.relpath(root, data)
        if rel.split(os.sep)[0] in skip:
            dirs[:] = []
            continue
        for f in files:
            if f.endswith(".json") and ".bak" not in f and ".corrupt" not in f:
                yield os.path.join(root, f)

def migrate_json(data, apply):
    total, files, unknown = 0, 0, {}
    for path in iter_json_files(data):
        with open(path, "rb") as fh:
            raw = fh.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8", errors="surrogateescape")
        new, n = rewrite_json_text(text, unknown)
        if n:
            files += 1; total += n
            print(f"  json {n:5d}  {os.path.relpath(path, data)}")
            if apply:
                json.loads(new)  # не записываем битый JSON
                tmp = path + ".migrate.tmp"
                with open(tmp, "wb") as fh:
                    fh.write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8", errors="surrogateescape"))
                os.replace(tmp, path)
    print(f"JSON: {total} путей в {files} файлах")
    if unknown:
        print("  не сопоставлены (оставлены как есть):")
        for k, v in sorted(unknown.items(), key=lambda kv: -kv[1])[:30]:
            print(f"    {v:5d}  {k}")

# ---------- 2. Транскрипты ----------
FLAT = [(flatten(w), flatten(l)) for w, l in ROOTS]

def migrate_transcript_dir(projects_dir, apply):
    if not os.path.isdir(projects_dir):
        return 0
    moved = 0
    for name in sorted(os.listdir(projects_dir)):
        src = os.path.join(projects_dir, name)
        if not os.path.isdir(src):
            continue
        for fw, fl in FLAT:
            if name.lower() == fw.lower() or name.lower().startswith(fw.lower() + "-"):
                dst = os.path.join(projects_dir, fl + name[len(fw):])
                moved += 1
                if apply:
                    if os.path.exists(dst):  # слияние: файлы с одинаковыми именами не перетираем
                        for f in os.listdir(src):
                            s, d = os.path.join(src, f), os.path.join(dst, f)
                            if not os.path.exists(d):
                                os.replace(s, d)
                        try: os.rmdir(src)
                        except OSError: print(f"  ! не пустая после слияния: {src}")
                    else:
                        os.replace(src, dst)
                break
    return moved

def migrate_transcripts(data, extra, apply):
    dirs = [os.path.join(data, "claude-profiles", p, "projects")
            for p in sorted(os.listdir(os.path.join(data, "claude-profiles")))] \
           + [os.path.join(data, "claude-profiles", "projects")] + extra
    arch = os.path.join(data, "archived-transcripts")
    if os.path.isdir(arch):
        for r, ds, _ in os.walk(arch):
            if os.path.basename(r) == "projects":
                dirs.append(r); ds[:] = []
    total = 0
    for d in dict.fromkeys(dirs):
        n = migrate_transcript_dir(d, apply)
        if n: print(f"  transcripts {n:5d}  {d}")
        total += n
    print(f"Транскрипты: {total} папок")

# ---------- 3. Git ----------
def git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)

def fix_gitdir_file(path, apply):
    with open(path, encoding="utf-8", errors="replace") as fh:
        s = fh.read()
    m = re.match(r"(gitdir:\s*)?(.+?)\s*$", s, re.S)
    if not m: return False
    new = map_win_path(m.group(2).strip())
    if not new: return False
    if apply:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write((m.group(1) or "") + new + "\n")
    return True

def migrate_repos(roots, apply):
    repos, pointers = [], 0
    for top in roots:
        for root, dirs, files in os.walk(top):
            dirs[:] = [d for d in dirs if d not in ("node_modules", "bin", "obj")]
            if ".git" in dirs:
                repos.append(root)
                wt = os.path.join(root, ".git", "worktrees")
                if os.path.isdir(wt):
                    for w in os.listdir(wt):
                        g = os.path.join(wt, w, "gitdir")
                        if os.path.isfile(g) and fix_gitdir_file(g, apply): pointers += 1
                dirs.remove(".git")
            if ".git" in files:  # worktree / submodule
                if fix_gitdir_file(os.path.join(root, ".git"), apply): pointers += 1
                repos.append(root)
    print(f"Git: репозиториев {len(repos)}, указателей worktree переписано {pointers}")
    if not apply:
        return
    for repo in repos:
        git(repo, "config", "core.autocrlf", "true")
        ls = git(repo, "ls-files", "-s")
        for line in ls.stdout.splitlines():
            mode, _, _, name = line.split(None, 3)
            if mode == "100755":
                p = os.path.join(repo, name)
                if os.path.isfile(p): os.chmod(p, os.stat(p).st_mode | 0o111)
        git(repo, "worktree", "prune")

# ---------- 4. Проверка ----------
def resolve_case(path):
    """Найти реальный путь с учётом регистра по сегментам; None если нет."""
    if os.path.exists(path): return path
    cur = "/"
    for part in Path(path).parts[1:]:
        try: names = os.listdir(cur)
        except OSError: return None
        hit = next((n for n in names if n == part), None) or next((n for n in names if n.lower() == part.lower()), None)
        if hit is None: return None
        cur = os.path.join(cur, hit)
    return cur

def check_projects(data):
    with open(os.path.join(data, "projects.json"), encoding="utf-8-sig") as fh:
        projects = json.load(fh)
    bad = 0
    for p in projects:
        # в сухом прогоне в файле ещё Windows-пути — проверяем то, во что они превратятся
        rp = map_win_path(p.get("RootPath", "")) or p.get("RootPath", "")
        real = resolve_case(rp)
        if real != rp:
            bad += 1
            print(f"  ! {p.get('Name', '?')}: {rp} -> {'НЕТ НА ДИСКЕ' if real is None else 'регистр: ' + real}")
    print(f"Проекты: {len(projects)}, проблемных {bad}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--transcripts", nargs="*", default=[], help="доп. каталоги projects (~/.claude/projects)")
    ap.add_argument("--repos", nargs="*", default=[])
    ap.add_argument("--apply", action="store_true")
    a = ap.parse_args()
    print("РЕЖИМ:", "ПРИМЕНЕНИЕ" if a.apply else "сухой прогон")
    migrate_json(a.data, a.apply)
    migrate_transcripts(a.data, a.transcripts, a.apply)
    migrate_repos(a.repos, a.apply)
    check_projects(a.data)

if __name__ == "__main__":
    main()
