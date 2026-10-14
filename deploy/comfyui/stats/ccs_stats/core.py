"""Чистая сборка ответа GET /ccs/stats: без ComfyUI и без pynvml на верхнем уровне.

Источники (comfy.model_management и pynvml) передаются аргументами, поэтому тесты подменяют
их заглушками. Каждый источник собирается отдельно: сбой одного не роняет другой, а сбой
целиком даёт пустой блок и короткую причину — только имя типа исключения, без текста:
текст исключений ComfyUI и NVML бывает с путями файловой системы.
"""
import re

RESPONSE_VERSION = 1

# Потолок списка: ответ маленький, а больше пары десятков моделей в памяти одной карты не бывает
MAX_LOADED = 16
MAX_NAME = 96
MIB = 1024 * 1024

_UNSAFE = re.compile(r"[^A-Za-z0-9._+-]")
_GPU_UNSAFE = re.compile(r"[^A-Za-z0-9 ._+-]")


def _err(where, e):
    return f"{where}: {type(e).__name__}"


def _basename(path):
    """Имя файла без каталогов. Путь с компонентом loras скрывается целиком: там лежат
    LoRA пользователей (loras/ccs/<владелец>/<слаг>), их имена наружу не отдаём."""
    if not isinstance(path, str) or not path:
        return None
    parts = [p for p in re.split(r"[\\/]+", path) if p]
    if not parts:
        return None
    if any(p.lower() == "loras" for p in parts[:-1]):
        return "lora"
    return parts[-1]


def _clean(name):
    name = _UNSAFE.sub("_", name)[:MAX_NAME]
    return name or None


def model_name(patcher):
    """Имя загруженной модели: basename файла из cached_patcher_init (его заполняют штатные
    загрузчики ComfyUI 0.37: диффузия, CLIP, чекпойнт), иначе имя класса модели (QwenImage,
    AutoencoderKL), иначе None."""
    init = getattr(patcher, "cached_patcher_init", None)
    if isinstance(init, tuple) and len(init) >= 2 and isinstance(init[1], tuple) and init[1]:
        first = init[1][0]
        paths = first if isinstance(first, (list, tuple)) else [first]
        names = [n for n in (_basename(p) for p in paths) if n]
        if names:
            return _clean("+".join(dict.fromkeys(names)))
    inner = getattr(patcher, "model", None)
    if inner is not None:
        return _clean(type(inner).__name__)
    return None


def _mib(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0:
        return None
    return int(round(value / MIB))


def _call_mib(fn):
    try:
        return _mib(fn())
    except Exception:
        return None


def collect_loaded(mm):
    """mm — модуль comfy.model_management (или заглушка). Возвращает (список, ошибка)."""
    try:
        # Копия списка: его меняет поток исполнения графа, пока маршрут читает в цикле событий
        loaded = list(mm.current_loaded_models)
    except Exception as e:
        return [], _err("current_loaded_models", e)

    out = []
    for lm in loaded:
        if len(out) >= MAX_LOADED:
            break
        try:
            patcher = lm.model  # weakref: модель могла уже умереть
            if patcher is None:
                continue
            name = model_name(patcher)
            if not name:
                continue
            out.append({
                "name": name,
                "sizeMb": _call_mib(lm.model_memory),
                "loadedMb": _call_mib(lm.model_loaded_memory),
            })
        except Exception:
            # Одна сломанная запись не прячет остальные
            continue
    return out, None


def _text(v):
    return v.decode("utf-8", "replace") if isinstance(v, bytes) else str(v)


def _safe(fn, *args):
    try:
        return fn(*args)
    except Exception:
        return None


def _gpu(nvml, i):
    h = nvml.nvmlDeviceGetHandleByIndex(i)
    util = _safe(nvml.nvmlDeviceGetUtilizationRates, h)
    mem = _safe(nvml.nvmlDeviceGetMemoryInfo, h)
    temp = _safe(nvml.nvmlDeviceGetTemperature, h, getattr(nvml, "NVML_TEMPERATURE_GPU", 0))
    power = _safe(nvml.nvmlDeviceGetPowerUsage, h)
    limit = _safe(nvml.nvmlDeviceGetEnforcedPowerLimit, h)
    name = _safe(nvml.nvmlDeviceGetName, h)
    return {
        "index": i,
        "name": _GPU_UNSAFE.sub("_", _text(name))[:MAX_NAME] if name is not None else None,
        "utilPerc": int(util.gpu) if util is not None else None,
        "memUsedMb": _mib(mem.used) if mem is not None else None,
        "memTotalMb": _mib(mem.total) if mem is not None else None,
        "tempC": int(temp) if temp is not None else None,
        # NVML отдаёт милливатты; в WSL мощность бывает недоступна — тогда null
        "powerW": int(round(power / 1000)) if power is not None else None,
        "powerLimitW": int(round(limit / 1000)) if limit is not None else None,
    }


def collect_gpus(nvml):
    """nvml — модуль pynvml (или заглушка), None — модуля нет. Возвращает (список|None, ошибка).
    nvmlInit вызывается каждый раз: счётчик инициализаций у NVML свой, повтор дешёвый."""
    if nvml is None:
        return None, "pynvml: ModuleNotFoundError"
    try:
        nvml.nvmlInit()
    except Exception as e:
        return None, _err("nvmlInit", e)
    try:
        count = nvml.nvmlDeviceGetCount()
        gpus = []
        for i in range(count):
            try:
                gpus.append(_gpu(nvml, i))
            except Exception:
                gpus.append({"index": i, "name": None, "utilPerc": None, "memUsedMb": None,
                             "memTotalMb": None, "tempC": None, "powerW": None,
                             "powerLimitW": None})
        return gpus, None
    except Exception as e:
        return None, _err("nvml", e)
    finally:
        try:
            nvml.nvmlShutdown()
        except Exception:
            pass


def build_response(mm, nvml):
    """Тело ответа маршрута. Поля error и gpusError появляются только при сбое."""
    loaded, error = collect_loaded(mm)
    gpus, gpus_error = collect_gpus(nvml)
    body = {"version": RESPONSE_VERSION, "loadedModels": loaded, "gpus": gpus}
    if error:
        body["error"] = error
    if gpus_error:
        body["gpusError"] = gpus_error
    return body
