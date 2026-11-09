"""Маршрут GET /ccs/stats: что загружено в память ComfyUI сейчас и карты узла.

Узлов графа пакет не добавляет — только маршрут на сервере ComfyUI (он же доступен как
/api/ccs/stats: ComfyUI дублирует маршруты с префиксом /api). Бэкенд берёт отсюда «В памяти»
и карту «RTX 3090 · медиа»: хостовый nvidia-smi карту узла не видит.

Только чтение: имена моделей без путей (LoRA пользователей скрыты как «lora»), размеры в МиБ,
pynvml по картам. Сборка ответа — core.build_response; маршрут не падает никогда: любой сбой
даёт пустой блок и короткую причину в error / gpusError. Формат — example-response.json рядом
с пакетом, версия формата — поле version.
"""
import logging

from aiohttp import web
from server import PromptServer

from .core import RESPONSE_VERSION, build_response

log = logging.getLogger("ccs_stats")

try:
    import pynvml as _nvml
except Exception:  # нет nvidia-ml-py в venv ComfyUI — карты будут null с причиной
    _nvml = None


def _model_management():
    import comfy.model_management as mm
    return mm


def stats_body():
    try:
        mm = _model_management()
    except Exception as e:
        mm = _Broken(e)
    try:
        return build_response(mm, _nvml)
    except Exception as e:
        log.warning("ccs_stats: сборка ответа упала: %s", type(e).__name__)
        return {"version": RESPONSE_VERSION, "loadedModels": [], "gpus": None,
                "error": f"build: {type(e).__name__}"}


class _Broken:
    """Заглушка model_management, если импорт не удался: collect_loaded вернёт ошибку."""

    def __init__(self, e):
        self._e = e

    @property
    def current_loaded_models(self):
        raise self._e


@PromptServer.instance.routes.get("/ccs/stats")
async def ccs_stats(request):
    return web.json_response(stats_body(), headers={"Cache-Control": "no-store"})


# Пустой словарь, а не отсутствие: без NODE_CLASS_MAPPINGS ComfyUI пишет в лог «Skip … module»
NODE_CLASS_MAPPINGS = {}
NODE_DISPLAY_NAME_MAPPINGS = {}
