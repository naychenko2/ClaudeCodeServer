"""Тесты маршрута /ccs/stats: чистая сборка ответа (core) с заглушками model_management и
pynvml и регистрация маршрута (__init__) с заглушками server и aiohttp. ComfyUI, pynvml и
aiohttp для запуска не нужны. Запуск из каталога пакета:

    python3 -m unittest discover -s tests -v

Что покрыто:
- имена моделей: basename без каталогов, CLIP из нескольких файлов, LoRA скрыта как «lora»,
  запасное имя класса, вычистка символов вне белого списка;
- загруженные модели: мёртвая weakref и сломанная запись пропускаются, сбой списка — пустой
  список и причина без текста исключения (там бывают пути), потолок 16, размеры в МиБ;
- карты: нет pynvml / сбой nvmlInit — gpus null с причиной; поле, которое NVML не отдал
  (мощность в WSL), — null, остальные поля на месте; nvmlShutdown после успешного Init;
- контракт: набор ключей ответа и карт совпадает с example-response.json рядом с пакетом;
- маршрут: путь /ccs/stats, ответ без кэша, сбой сборки и сбой импорта comfy не роняют его.
"""
import asyncio
import json
import os
import sys
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
NODE = os.path.dirname(HERE)    # каталог ccs_stats (где core.py)
PARENT = os.path.dirname(NODE)  # каталог stats/ (родитель для import ccs_stats)
for p in (NODE, PARENT):
    if p not in sys.path:
        sys.path.insert(0, p)

import core  # noqa: E402

EXAMPLE = os.path.join(PARENT, "example-response.json")
MIB = 1024 * 1024


class Patcher:
    def __init__(self, init=None, model=None):
        self.cached_patcher_init = init
        self.model = model


class QwenImage:
    pass


class Loaded:
    def __init__(self, patcher, size=None, loaded=None):
        self._patcher = patcher
        self._size = size
        self._loaded = loaded

    @property
    def model(self):
        return self._patcher

    def model_memory(self):
        if self._size is None:
            raise RuntimeError("нет размера")
        return self._size

    def model_loaded_memory(self):
        if self._loaded is None:
            raise RuntimeError("нет размера")
        return self._loaded


def mm_with(*loaded):
    return types.SimpleNamespace(current_loaded_models=list(loaded))


class FakeNvml:
    NVML_TEMPERATURE_GPU = 0

    def __init__(self, count=1, power=338_000, init_error=None):
        self.count = count
        self.power = power
        self.init_error = init_error
        self.shutdowns = 0

    def nvmlInit(self):
        if self.init_error:
            raise self.init_error

    def nvmlShutdown(self):
        self.shutdowns += 1

    def nvmlDeviceGetCount(self):
        return self.count

    def nvmlDeviceGetHandleByIndex(self, i):
        return i

    def nvmlDeviceGetName(self, h):
        return b"NVIDIA GeForce RTX 3090"

    def nvmlDeviceGetUtilizationRates(self, h):
        return types.SimpleNamespace(gpu=97, memory=40)

    def nvmlDeviceGetMemoryInfo(self, h):
        return types.SimpleNamespace(used=21874 * MIB, total=24576 * MIB, free=2702 * MIB)

    def nvmlDeviceGetTemperature(self, h, kind):
        return 71

    def nvmlDeviceGetPowerUsage(self, h):
        if self.power is None:
            raise RuntimeError("Not Supported")
        return self.power

    def nvmlDeviceGetEnforcedPowerLimit(self, h):
        return 350_000


class ModelNameTests(unittest.TestCase):
    def test_basename_without_dirs(self):
        p = Patcher(init=(object, ("/home/an/ai-data/comfy-models/diffusion_models/"
                                   "qwen_image_2.1_int8_convrot.safetensors", {})))
        self.assertEqual(core.model_name(p), "qwen_image_2.1_int8_convrot.safetensors")

    def test_clip_from_several_files(self):
        p = Patcher(init=(object, (["/m/te/a.safetensors", "C:\\m\\te\\b.safetensors"], None)))
        self.assertEqual(core.model_name(p), "a.safetensors+b.safetensors")

    def test_user_lora_hidden(self):
        p = Patcher(init=(object, ("/m/loras/ccs/0f3a/andrey-v3.safetensors",)))
        self.assertEqual(core.model_name(p), "lora")

    def test_class_name_fallback(self):
        self.assertEqual(core.model_name(Patcher(init=None, model=QwenImage())), "QwenImage")
        self.assertIsNone(core.model_name(Patcher()))

    def test_unsafe_chars_replaced(self):
        p = Patcher(init=(object, ("/m/модель версия 2.safetensors",)))
        name = core.model_name(p)
        self.assertRegex(name, r"^[A-Za-z0-9._+-]+$")
        self.assertTrue(name.endswith("_2.safetensors"))


class LoadedTests(unittest.TestCase):
    def test_sizes_in_mib_and_skips(self):
        good = Loaded(Patcher(init=(object, ("/x/vae.safetensors",))), 242 * MIB, 242 * MIB)
        dead = Loaded(None)
        nosize = Loaded(Patcher(model=QwenImage()))
        loaded, error = core.collect_loaded(mm_with(good, dead, nosize))
        self.assertIsNone(error)
        self.assertEqual(loaded, [
            {"name": "vae.safetensors", "sizeMb": 242, "loadedMb": 242},
            {"name": "QwenImage", "sizeMb": None, "loadedMb": None},
        ])

    def test_broken_entry_does_not_hide_others(self):
        class Boom:
            @property
            def model(self):
                raise RuntimeError("/secret/path")
        ok = Loaded(Patcher(model=QwenImage()), MIB, 0)
        loaded, error = core.collect_loaded(mm_with(Boom(), ok))
        self.assertIsNone(error)
        self.assertEqual([m["name"] for m in loaded], ["QwenImage"])

    def test_list_failure_gives_empty_and_type_only(self):
        class Mm:
            @property
            def current_loaded_models(self):
                raise AttributeError("/home/an/ComfyUI-h3/comfy/model_management.py")
        loaded, error = core.collect_loaded(Mm())
        self.assertEqual(loaded, [])
        self.assertEqual(error, "current_loaded_models: AttributeError")

    def test_cap(self):
        many = [Loaded(Patcher(model=QwenImage()), MIB, MIB) for _ in range(40)]
        loaded, _ = core.collect_loaded(mm_with(*many))
        self.assertEqual(len(loaded), core.MAX_LOADED)


class GpuTests(unittest.TestCase):
    def test_no_pynvml(self):
        self.assertEqual(core.collect_gpus(None), (None, "pynvml: ModuleNotFoundError"))

    def test_init_failure(self):
        nvml = FakeNvml(init_error=OSError("libnvidia-ml.so.1: cannot open"))
        self.assertEqual(core.collect_gpus(nvml), (None, "nvmlInit: OSError"))

    def test_fields(self):
        nvml = FakeNvml()
        gpus, error = core.collect_gpus(nvml)
        self.assertIsNone(error)
        self.assertEqual(gpus, [{
            "index": 0, "name": "NVIDIA GeForce RTX 3090", "utilPerc": 97,
            "memUsedMb": 21874, "memTotalMb": 24576, "tempC": 71,
            "powerW": 338, "powerLimitW": 350,
        }])
        self.assertEqual(nvml.shutdowns, 1)

    def test_unsupported_field_is_null(self):
        gpus, _ = core.collect_gpus(FakeNvml(power=None))
        self.assertIsNone(gpus[0]["powerW"])
        self.assertEqual(gpus[0]["memTotalMb"], 24576)


class ContractTests(unittest.TestCase):
    def test_keys_match_example(self):
        with open(EXAMPLE, encoding="utf-8") as f:
            example = json.load(f)
        body = core.build_response(
            mm_with(Loaded(Patcher(model=QwenImage()), MIB, MIB)), FakeNvml())
        self.assertEqual(set(body), set(example))
        self.assertEqual(body["version"], example["version"])
        self.assertEqual(set(body["loadedModels"][0]), set(example["loadedModels"][0]))
        self.assertEqual(set(body["gpus"][0]), set(example["gpus"][0]))

    def test_errors_only_on_failure(self):
        body = core.build_response(mm_with(), None)
        self.assertEqual(body["loadedModels"], [])
        self.assertNotIn("error", body)
        self.assertIsNone(body["gpus"])
        self.assertEqual(body["gpusError"], "pynvml: ModuleNotFoundError")


class RouteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.routes = {}

        class Routes:
            def get(self, path):
                def deco(fn):
                    cls.routes[path] = fn
                    return fn
                return deco

        server = types.ModuleType("server")
        server.PromptServer = types.SimpleNamespace(
            instance=types.SimpleNamespace(routes=Routes()))
        web = types.SimpleNamespace(
            json_response=lambda body, headers=None: {"body": body, "headers": headers})
        aiohttp = types.ModuleType("aiohttp")
        aiohttp.web = web
        sys.modules["server"] = server
        sys.modules["aiohttp"] = aiohttp
        sys.modules.pop("ccs_stats", None)
        import ccs_stats
        cls.pkg = ccs_stats

    def call(self):
        return asyncio.run(self.routes["/ccs/stats"](None))

    def test_registered_without_cache(self):
        resp = self.call()
        self.assertEqual(resp["headers"], {"Cache-Control": "no-store"})
        self.assertEqual(resp["body"]["version"], 1)
        self.assertEqual(self.pkg.NODE_CLASS_MAPPINGS, {})

    def test_comfy_import_failure_is_reported(self):
        # Вне ComfyUI модуля comfy нет: маршрут обязан ответить, а не упасть
        body = self.call()["body"]
        self.assertEqual(body["loadedModels"], [])
        self.assertEqual(body["error"], "current_loaded_models: ModuleNotFoundError")

    def test_build_failure_does_not_raise(self):
        original = self.pkg.build_response
        self.pkg.build_response = lambda mm, nvml: 1 / 0
        try:
            body = self.call()["body"]
        finally:
            self.pkg.build_response = original
        self.assertEqual(body, {"version": 1, "loadedModels": [], "gpus": None,
                                "error": "build: ZeroDivisionError"})


if __name__ == "__main__":
    unittest.main()
