#!/usr/bin/env python3
"""Root-помощник: снимает датчики BMC (VRM, вентиляторы, System Temp) через IPMI.

Чтение /dev/ipmi0 требует root, а бэкенд и агент узла работают от обычного
пользователя. Помощник в цикле запускает
`/usr/bin/ipmitool -I open -d 0 sdr elist full` и атомарно пишет
/run/ccs-ipmi/sdr.json вида {"at": <unix-мс часов узла>, "sdr": "<stdout дословно>"}.
Разбор строк SDR здесь не делается — он живёт в бэкенде.

Период — 15 с, запуски строго последовательные: пауза отсчитывается от КОНЦА
предыдущего опроса (stop.wait после записи), поэтому два ipmitool к одному
BMC не бывают одновременно, даже когда опрос затянулся до 4,5 с.
Метка "at" ставится ПОСЛЕ завершения ipmitool: медленный опрос не выглядит
свежее, чем он есть. Устарела метка — бэкенд считает данные недоступными.

Устройство задано явно (-d 0): на worker два KCS-интерфейса, /dev/ipmi0 и
/dev/ipmi1, полагаться на умолчание ipmitool нельзя.
"""
import json
import os
import signal
import subprocess
import sys
import threading
import time

OUT_DIR = "/run/ccs-ipmi"
OUT_FILE = "sdr.json"
IPMITOOL = "/usr/bin/ipmitool"
IPMITOOL_ARGS = ["-I", "open", "-d", "0", "sdr", "elist", "full"]
IPMITOOL_TIMEOUT_S = 10
INTERVAL_S = 15.0
# Агент всё равно не повезёт файл больше этого размера.
MAX_FILE_BYTES = 16 * 1024


def run_ipmitool(timeout=IPMITOOL_TIMEOUT_S):
    """Запускает ipmitool без оболочки; возвращает stdout. Любой сбой — исключение."""
    proc = subprocess.run(
        [IPMITOOL] + IPMITOOL_ARGS,
        shell=False,
        capture_output=True,
        timeout=timeout,
        check=False,
    )
    if proc.returncode != 0:
        err = proc.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError("ipmitool вернул код %d: %s" % (proc.returncode, err))
    return proc.stdout.decode("utf-8", "replace")


def collect(run, now_ms=None):
    """Один опрос. Возвращает payload или None, если файл перезаписывать нельзя."""
    try:
        sdr = run()
    except Exception as e:  # включая TimeoutExpired и FileNotFoundError
        print("ipmitool не отдал данные: %s" % e, file=sys.stderr, flush=True)
        return None
    # Время берётся после завершения опроса, а не до запуска.
    payload = {
        "at": int(time.time() * 1000) if now_ms is None else now_ms,
        "sdr": sdr,
    }
    size = len(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
    if size > MAX_FILE_BYTES:
        print("вывод ipmitool слишком велик (%d Б в файле, предел %d): файл не пишем"
              % (size, MAX_FILE_BYTES), file=sys.stderr, flush=True)
        return None
    return payload


def write_atomic(payload, out_dir=OUT_DIR, out_file=OUT_FILE):
    # Имя tmp уникально на процесс: ручной запуск рядом со службой не затрёт чужой.
    tmp = os.path.join(out_dir, "%s.%d.tmp" % (out_file, os.getpid()))
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644)
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(payload, f, separators=(",", ":"))
        os.chmod(tmp, 0o644)
        os.replace(tmp, os.path.join(out_dir, out_file))
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def run(stop, run_ipmitool=run_ipmitool, out_dir=OUT_DIR, interval=INTERVAL_S):
    while not stop.is_set():
        try:
            payload = collect(run_ipmitool)
            if payload is not None:
                write_atomic(payload, out_dir)
        except Exception as e:
            print("итерация не удалась: %s" % e, file=sys.stderr, flush=True)
        # Пауза — после записи: период считается от конца опроса.
        stop.wait(interval)


def main():
    os.umask(0o022)
    stop = threading.Event()
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: stop.set())
    run(stop)
    return 0


if __name__ == "__main__":
    sys.exit(main())
