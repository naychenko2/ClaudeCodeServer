#!/usr/bin/env python3
"""Анализ: насколько прогресс и срок фичи по карточкам совпадают с реальностью.

Читает кэш, который собирает features_progress_fetch.py (REST трекера), и по каждой
ЗАКРЫТОЙ фиче (epic.status = done) восстанавливает хронологию:
  * знаменатель «из скольких шагов» в каждый момент t (шаги-карточки и ключи действующих планов);
  * числитель «принято» в каждый момент t (последний виток по цепочке шага принят);
  * процент трекера P(t) = принято / знаменатель — так его показала бы страница в момент t.

«Реальная доля готового» R(t) считается задним числом: сколько из ФИНАЛЬНО принятых шагов
принято к моменту t (шаги, снятые или не принятые к закрытию, в истину не входят).

Ограничения восстановления (честно, см. отчёт):
  * состав планов берётся финальный (правок planSteps во времени трекер не хранит);
    момент появления шагов плана = createdAt плана-задачи;
  * снятый (withdrawn) план/шаг исключается целиком, момент снятия в данных не используется;
  * шаги без привязки доработок (unlinkedReworks) в цепочку не входят — как и в продукте.
Сверка с продуктом: итоговые accepted/total восстановления сравниваются с run.stepProgress.

Запуск:
  python3 features_progress_analyze.py --cache /tmp/fpa-cache [--json out.json] [--table out.md]
"""
import argparse
import glob
import json
import os
import statistics
import sys
from datetime import datetime, timezone

ACCEPTED = {"accepted", "accepted_with_remarks"}
MIN_STEPS = 3          # меньше шагов — не «фича» с процентом, а эпик-контейнер или мелочь
EPS_GRID = [-0.5, -0.25, 0.0, 0.25, 0.5]  # смещение оценки итогового числа шагов моделью


def ts(s):
    if not s:
        return None
    s = s.rstrip("Z")
    if "." in s:
        head, frac = s.split(".")
        s = head + "." + frac[:6]
    return datetime.fromisoformat(s).replace(tzinfo=timezone.utc)


def minutes(a, b):
    return (b - a).total_seconds() / 60.0


def key_of(x):
    return (x or "").strip().casefold()


def load(cache):
    out = []
    for path in sorted(glob.glob(os.path.join(cache, "features", "*.json"))):
        d = json.load(open(path, encoding="utf-8"))
        plans = []
        for p in d["run"].get("plan", []):
            pp = os.path.join(cache, "plans", p["id"] + ".json")
            plans.append(json.load(open(pp, encoding="utf-8")) if os.path.exists(pp) else p)
        out.append((d, plans))
    return out


def active_plans(plans, t):
    """ActivePlans (FeatureStepAccounting) на момент t: не снятые и не вытесненные более поздними
    планами с пересекающимися ключами; учитываются только планы, созданные не позже t."""
    live = []
    for p in plans:
        if ts(p["createdAt"]) > t:
            continue
        if (p.get("verification") or {}).get("verdict") == "withdrawn":
            continue
        keys = {key_of(s["stepRef"]) for s in (p.get("planSteps") or [])
                if s.get("isStep", True) and (s.get("kind") in (None, "step")) and key_of(s.get("stepRef"))}
        live.append((p["createdAt"], p["id"], p, keys))
    live.sort(key=lambda x: (x[0], x[1]), reverse=True)
    res = []
    for i, (_, _, p, keys) in enumerate(live):
        if any(keys & live[j][3] for j in range(i)):
            continue
        res.append((p, keys))
    return res


def plan_state(act):
    if not act:
        return "none"
    with_steps = sum(1 for p, keys in act if keys)
    if with_steps == 0:
        return "pending"
    return "partial" if with_steps < len(act) else "cut"


def analyze_feature(d, plans):
    run = d["run"]
    epic = run["epic"]
    task_by_id = {t["id"]: t for t in run["steps"]}
    t_close = ts(epic["completedAt"])

    chains = []
    for c in run["stepChains"]:
        if c["outcome"] == "withdrawn":
            continue
        tasks = [task_by_id[i] for i in c["taskIds"] if i in task_by_id]
        creates = [ts(t["createdAt"]) for t in tasks if t["role"] == "step"] or [ts(t["createdAt"]) for t in tasks]
        if not creates:
            continue
        rounds = [(ts(r["completedAt"]), r["verdict"] in ACCEPTED) for r in c["rounds"]
                  if r["completedAt"] and r["verdict"] in ACCEPTED | {"returned"}]
        rounds.sort()
        # доработки цепочки: интервал «открыта» [создана, закрыта); незакрытая — до конца времён
        reworks = [(ts(t["createdAt"]), ts(t["completedAt"]) if t["status"] == "done" and t.get("completedAt") else None)
                   for t in tasks if t["role"] == "rework"]
        chains.append({
            "key": key_of(c["stepRef"]) or c["taskIds"][0],
            "created": min(creates),
            "rounds": rounds,
            "reworks": reworks,
            # момент первой сдачи шага (step-задача Done): «работа сделана», ревью ещё может не быть
            "submitted": min((ts(t["completedAt"]) for t in tasks
                              if t["role"] == "step" and t["status"] == "done" and t.get("completedAt")), default=None),
            "outcome": c["outcome"],
            "acceptedAt": ts(c["acceptedAt"]) if c["outcome"] == "accepted" and c.get("acceptedAt") else None,
        })
    n_chains = len(chains)
    final_accepted = [c for c in chains if c["acceptedAt"]]
    if n_chains < MIN_STEPS:
        return {"skip": "шагов < %d (%d)" % (MIN_STEPS, n_chains), "title": epic["title"], "id": epic["id"]}

    withdrawn_keys = {key_of(c["stepRef"]) for c in run["stepChains"] if c["outcome"] == "withdrawn"}
    chain_keys = {c["key"] for c in chains}

    def state_at(t):
        den_keys = {c["key"] for c in chains if c["created"] <= t}
        acc = 0
        for c in chains:
            if c["created"] > t:
                continue
            # «принят» = последний виток принят И нет незакрытой доработки (как FeatureStepAccounting)
            last = [r for r in c["rounds"] if r[0] <= t]
            open_rework = any(cr <= t and (cl is None or cl > t) for cr, cl in c["reworks"])
            if last and last[-1][1] and not open_rework:
                acc += 1
        act = active_plans(plans, t)
        for _, keys in act:
            den_keys |= {k for k in keys if k not in withdrawn_keys}
        return len(den_keys), acc, plan_state(act), sum(1 for _, keys in act for k in keys if k not in withdrawn_keys and k not in chain_keys)

    # Все моменты, где что-то меняется
    pts = {c["created"] for c in chains}
    for c in chains:
        pts |= {r[0] for r in c["rounds"]}
        if c["submitted"]:
            pts.add(c["submitted"])
        for cr, cl in c["reworks"]:
            pts.add(cr)
            if cl:
                pts.add(cl)
    for p in plans:
        pts.add(ts(p["createdAt"]))
    first_t = min(pts)
    t_end = max([t_close] + [c["acceptedAt"] for c in final_accepted]) if t_close else max(pts)
    pts = sorted(p for p in pts if p <= t_end)

    series = []
    for t in pts:
        den, acc, pstate, unplanned_keys = state_at(t)
        done_final = sum(1 for c in final_accepted if c["acceptedAt"] <= t)
        series.append({"t": t, "den": den, "acc": acc, "plan": pstate, "done_final": done_final,
                       "sub": sum(1 for c in chains if c["submitted"] and c["submitted"] <= t)})
    series = [s for s in series if s["den"] > 0]
    if not series:
        return {"skip": "нет знаменателя", "title": epic["title"], "id": epic["id"]}

    a_end = len(final_accepted)
    a_sub = sum(1 for c in chains if c["submitted"])
    n_end = state_at(t_end)[0]
    last_state = state_at(t_end)
    t0 = series[0]["t"]
    span = max(minutes(t0, t_end), 1.0)

    # --- метрики ошибки процента (по времени и по событиям) ---
    def pct(s, E=None):
        d_ = s["den"] if E is None else max(s["den"], E)
        return s["acc"] / d_

    def real(s):
        return s["done_final"] / a_end if a_end else None

    def real_time(s, nxt):
        """Вторая «истина»: доля прошедшего времени жизни фичи (середина интервала)."""
        mid = s["t"] + (nxt - s["t"]) / 2
        return min(max(minutes(t0, mid) / span, 0.0), 1.0)

    def real_sub(s):
        return s["sub"] / a_sub if a_sub else None

    def err_stats(fn_pred, mask=None, truth="acc"):
        """Среднее |P-R| и смещение P-R: по времени (интервалы) и по событиям.
        truth: acc — доля финально принятых шагов, sub — доля сданных (Done) шагов, time — доля времени жизни."""
        tw = te = bw = be = w_total = 0.0
        n = 0
        for i, s in enumerate(series):
            if a_end == 0:
                break
            nxt = series[i + 1]["t"] if i + 1 < len(series) else t_end
            w = max(minutes(s["t"], nxt), 0.0)
            if mask is not None and not mask(s):
                continue
            p = fn_pred(s)
            r = real_time(s, nxt) if truth == "time" else real_sub(s) if truth == "sub" else real(s)
            tw += abs(p - r) * w
            bw += (p - r) * w
            w_total += w
            te += abs(p - r)
            be += (p - r)
            n += 1
        if a_end == 0 or n == 0:
            return None
        return {"mae_time": tw / w_total if w_total else None, "bias_time": bw / w_total if w_total else None,
                "mae_ev": te / n, "bias_ev": be / n, "cover_time": w_total / span, "cover_ev": n / len(series)}

    cur = err_stats(lambda s: pct(s))
    only_cut = err_stats(lambda s: pct(s), mask=lambda s: s["plan"] == "cut")
    hybrid = {}
    free = {}  # модель вправе и сузить знаменатель: P = принято / E (не больше 100 %)
    for eps in EPS_GRID:
        E = a_end * (1 + eps)
        hybrid[eps] = err_stats(lambda s, E=E: pct(s, E))
        free[eps] = err_stats(lambda s, E=E: min(1.0, s["acc"] / E))
    cur_t = err_stats(lambda s: pct(s), truth="time")
    only_cut_t = err_stats(lambda s: pct(s), mask=lambda s: s["plan"] == "cut", truth="time")
    hybrid_t = err_stats(lambda s: pct(s, a_end), truth="time")
    cur_s = err_stats(lambda s: pct(s), truth="sub")
    only_cut_s = err_stats(lambda s: pct(s), mask=lambda s: s["plan"] == "cut", truth="sub")
    hybrid_s = err_stats(lambda s: pct(s, a_sub), truth="sub")
    free_s = err_stats(lambda s: min(1.0, s["acc"] / a_sub), truth="sub") if a_sub else None
    # на закрытии: во что упираются непринятые шаги цепочек
    unacc = {}
    for c in run["stepChains"]:
        if c["outcome"] not in ("accepted", "withdrawn"):
            unacc[c["outcome"]] = unacc.get(c["outcome"], 0) + 1

    # --- падения процента, прыжки знаменателя, ложные 100 % ---
    drops = []
    drops_by_return = drops_by_den = 0
    den_changes = den_up = den_down = 0
    false_full_events = 0
    false_full_time = 0.0
    prev = None
    first_acc_idx = next((i for i, s in enumerate(series) if s["acc"] > 0), None)
    den_after_first_acc = 0
    for i, s in enumerate(series):
        if prev is not None:
            if s["den"] != prev["den"]:
                den_changes += 1
                if s["den"] > prev["den"]:
                    den_up += 1
                    if first_acc_idx is not None and i > first_acc_idx:
                        den_after_first_acc += 1
                else:
                    den_down += 1
            if pct(s) < pct(prev) - 1e-9:
                drops.append(pct(prev) - pct(s))
                if s["acc"] < prev["acc"]:
                    drops_by_return += 1
                else:
                    drops_by_den += 1
        if pct(s) >= 1 - 1e-9 and a_end and real(s) < 1 - 1e-9:
            false_full_events += 1
            nxt = series[i + 1]["t"] if i + 1 < len(series) else t_end
            false_full_time += max(minutes(s["t"], nxt), 0.0)
        prev = s

    # --- план в начале ---
    first_step_t = min(c["created"] for c in chains)
    den_f, acc_f, plan_f, _ = state_at(first_step_t)
    # знаменатель в момент первой карточки шага — доля от итога
    n_total_end = max(n_end, 1)
    plan_keys_first = len({k for _, keys in active_plans(plans, first_step_t) for k in keys if k not in withdrawn_keys})
    if plan_f == "cut":
        start_kind = "нарезан"
    elif plan_f in ("pending", "partial"):
        start_kind = "пуст/частичен"
    else:
        start_kind = "нет плана"
    first_acc_t = series[first_acc_idx]["t"] if first_acc_idx is not None else None
    den_at_first_acc = series[first_acc_idx]["den"] if first_acc_idx is not None else None

    # --- срок ---
    t_ready = max([ts(r["completedAt"]) for c in run["stepChains"] for r in c["rounds"]
                   if r["completedAt"] and r["verdict"] in ACCEPTED] +
                  [ts((run["final"] or {}).get("completedAt")) or t0] +
                  [ts(m["completedAt"]) for m in run.get("merges", []) if m.get("completedAt")] +
                  [t0])
    t_ready = min(t_ready, t_end)
    s0 = ts(epic["createdAt"])
    note = epic.get("note") or {}
    est = note.get("estimate") or {}
    totals = ((d.get("stats") or {}).get("timeline") or {}).get("totals") or {}
    return {
        "id": epic["id"], "title": epic["title"], "state": run["state"],
        "closed_at": t_close, "s0": s0, "t0": t0, "t_ready": t_ready,
        "n_chains": n_chains, "a_end": a_end, "n_end": n_end,
        "product": {"accepted": run["stepProgress"]["accepted"], "total": run["stepProgress"]["total"],
                    "plan_state": run["stepProgress"]["planState"]},
        "recon_match": (last_state[0] == run["stepProgress"]["total"]) and (state_at(t_end)[1] == run["stepProgress"]["accepted"]),
        "recon_final": (last_state[0], state_at(t_end)[1]),
        "end_pct": state_at(t_end)[1] / max(last_state[0], 1),
        "events": len(series), "span_min": span,
        "den_changes": den_changes, "den_up": den_up, "den_down": den_down, "den_up_after_first_acc": den_after_first_acc,
        "drops": len(drops), "drops_by_return": drops_by_return, "drops_by_den": drops_by_den, "max_drop": max(drops) if drops else 0.0,
        "false_full_events": false_full_events, "false_full_share": false_full_time / span,
        "start_plan": plan_f, "start_kind": start_kind, "plan_keys_first": plan_keys_first,
        "den_first_share": den_f / n_total_end, "den_first": den_f,
        "den_first_acc": den_at_first_acc,
        "den_at_first_acc_share": (den_at_first_acc / n_total_end) if den_at_first_acc else None,
        "cur": cur, "only_cut": only_cut, "hybrid": hybrid, "free": free,
        "cur_s": cur_s, "only_cut_s": only_cut_s, "hybrid_s": hybrid_s, "free_s": free_s, "a_sub": a_sub, "cur_t": cur_t, "only_cut_t": only_cut_t, "hybrid_t": hybrid_t, "unaccepted": unacc,
        "initial_eta": epic.get("initialEtaMinutes"),
        "note_eta": est.get("etaMinutes"), "note_at": ts(note.get("at")),
        "wall_work_min": totals.get("workWallMinutes"),
    }


def selftest():
    """Синтетическая фича с известным ответом: проверяет саму восстановительную логику."""
    def z(hm, day="2026-11-01"):
        return "%sT%s:00Z" % (day, hm)

    def task(i, role, ref, created, done=None, status=None):
        return {"id": i, "role": role, "stepRef": ref, "createdAt": z(created), "status": status or ("done" if done else "inProgress"),
                "completedAt": z(done) if done else None}

    steps = [task("a", "step", "A", "10:10", "10:20"), task("b", "step", "B", "10:10", "10:25"),
             task("b2", "rework", "B", "10:36", "10:50"), task("c", "step", "C", "10:15", "10:40"),
             task("c2", "rework", "C", "10:50", "11:10"), task("d", "step", "D", "10:12", "10:30"), task("e", "step", "E", "10:55")]

    def rnd(v, at):
        return {"verdict": v, "completedAt": z(at)}

    chains = [
        {"stepRef": "A", "taskIds": ["a"], "outcome": "accepted", "acceptedAt": z("10:30"), "rounds": [rnd("accepted", "10:30")]},
        {"stepRef": "B", "taskIds": ["b", "b2"], "outcome": "accepted", "acceptedAt": z("11:00"),
         "rounds": [rnd("returned", "10:35"), rnd("accepted", "11:00")]},
        {"stepRef": "C", "taskIds": ["c", "c2"], "outcome": "accepted", "acceptedAt": z("10:45"),
         "rounds": [rnd("accepted_with_remarks", "10:45")]},
        {"stepRef": "D", "taskIds": ["d"], "outcome": "pending", "acceptedAt": None, "rounds": []},
        # шаг вне плана, заведённый после первых принятий: знаменатель вырастет с 4 до 5
        {"stepRef": "E", "taskIds": ["e"], "outcome": "running", "acceptedAt": None, "rounds": []},
    ]
    plan = {"id": "p", "role": "plan", "createdAt": z("10:00"), "verification": None,
            "planSteps": [{"stepRef": k, "isStep": True, "kind": None} for k in "ABCD"]}
    d = {"run": {"epic": {"id": "e", "title": "синтетика", "status": "done", "createdAt": z("09:50"), "completedAt": z("11:30"),
                          "initialEtaMinutes": 120, "note": None},
                 "steps": steps, "stepChains": chains, "plan": [plan], "final": None, "merges": [], "state": "closed",
                 "stepProgress": {"accepted": 3, "total": 5, "planState": "cut"}},
         "stats": {"timeline": {"totals": {"workWallMinutes": 90}}}}
    r = analyze_feature(d, [plan])
    checks = {
        "знаменатель 4 с момента плана, один рост до 5 после принятий": r["den_first"] == 4 and r["den_changes"] == 1 and r["den_up_after_first_acc"] == 1,
        "итог 3 из 5 (сверка с продуктом сходится)": r["recon_final"] == (5, 3) and r["recon_match"],
        "два падения: возврат C из-за открытой доработки и рост знаменателя": r["drops"] == 2 and r["drops_by_return"] == 1 and r["drops_by_den"] == 1,
        "план на старте — нарезан": r["start_kind"] == "нарезан",
        "ложных 100 % нет": r["false_full_events"] == 0,
        "итог на закрытии 60 %": abs(r["end_pct"] - 0.6) < 1e-9,
    }
    bad = [k for k, ok in checks.items() if not ok]
    for k, ok in checks.items():
        print(("ok   " if ok else "FAIL ") + k)
    return 1 if bad else 0


def pct_s(x):
    return "—" if x is None else "%.0f%%" % (100 * x)


def median(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None


def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default="/tmp/fpa-cache")
    ap.add_argument("--json", help="куда сохранить сводку по фичам")
    ap.add_argument("--table", help="куда записать markdown-таблицы")
    ap.add_argument("--only-matched", action="store_true", help="оставить только фичи, где восстановление сошлось с продуктом (проверка устойчивости)")
    ap.add_argument("--selftest", action="store_true", help="проверка логики на синтетической фиче и выход")
    args = ap.parse_args()
    if args.selftest:
        return selftest()

    rows, skipped = [], []
    total_done = 0
    for d, plans in load(args.cache):
        if d["run"]["epic"]["status"] != "done":
            continue
        total_done += 1
        r = analyze_feature(d, plans)
        (skipped if "skip" in r else rows).append(r)
    if args.only_matched:
        skipped += [r for r in rows if not r["recon_match"]]
        rows = [r for r in rows if r["recon_match"]]
    rows.sort(key=lambda r: r["closed_at"])
    out = []

    def p(line=""):
        out.append(line)

    p("Закрытых эпиков (status=done): %d; из них фич с ≥%d шагами: %d; пропущено: %d" %
      (total_done, MIN_STEPS, len(rows), len(skipped)))
    ok = [r for r in rows if r["recon_match"]]
    p("Реконструкция сошлась с продуктом (accepted и total на конец): %d из %d" % (len(ok), len(rows)))
    p()
    p("| Фича | Закрыта | План на старте | Знаменатель: 1-я карточка → 1-е принятие → итог | Изменений знам. (после 1-го принятия) | Падений % (из-за знам. / возврата; макс, п.п.) | Ложные 100 % | Итог на закрытии | Срок до готовности, ч (календарь / рабочее) | |P−R| по принятым |")
    p("|---|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        wall_h = ("%.0f" % (r["wall_work_min"] / 60)) if r["wall_work_min"] else "—"
        p("| %s | %s | %s | %d → %d → %d | %d (%d) | %d / %d; %.0f | %s | %s (%d/%d) | %.0f / %s | %s |" % (
            r["title"][:42].replace("|", "/"), r["closed_at"].strftime("%d.%m"), r["start_kind"],
            r["den_first"], r["den_first_acc"] or 0, r["n_end"], r["den_changes"], r["den_up_after_first_acc"],
            r["drops_by_den"], r["drops_by_return"], 100 * r["max_drop"],
            ("%d раз, %.1f%% времени" % (r["false_full_events"], 100 * r["false_full_share"])) if r["false_full_events"] else "нет",
            pct_s(r["end_pct"]), r["recon_final"][1], r["recon_final"][0],
            minutes(r["s0"], r["t_ready"]) / 60, wall_h,
            pct_s(r["cur"]["mae_time"]) if r["cur"] else "—"))
    p()
    # --- сводка ---
    with_acc = [r for r in rows if r["cur"]]
    p("== Сводка по %d фичам (A_end>0: %d) ==" % (len(rows), len(with_acc)))
    p("план на старте: " + ", ".join("%s=%d" % (k, sum(1 for r in rows if r["start_kind"] == k)) for k in ("нарезан", "пуст/частичен", "нет плана")))
    p("знаменатель менялся хотя бы раз: %d из %d; медиана изменений: %s" % (
        sum(1 for r in rows if r["den_changes"] > 0), len(rows), median(r["den_changes"] for r in rows)))
    p("знаменатель рос после первого принятия: %d из %d" % (sum(1 for r in rows if r["den_up_after_first_acc"] > 0), len(rows)))
    p("процент падал хотя бы раз: %d из %d; всего падений: %d" % (sum(1 for r in rows if r["drops"] > 0), len(rows), sum(r["drops"] for r in rows)))
    p("причины падений: из-за возврата (принято стало меньше) %d, из-за роста знаменателя %d; фич с падением из-за знаменателя: %d из %d" % (
        sum(r["drops_by_return"] for r in rows), sum(r["drops_by_den"] for r in rows),
        sum(1 for r in rows if r["drops_by_den"] > 0), len(rows)))
    p("ложные 100 %% до реальной готовности: %d из %d" % (sum(1 for r in rows if r["false_full_events"] > 0), len(rows)))
    p("итог на закрытии (accepted/total): медиана %s; <100%%: %d из %d" % (
        pct_s(median(r["end_pct"] for r in rows)), sum(1 for r in rows if r["end_pct"] < 1 - 1e-9), len(rows)))
    p("знаменатель при первой карточке / итог: медиана %s" % pct_s(median(r["den_first_share"] for r in rows)))
    p("знаменатель при первом принятии / итог: медиана %s" % pct_s(median(r["den_at_first_acc_share"] for r in rows)))
    p()
    p("== По плану на старте ==")
    for kind in ("нарезан", "пуст/частичен", "нет плана"):
        g = [r for r in rows if r["start_kind"] == kind]
        ga = [r for r in g if r["cur"]]
        if not g:
            continue
        p("%s: %d фич; знаменатель при 1-й карточке / итог: медиана %s; рос после 1-го принятия: %d из %d; падений из-за знаменателя на фичу (медиана): %s; |P−R| по принятым: %s; итог на закрытии (медиана): %s" % (
            kind, len(g), pct_s(median(r["den_first_share"] for r in g)),
            sum(1 for r in g if r["den_up_after_first_acc"] > 0), len(g), median(r["drops_by_den"] for r in g),
            pct_s(mean(r["cur"]["mae_time"] for r in ga)), pct_s(median(r["end_pct"] for r in g))))
    p()
    p("== Варианты счёта: средний |P−R| (по времени | по событиям), смещение P−R ==")
    p("(а0) карточки как есть:      %s | %s; смещение %s" % (
        pct_s(mean(r["cur"]["mae_time"] for r in with_acc)), pct_s(mean(r["cur"]["mae_ev"] for r in with_acc)),
        "%+.0f п.п." % (100 * mean(r["cur"]["bias_time"] for r in with_acc))))
    cut = [r for r in with_acc if r["only_cut"]]
    p("(а) только при плане cut:    ошибка %s (по времени) на %d фичах; покрытие времени %s; без плана-на-t показа нет" % (
        pct_s(mean(r["only_cut"]["mae_time"] for r in cut)), len(cut), pct_s(mean(r["only_cut"]["cover_time"] for r in with_acc if r["only_cut"]))))
    p("   то же у них же, но по карточкам как есть: %s" % pct_s(mean(r["cur"]["mae_time"] for r in cut)))
    p("   покрытие по всем %d фичам (нет показа = 0): %s" % (len(with_acc), pct_s(mean((r["only_cut"]["cover_time"] if r["only_cut"] else 0.0) for r in with_acc))))
    for eps in EPS_GRID:
        hs = [r["hybrid"][eps] for r in with_acc]
        p("(в) гибрид, оценка итога %+d%% от факта: %s | %s; смещение %+.0f п.п." % (
            100 * eps, pct_s(mean(h["mae_time"] for h in hs)), pct_s(mean(h["mae_ev"] for h in hs)),
            100 * mean(h["bias_time"] for h in hs)))
    wins = {eps: sum(1 for r in with_acc if r["hybrid"][eps]["mae_time"] < r["cur"]["mae_time"]) for eps in EPS_GRID}
    p("гибрид лучше карточек (по фичам, по времени): " + ", ".join("%+d%%: %d из %d" % (100 * e, w, len(with_acc)) for e, w in wins.items()))
    p("-- модель вправе и СУЗИТЬ знаменатель (P = принято / оценка итога, не больше 100 %) --")
    for eps in EPS_GRID:
        fs = [r["free"][eps] for r in with_acc]
        p("(в′) оценка итога %+d%% от факта: %s | %s; смещение %+.0f п.п." % (
            100 * eps, pct_s(mean(h["mae_time"] for h in fs)), pct_s(mean(h["mae_ev"] for h in fs)),
            100 * mean(h["bias_time"] for h in fs)))
    wins_f = {eps: sum(1 for r in with_acc if r["free"][eps]["mae_time"] < r["cur"]["mae_time"]) for eps in EPS_GRID}
    p("(в′) лучше карточек (по фичам, по времени): " + ", ".join("%+d%%: %d из %d" % (100 * e, w, len(with_acc)) for e, w in wins_f.items()))
    p()
    p("== Вторая «истина»: R(t) = доля прошедшего времени жизни фичи (грубее, но не зависит от шагов) ==")
    p("(а0) карточки как есть: %s; смещение %+.0f п.п." % (
        pct_s(mean(r["cur_t"]["mae_time"] for r in with_acc)), 100 * mean(r["cur_t"]["bias_time"] for r in with_acc)))
    cut_t = [r for r in with_acc if r["only_cut_t"]]
    p("(а) только при плане cut: %s на %d фичах (карточки у них же: %s)" % (
        pct_s(mean(r["only_cut_t"]["mae_time"] for r in cut_t)), len(cut_t), pct_s(mean(r["cur_t"]["mae_time"] for r in cut_t))))
    p("(в) гибрид с точной оценкой итога (только расширять): %s; смещение %+.0f п.п." % (
        pct_s(mean(r["hybrid_t"]["mae_time"] for r in with_acc)), 100 * mean(r["hybrid_t"]["bias_time"] for r in with_acc)))
    p()
    p("== Третья «истина»: R(t) = доля СДАННЫХ шагов (Done-задача шага; ревью не обязательно) ==")
    with_sub = [r for r in with_acc if r["cur_s"]]
    p("(а0) карточки как есть: %s; смещение %+.0f п.п. (n=%d)" % (
        pct_s(mean(r["cur_s"]["mae_time"] for r in with_sub)), 100 * mean(r["cur_s"]["bias_time"] for r in with_sub), len(with_sub)))
    cut_s = [r for r in with_sub if r["only_cut_s"]]
    p("(а) только при плане cut: %s на %d фичах (карточки у них же: %s)" % (
        pct_s(mean(r["only_cut_s"]["mae_time"] for r in cut_s)), len(cut_s), pct_s(mean(r["cur_s"]["mae_time"] for r in cut_s))))
    p("(в) гибрид, точная оценка итога (только расширять): %s; смещение %+.0f п.п." % (
        pct_s(mean(r["hybrid_s"]["mae_time"] for r in with_sub)), 100 * mean(r["hybrid_s"]["bias_time"] for r in with_sub)))
    fsub = [r for r in with_sub if r["free_s"]]
    p("(в′) точная оценка, можно сузить: %s; смещение %+.0f п.п." % (
        pct_s(mean(r["free_s"]["mae_time"] for r in fsub)), 100 * mean(r["free_s"]["bias_time"] for r in fsub)))
    p()
    p("== Что осталось непринятым к закрытию (шаги цепочек по исходу) ==")
    agg = {}
    for r in rows:
        for k, v in r["unaccepted"].items():
            agg[k] = agg.get(k, 0) + v
    tot_steps = sum(r["n_end"] for r in rows)
    p("всего шагов (итог) в выборке: %d; непринятых: %s" % (tot_steps, ", ".join("%s=%d" % kv for kv in sorted(agg.items()))))
    p("фич, где не принято ничего лишнего (все шаги приняты): %d из %d" % (sum(1 for r in rows if not r["unaccepted"]), len(rows)))
    p()

    # --- срок ---
    p("== Срок ==")
    import math
    sample = [r for r in rows if r["initial_eta"]]
    ready = {r["id"]: minutes(r["s0"], r["t_ready"]) for r in rows}
    wall = {r["id"]: r["wall_work_min"] for r in rows}
    p("initialEtaMinutes есть у %d из %d закрытых фич (поле введено 29.10; до того начальной оценки не писали)" % (len(sample), len(rows)))
    p("Ошибка = (оценка − факт) / факт; «×» — во сколько раз оценка отличается от факта (>1 — завышена).")
    p("| Фича | Начальная оценка, мин | Факт до готовности, мин (календарь) | Ошибка | × | Факт рабочий (wall), мин | Ошибка | Наивная: медиана срока др. фич, мин | Ошибка | Оценка оркестратора (последняя), мин | Факт от её записи, мин |")
    p("|---|---|---|---|---|---|---|---|---|---|---|")
    ratios = {"initial": [], "initial_wall": [], "naive": [], "note": []}

    def fmt_err(est, act):
        return ("%+.0f%%" % (100 * (est - act) / act)) if act and act > 0 else "—"

    for r in sample:
        act = ready[r["id"]]
        w = wall[r["id"]]
        naive = statistics.median([v for k, v in ready.items() if k != r["id"]])
        n_eta = r["note_eta"]
        rem = minutes(r["note_at"], r["t_ready"]) if r["note_at"] else None
        if act > 0:
            ratios["initial"].append(r["initial_eta"] / act)
            ratios["naive"].append(naive / act)
        if w:
            ratios["initial_wall"].append(r["initial_eta"] / w)
        if n_eta and rem and rem > 5:
            ratios["note"].append(n_eta / rem)
        p("| %s | %d | %.0f | %s | %.2f | %s | %s | %.0f | %s | %s | %s |" % (
            r["title"][:36].replace("|", "/"), r["initial_eta"], act, fmt_err(r["initial_eta"], act),
            r["initial_eta"] / act if act > 0 else float("nan"),
            ("%.0f" % w) if w else "—", fmt_err(r["initial_eta"], w),
            naive, fmt_err(naive, act),
            n_eta if n_eta else "—", ("%.0f" % rem) if rem is not None else "—"))
    p()

    def factor(xs):
        """Типичный множитель ошибки: exp(медиана |ln(оценка/факт)|); и доля попаданий в коридор ×2."""
        if not xs:
            return "n=0"
        f = math.exp(statistics.median(abs(math.log(x)) for x in xs))
        in2 = sum(1 for x in xs if 0.5 <= x <= 2)
        over = sum(1 for x in xs if x > 1)
        return "n=%d: типичная ошибка ×%.1f, в коридоре ×2: %d из %d, завышено: %d, занижено: %d" % (len(xs), f, in2, len(xs), over, len(xs) - over)

    p("начальная оценка vs календарный факт: " + factor(ratios["initial"]))
    p("начальная оценка vs рабочий факт (wall): " + factor(ratios["initial_wall"]))
    p("наивная (медиана других фич) vs календарный факт, те же фичи: " + factor(ratios["naive"]))
    p("последняя оценка оркестратора vs факт от её записи: " + factor(ratios["note"]))
    p("оценок оркестратора, записанных ДО готовности (можно проверить): %d из %d закрытых фич с note" % (
        sum(1 for r in rows if r["note_at"] and r["note_at"] < r["t_ready"]), sum(1 for r in rows if r["note_at"])))
    p("медианный срок до готовности по всем %d фичам: %.0f мин (%.1f ч); медиана рабочего: %.1f ч" % (
        len(rows), statistics.median(ready.values()), statistics.median(ready.values()) / 60,
        statistics.median([v for v in wall.values() if v]) / 60))

    text = "\n".join(out)
    print(text)
    if args.table:
        with open(args.table, "w", encoding="utf-8") as f:
            f.write(text + "\n")
    if args.json:
        def conv(o):
            if isinstance(o, datetime):
                return o.isoformat()
            return str(o)
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"rows": rows, "skipped": skipped}, f, ensure_ascii=False, indent=1, default=conv)
    return 0


if __name__ == "__main__":
    sys.exit(main())
