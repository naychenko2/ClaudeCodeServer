#!/usr/bin/env python3
"""Хранитель префикса prefix cache локальной модели (2026-10-08).

Прокси складывает снимки префикса первого хода (model + tools + system, без текста человека)
в PREFIX_SNAPSHOT_DIR. Этот скрипт по таймеру берёт снимки не старше MAX_AGE_HOURS и, если
движок свободен, шлёт каждый через прокси с одним пустым ходом и max_tokens=1: при горячем
префиксе это доли секунды, при выпавшем — один прогрев в фоне вместо 5–7 с на глазах у человека.
Печатает usage ответа — по cache_read видно, был ли префикс в кэше до прогрева.
"""
import glob, json, os, sys, time, urllib.request

SNAP = os.environ.get("PREFIX_SNAPSHOT_DIR", "/var/lib/thinking-strip-proxy/prefix")
PROXY = os.environ.get("PROXY_URL", "http://127.0.0.1:18021")
METRICS = os.environ.get("ENGINE_METRICS", "http://127.0.0.1:18020/metrics")
MAX_AGE = float(os.environ.get("MAX_AGE_HOURS", "24")) * 3600
TIMEOUT = int(os.environ.get("KEEPER_TIMEOUT", "120"))


def engine_busy():
    try:
        with urllib.request.urlopen(METRICS, timeout=5) as r:
            for line in r.read().decode().splitlines():
                if line.startswith("vllm:num_requests_running"):
                    return float(line.rsplit(" ", 1)[1]) > 0
    except Exception as e:
        print(f"метрики недоступны: {e}", flush=True)
        return True
    return False


def touch(path):
    body = json.load(open(path, encoding="utf-8"))
    req = {"model": body["model"], "max_tokens": 1, "system": body["system"], "tools": body["tools"],
           "messages": [{"role": "user", "content": "."}]}
    data = json.dumps(req, ensure_ascii=False).encode("utf-8")
    r = urllib.request.Request(PROXY + "/v1/messages", data=data,
                               headers={"Content-Type": "application/json", "anthropic-version": "2023-06-01"})
    t0 = time.perf_counter()
    with urllib.request.urlopen(r, timeout=TIMEOUT) as resp:
        u = json.load(resp).get("usage", {})
    dt = time.perf_counter() - t0
    tot = u.get("input_tokens", 0) + u.get("cache_read_input_tokens", 0) + u.get("cache_creation_input_tokens", 0)
    print(f"{os.path.basename(path)}: {dt:.1f} с, токенов {tot}, из кэша {u.get('cache_read_input_tokens', 0)}, "
          f"посчитано {u.get('cache_creation_input_tokens', 0) + u.get('input_tokens', 0)}", flush=True)


def main():
    files = [f for f in glob.glob(os.path.join(SNAP, "*.json")) if time.time() - os.path.getmtime(f) < MAX_AGE]
    if not files:
        print("снимков нет", flush=True); return 0
    if engine_busy():
        print("движок занят — пропуск", flush=True); return 0
    for f in sorted(files, key=os.path.getmtime, reverse=True):
        try:
            touch(f)
        except Exception as e:
            print(f"{os.path.basename(f)}: ошибка {e}", flush=True)
        if engine_busy():
            print("движок занят — остальные снимки в следующий раз", flush=True); break
    return 0


if __name__ == "__main__":
    sys.exit(main())
