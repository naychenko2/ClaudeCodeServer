#!/usr/bin/env python3
"""Root-помощник: читает температуру памяти GDDR6X и hotspot видеокарт RTX 3090.

Драйвер и NVML этих значений не отдают, а регистры карты читаются напрямую
через /dev/mem — для этого нужен root (точнее, CAP_SYS_RAWIO). Помощник раз
в INTERVAL_S секунд читает два 32-битных регистра у каждой найденной карты и
атомарно пишет JSON в /run/ccs-gddr6/temps.json; бэкенд только читает файл.
Метка "at" в файле — признак свежести: устарела — данных нет.

Факты о регистрах (BAR0 карты + смещение):
  0x0000E2A8 — температура памяти: (raw & 0xfff) // 0x20
  0x0002046C — hotspot:            (raw >> 8) & 0xff

Служба только читает: /sys/bus/pci/devices и /dev/mem (PROT_READ). Никаких
записей в устройства, сокетов и запуска процессов.
"""
import glob
import json
import mmap
import os
import signal
import sys
import threading
import time

OUT_DIR = "/run/ccs-gddr6"
OUT_FILE = "temps.json"
SYSFS_PCI = "/sys/bus/pci/devices"
DEV_MEM = "/dev/mem"
INTERVAL_S = 2.0

VENDOR_NVIDIA = 0x10DE
# Жёсткий список: чужие устройства по этому адресу читать нельзя.
SUPPORTED_DEVICE_IDS = frozenset({0x2204})  # RTX 3090 (GA102)

MEM_TEMP_OFFSET = 0x0000E2A8
HOTSPOT_OFFSET = 0x0002046C

TEMP_MIN_C = 1
TEMP_MAX_C = 150
_PAGE = mmap.PAGESIZE


def decode_mem_temp(raw):
    return (raw & 0xFFF) // 0x20


def decode_hotspot(raw):
    return (raw >> 8) & 0xFF


def sanitize(temp_c):
    """Значение вне 1…150 °C — мусор (регистр не отвечает), отдаём null."""
    if temp_c is None or not (TEMP_MIN_C <= temp_c <= TEMP_MAX_C):
        return None
    return temp_c


def _read_hex(path):
    with open(path, "r") as f:
        return int(f.read().strip(), 16)


def parse_bar0(resource_text):
    """BAR0 — начальный адрес из первой строки файла resource; 0 = BAR не назначен."""
    first = resource_text.splitlines()[0].split()
    start = int(first[0], 16)
    return start if start else None


def find_gpus(sysfs_root=SYSFS_PCI):
    """Карты из жёсткого списка: [(busId, deviceId, bar0)], по busId."""
    found = []
    for dev_dir in sorted(glob.glob(os.path.join(sysfs_root, "*"))):
        try:
            if _read_hex(os.path.join(dev_dir, "vendor")) != VENDOR_NVIDIA:
                continue
            device = _read_hex(os.path.join(dev_dir, "device"))
            if device not in SUPPORTED_DEVICE_IDS:
                continue
            with open(os.path.join(dev_dir, "resource"), "r") as f:
                bar0 = parse_bar0(f.read())
        except (OSError, ValueError, IndexError):
            continue
        if bar0 is None:
            continue
        found.append((os.path.basename(dev_dir), device, bar0))
    return found


def read_reg32(mem_fd, phys_addr):
    """Одно 32-битное чтение регистра: страница в режиме только чтение."""
    base = phys_addr & ~(_PAGE - 1)
    mm = mmap.mmap(mem_fd, _PAGE, flags=mmap.MAP_SHARED, prot=mmap.PROT_READ, offset=base)
    try:
        view = memoryview(mm).cast("I")
        try:
            return view[(phys_addr - base) // 4]
        finally:
            view.release()
    finally:
        mm.close()


def read_gpu(mem_fd, bar0):
    """(memTempC, hotspotTempC); сбой одного регистра не мешает другому."""
    result = []
    for offset, decode in ((MEM_TEMP_OFFSET, decode_mem_temp), (HOTSPOT_OFFSET, decode_hotspot)):
        try:
            result.append(sanitize(decode(read_reg32(mem_fd, bar0 + offset))))
        except (OSError, ValueError):
            result.append(None)
    return tuple(result)


def collect(mem_fd, sysfs_root=SYSFS_PCI, now_ms=None):
    gpus = []
    for bus_id, device, bar0 in find_gpus(sysfs_root):
        mem_c, hot_c = read_gpu(mem_fd, bar0) if mem_fd is not None else (None, None)
        gpus.append({
            "busId": bus_id,
            "deviceId": "0x%04x" % device,
            "memTempC": mem_c,
            "hotspotTempC": hot_c,
        })
    return {
        "v": 1,
        "at": int(time.time() * 1000) if now_ms is None else now_ms,
        "gpus": gpus,
    }


def write_atomic(payload, out_dir=OUT_DIR, out_file=OUT_FILE):
    tmp = os.path.join(out_dir, out_file + ".tmp")
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


def run(stop, dev_mem=DEV_MEM, sysfs_root=SYSFS_PCI, out_dir=OUT_DIR, interval=INTERVAL_S):
    while not stop.is_set():
        mem_fd = None
        try:
            mem_fd = os.open(dev_mem, os.O_RDONLY)
        except OSError as e:
            print("не открыть %s: %s" % (dev_mem, e), file=sys.stderr, flush=True)
        try:
            write_atomic(collect(mem_fd, sysfs_root), out_dir)
        except OSError as e:
            print("не записать результат: %s" % e, file=sys.stderr, flush=True)
        finally:
            if mem_fd is not None:
                os.close(mem_fd)
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
