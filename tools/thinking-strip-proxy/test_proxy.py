#!/usr/bin/env python3
# Тесты чистых функций прокси. Без сети и без поднятого vLLM:
#   python3 -m unittest discover -s tools/thinking-strip-proxy
import base64
import contextlib
import copy
import gzip
import http.client
import io
import json
import os
import shutil
import tempfile
import threading
import time
import unittest
import unittest.mock
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import diff_miss
import proxy

# Параметры прунинга в тестах задаём явно: дефолты живут в переменных окружения и
# меняются на стенде, а тест обязан проверять поведение, а не текущую настройку.
# Ручки границы — в ТОКЕНАХ, а рассуждает тест в выводах, поэтому переводим через тот же
# коэффициент, что и прокси: хвост 120k символов = 20 последних выводов, ступень 60k = 10
# выводов. Жёстко зашить числа в токенах нельзя — они поехали бы вместе с коэффициентом, и
# тесты границы начали бы проверять другую геометрию. Мерку саму по себе проверяет
# ОЦЕНОЧНЫЙ набор ниже, там числа зашиты намеренно.
def _в_токенах(символов, вверх=False):
    точно = символов / proxy.CHARS_PER_TOKEN_HISTORY
    return int(точно) + 1 if вверх and int(точно) != точно else int(точно)


BIG = "x" * 6000  # один «крупный» вывод


def tool_use(i):
    return {"role": "assistant", "content": [
        {"type": "tool_use", "id": f"t{i}", "name": "Bash", "input": {"command": f"echo {i}"}}]}


def tool_result(i, text=BIG):
    return {"role": "user", "content": [
        {"type": "tool_result", "tool_use_id": f"t{i}", "content": text}]}


def history(n_calls):
    """Диалог из n_calls пар «вызов инструмента → его вывод»."""
    msgs = [{"role": "user", "content": "задача"}]
    for i in range(n_calls):
        msgs += [tool_use(i), tool_result(i)]
    return msgs


def оценка(msgs, extra=0):
    """Та же мерка контекста, по которой прокси считает полосу."""
    return proxy.estimate_tokens(proxy._context_chars(msgs), extra, proxy._signature_chars(msgs))


# Гейт подбирается ПОД историю, а не наоборот: с 2026-09-23 цель обрезки задаётся полосой
# сырого контекста, поэтому «сколько обрежем» зависит от того, где история стоит относительно
# гейта. `history(120)` ставим на 5000 токенов выше гейта — начало первой полосы с запасом:
# запас меньше ступени (иначе тест стабильности внутри полосы стал бы тестом перехода) и
# больше одного вывода (иначе замена вывода на короткий уводила бы историю под гейт, и тесты
# «мелкие выводы не трогаем» зеленели бы вырожденно, вообще не доходя до границы).
ЗАПАС_НАД_ГЕЙТОМ = 5000
P = dict(keep_tail_tokens=_в_токенах(20 * len(BIG)),
         step_tokens=_в_токенах(10 * len(BIG), вверх=True),
         min_chars=2000, min_tokens=_в_токенах(30000),
         min_context_tokens=оценка(history(120)) - ЗАПАС_НАД_ГЕЙТОМ)


class СэмплингTests(unittest.TestCase):
    def test_перебивает_температуру_cli_и_не_трогает_остальное(self):
        with unittest.mock.patch.object(proxy, "SAMPLING", {"temperature": 0.7, "top_p": 0.8, "top_k": 20}):
            doc = {"model": "m", "temperature": 1, "messages": []}
            self.assertTrue(proxy.apply_sampling(doc))
            self.assertEqual(doc, {"model": "m", "temperature": 0.7, "top_p": 0.8, "top_k": 20, "messages": []})
            self.assertFalse(proxy.apply_sampling(doc))

    def test_пустой_sampling_ничего_не_меняет(self):
        with unittest.mock.patch.object(proxy, "SAMPLING", {}):
            doc = {"temperature": 1}
            self.assertFalse(proxy.apply_sampling(doc))
            self.assertEqual(doc, {"temperature": 1})


class ГибридTests(unittest.TestCase):
    def _doc(self, last_content, output_config=True):
        doc = {"model": "m", "messages": [{"role": "user", "content": "задача"},
                                          {"role": "assistant", "content": [{"type": "tool_use", "id": "t1", "name": "Read", "input": {}}]},
                                          {"role": "user", "content": last_content}]}
        if output_config:
            doc["output_config"] = {"effort": "low"}
        return doc

    def test_решение_по_последнему_сообщению(self):
        self.assertEqual(proxy.thinking_decision({"messages": [{"role": "user", "content": "привет"}]}), "think")
        self.assertEqual(proxy.thinking_decision(self._doc([{"type": "tool_result", "tool_use_id": "t1", "content": "ok"}])), "fast")
        self.assertEqual(proxy.thinking_decision(self._doc([{"type": "tool_result", "tool_use_id": "t1", "content": "boom", "is_error": True},
                                                            {"type": "tool_result", "tool_use_id": "t2", "content": "ok"}])), "think")
        self.assertEqual(proxy.thinking_decision(self._doc([{"type": "text", "text": "а теперь другое"}])), "think")
        self.assertEqual(proxy.thinking_decision({"messages": []}), "think")
        # служебные system-сообщения CLI в хвосте не считаются ходом
        d = self._doc([{"type": "tool_result", "tool_use_id": "t1", "content": "ok"}])
        d["messages"].append({"role": "system", "content": "<total_tokens>1000 tokens left</total_tokens>"})
        d["messages"].append({"role": "system", "content": [{"type": "text", "text": "# Environment\n…"}]})
        self.assertEqual(proxy.thinking_decision(d), "fast")
        d["messages"][-3]["content"][0]["is_error"] = True
        self.assertEqual(proxy.thinking_decision(d), "think")
        self.assertEqual(proxy.thinking_decision({"messages": [{"role": "assistant", "content": "x"}]}), "think")

    def test_v2_быстро_только_после_инструментов_чтения(self):
        d = self._doc([{"type": "tool_result", "tool_use_id": "t1", "content": "ok"}])
        self.assertEqual(proxy.thinking_decision(d), "fast")  # t1 — Read
        d["messages"][1]["content"][0]["name"] = "Write"
        self.assertEqual(proxy.thinking_decision(d), "think")
        d["messages"][1]["content"] = [{"type": "tool_use", "id": "t1", "name": "Grep", "input": {}},
                                       {"type": "tool_use", "id": "t2", "name": "Bash", "input": {}}]
        d["messages"][2]["content"] = [{"type": "tool_result", "tool_use_id": "t1", "content": "a"},
                                       {"type": "tool_result", "tool_use_id": "t2", "content": "b"}]
        self.assertEqual(proxy.thinking_decision(d), "think")  # смешанный пакет — думать
        d["messages"][2]["content"] = [{"type": "tool_result", "tool_use_id": "t9", "content": "a"}]
        self.assertEqual(proxy.thinking_decision(d), "think")  # неизвестный id — думать

    def test_hybrid_быстрый_шаг_режет_output_config_и_ставит_presence(self):
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "hybrid"), \
             unittest.mock.patch.object(proxy, "SAMPLING_FAST", {"presence_penalty": 1.5}):
            doc = self._doc([{"type": "tool_result", "tool_use_id": "t1", "content": "ok"}])
            self.assertEqual(proxy.apply_thinking_mode(doc), (True, "fast"))
            self.assertNotIn("output_config", doc)
            self.assertEqual(doc["presence_penalty"], 1.5)
            self.assertNotIn("thinking_token_budget", doc)

    def test_hybrid_шаг_планирования_оставляет_размышления_с_бюджетом(self):
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "hybrid"), \
             unittest.mock.patch.object(proxy, "THINKING_BUDGET", 1024), \
             unittest.mock.patch.object(proxy, "SAMPLING_FAST", {"presence_penalty": 1.5}):
            doc = self._doc([{"type": "text", "text": "вопрос"}])
            self.assertEqual(proxy.apply_thinking_mode(doc), (True, "think"))
            self.assertEqual(doc["output_config"], {"effort": "low"})
            self.assertEqual(doc["thinking_token_budget"], 1024)
            self.assertNotIn("presence_penalty", doc)

    def test_прежние_режимы_не_изменились(self):
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "off"), unittest.mock.patch.object(proxy, "SAMPLING_FAST", {}):
            doc = self._doc([{"type": "text", "text": "x"}])
            self.assertEqual(proxy.apply_thinking_mode(doc), (True, None)); self.assertNotIn("output_config", doc)
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "budget"), unittest.mock.patch.object(proxy, "THINKING_BUDGET", 1024):
            doc = self._doc([{"type": "text", "text": "x"}])
            self.assertEqual(proxy.apply_thinking_mode(doc), (True, None)); self.assertEqual(doc["thinking_token_budget"], 1024); self.assertIn("output_config", doc)
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "on"):
            doc = self._doc([{"type": "text", "text": "x"}])
            self.assertEqual(proxy.apply_thinking_mode(doc), (False, None)); self.assertIn("output_config", doc)

    def test_подмена_усилия_только_при_размышлениях(self):
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "budget"), unittest.mock.patch.object(proxy, "EFFORT_OVERRIDE", "medium"):
            doc = self._doc([{"type": "text", "text": "x"}])
            proxy.apply_thinking_mode(doc)
            self.assertEqual(doc["output_config"]["effort"], "medium")
        with unittest.mock.patch.object(proxy, "THINKING_MODE", "off"), unittest.mock.patch.object(proxy, "EFFORT_OVERRIDE", "medium"):
            doc = self._doc([{"type": "text", "text": "x"}])
            proxy.apply_thinking_mode(doc)
            self.assertNotIn("output_config", doc)

    def test_снимок_префикса_только_первый_ход_и_обрезка_трейлера(self):
        doc = {"model": "m", "tools": [{"name": "Read"}],
               "system": [{"type": "text", "text": "карта\n"}, {"type": "text", "text": "правила\nCCS-Session: abc\nCCS-Task: t1\nхвост"}],
               "messages": [{"role": "user", "content": "задача"}]}
        body = proxy.prefix_snapshot_body(doc)
        self.assertEqual([b["text"] for b in body["system"]], ["карта\n", "правила\n"])
        self.assertEqual(body["tools"], [{"name": "Read"}])
        doc2 = dict(doc, messages=doc["messages"] + [{"role": "assistant", "content": "ок"}, {"role": "user", "content": "ещё"}])
        self.assertIsNone(proxy.prefix_snapshot_body(doc2))
        doc4 = dict(doc, messages=doc["messages"] + [{"role": "system", "content": "<total_tokens>1 tokens left</total_tokens>"}])
        self.assertIsNotNone(proxy.prefix_snapshot_body(doc4))  # хвостовое system-сообщение CLI — не ход
        doc3 = dict(doc, system="строка без трейлера")
        self.assertEqual(proxy.prefix_snapshot_body(doc3)["system"], [{"type": "text", "text": "строка без трейлера"}])
        self.assertEqual(proxy.prefix_signature(body), proxy.prefix_signature(proxy.prefix_snapshot_body(dict(doc, messages=[{"role": "user", "content": "другая задача"}]))))

    def test_снимок_префикса_пишется_в_каталог(self):
        with tempfile.TemporaryDirectory() as d, unittest.mock.patch.object(proxy, "PREFIX_SNAPSHOT_DIR", d), \
             unittest.mock.patch.object(proxy, "_prefix_snapshot_written", {}):
            doc = {"model": "m", "tools": [{"name": "Read"}], "system": "карта", "messages": [{"role": "user", "content": "задача"}]}
            self.assertTrue(proxy.save_prefix_snapshot(doc))
            files = os.listdir(d); self.assertEqual(len(files), 1)
            saved = json.load(open(os.path.join(d, files[0]), encoding="utf-8"))
            self.assertEqual(saved["system"], [{"type": "text", "text": "карта"}]); self.assertNotIn("messages", saved)
            self.assertFalse(proxy.save_prefix_snapshot(doc))  # повтор той же подписи раньше интервала — не пишем

    def test_разбор_kv(self):
        self.assertEqual(proxy._parse_kv("temperature=0.7,top_k=20,presence_penalty=1.5"), {"temperature": 0.7, "top_k": 20, "presence_penalty": 1.5})
        self.assertEqual(proxy._parse_kv(""), {})


class PruneTests(unittest.TestCase):
    def test_детерминизм(self):
        msgs = history(120)
        first = proxy.prune_tool_results(copy.deepcopy(msgs), **P)
        second = proxy.prune_tool_results(copy.deepcopy(msgs), **P)
        self.assertEqual(first, second)
        self.assertGreater(first[2], 0, "на такой истории прунинг обязан сработать")

    def test_граница_не_съезжает_при_дописывании_хвоста(self):
        """Главный тест: запросы, отличающиеся только хвостом, дают одинаковый префикс.

        История дописывается в конец, поэтому history(120) — ровно префикс history(120+n).
        Сравниваем весь общий префикс: если граница поехала хоть на один вывод, обрезанным
        окажется сообщение ВНУТРИ него, и для vLLM это обрыв кэша на всю оставшуюся историю.

        Семь дописанных вызовов — это ещё ПЕРВАЯ полоса контекста (запас над гейтом 5000
        токенов плюс 7 × ~1825 меньше ступени 18182); восьмой уводит в следующую, и там
        граница обязана сдвинуться — это соседний тест.
        """
        base = history(120)
        pruned_base, _, blocks = proxy.prune_tool_results(base, **P)
        self.assertGreater(blocks, 0, "иначе тест сравнивает две нетронутые истории и всегда зелён")
        for extra in range(1, 8):  # внутри одной полосы граница обязана стоять
            longer, _, ещё = proxy.prune_tool_results(history(120 + extra), **P)
            self.assertEqual(ещё, blocks, f"граница поехала после {extra} дописанных вызовов")
            self.assertEqual(longer[:len(base)], pruned_base,
                             f"префикс поехал после {extra} дописанных вызовов")

    def test_граница_двигается_квантом(self):
        """…и всё же двигается: полоса контекста сменилась — обрезанных блоков ровно на квант больше."""
        _, _, before = proxy.prune_tool_results(history(120), **P)
        _, _, after = proxy.prune_tool_results(history(130), **P)
        self.assertEqual(after - before, 10)

    def test_последние_выводы_не_тронуты(self):
        msgs = history(120)
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        for m in pruned[-2 * 20:]:
            for b in m["content"]:
                if b.get("type") == "tool_result":
                    self.assertEqual(b["content"], BIG)

    def test_tool_use_и_реплики_человека_не_тронуты(self):
        msgs = history(120)
        msgs.insert(1, {"role": "user", "content": "длинная реплика человека " + BIG})
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[1], msgs[1])
        for a, b in zip(msgs, pruned):
            if a.get("role") == "assistant":
                self.assertEqual(a, b)

    def test_вход_не_мутирует(self):
        msgs = history(120)
        before = copy.deepcopy(msgs)
        proxy.prune_tool_results(msgs, **P)
        self.assertEqual(msgs, before)

    def test_мелкие_выводы_не_трогаем(self):
        msgs = history(120)
        msgs[2]["content"][0]["content"] = "ok"  # вывод первого вызова — короткий
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[2]["content"][0]["content"], "ok")

    def test_порог_выгоды(self):
        """Освобождается меньше порога — не трогаем историю вовсе."""
        msgs = history(120)
        out, freed, blocks = proxy.prune_tool_results(msgs, min_tokens=10 ** 9,
                                                      **{k: v for k, v in P.items() if k != "min_tokens"})
        self.assertIs(out, msgs)
        self.assertEqual((freed, blocks), (0, 0))

    def test_короткая_история_не_трогается(self):
        msgs = history(25)  # 25 вызовов при защите последних 20 — квант ещё не набрался
        out, freed, blocks = proxy.prune_tool_results(msgs, **P)
        self.assertIs(out, msgs)
        self.assertEqual((freed, blocks), (0, 0))

    def test_идемпотентность(self):
        """Повторный прогон уже обрезанной истории ничего не меняет."""
        once, _, _ = proxy.prune_tool_results(history(120), **P)
        twice, freed, blocks = proxy.prune_tool_results(once, **P)
        self.assertEqual(twice, once)
        self.assertEqual((freed, blocks), (0, 0))

    def test_форма_content_сохраняется(self):
        msgs = history(120)
        msgs[2]["content"][0]["content"] = [{"type": "text", "text": BIG}]
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[2]["content"][0]["content"],
                         [{"type": "text", "text": proxy.PRUNE_PLACEHOLDER}])
        self.assertEqual(pruned[4]["content"][0]["content"], proxy.PRUNE_PLACEHOLDER)

    def test_блок_меняется_только_в_content(self):
        """Потеря tool_use_id рвёт парность с tool_use — история станет невалидной."""
        msgs = history(120)
        msgs[2]["content"][0]["is_error"] = True
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[2]["content"][0], {
            "type": "tool_result", "tool_use_id": "t0", "is_error": True,
            "content": proxy.PRUNE_PLACEHOLDER})

    def test_картинки_не_трогаем(self):
        msgs = history(120)
        image = [{"type": "image", "source": {"type": "base64", "data": "A" * 9000}}]
        msgs[2]["content"][0]["content"] = image
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[2]["content"][0]["content"], image)

    def test_маленький_чат_не_трогаем_даже_при_многих_вызовах(self):
        """Условие «чат уже большой» не зависит от числа вызовов — это отдельная ручка."""
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(120):  # вызовов много, но выводы скромные: контекст ~91k токенов
            msgs += [tool_use(i), tool_result(i, "y" * 2500)]
        out, freed, blocks = proxy.prune_tool_results(
            msgs, **dict(P, min_context_tokens=150000))
        self.assertIs(out, msgs)
        self.assertEqual((freed, blocks), (0, 0))

    def test_системная_часть_идёт_в_зачёт_контекста(self):
        """Системный промпт и тулсет весят как половина контекста — без них мерка врёт."""
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(120):
            msgs += [tool_use(i), tool_result(i, "y" * 2500)]  # сама история ~91k токенов
        big = dict(P, min_context_tokens=150000)
        _, _, without = proxy.prune_tool_results(msgs, **big)
        _, _, with_system = proxy.prune_tool_results(msgs, extra_chars=400_000, **big)
        self.assertEqual(without, 0, "без системной части чат не дотягивает до порога")
        self.assertGreater(with_system, 0, "с ней — дотягивает")

    def test_колебание_системной_части_не_мигает_решением(self):
        """Системный блок пересобирается каждый ход; его дрожь не должна включать и выключать
        прунинг — это мигание середины истории, то есть потеря кэша на каждом ходу."""
        msgs = [{"role": "user", "content": "з"}]
        for i in range(120):
            msgs += [tool_use(i), tool_result(i, "y" * 4050)]  # история чуть ниже порога
        big = dict(P, min_context_tokens=150000)
        толще = proxy.prune_tool_results(msgs, extra_chars=15_000, **big)[2]
        тоньше = proxy.prune_tool_results(msgs, extra_chars=5_000, **big)[2]
        self.assertEqual(толще, тоньше, "решение поехало от дрожи системного блока")

    def test_большой_чат_трогаем(self):
        msgs = history(120)  # те же 120 вызовов, но выводы крупные: контекст ~218k токенов
        _, freed, blocks = proxy.prune_tool_results(msgs, **dict(P, min_context_tokens=150000))
        self.assertGreater(blocks, 0)
        self.assertGreater(freed, 0)

    def test_тело_write_режется_а_путь_остаётся(self):
        """На задачах правки кода тела Write/Edit — до 47 % контекста. Путь файла нужен модели,
        чтобы помнить, ЧТО она писала; тело — нет, файл на диске."""
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(120):  # выводы мелкие, вся масса — в аргументах Write
            msgs += [{"role": "assistant", "content": [{"type": "tool_use", "id": f"t{i}", "name": "Write",
                                                        "input": {"file_path": f"/x/{i}.cs", "content": BIG}}]},
                     tool_result(i, "ok")]
        _, _, без_флага = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(без_флага, 0, "без PRUNE_INPUTS тела Write не трогаем: модель подражает плейсхолдеру")
        pruned, freed, blocks = proxy.prune_tool_results(msgs, prune_inputs=True, **P)
        self.assertGreater(blocks, 0)
        первый = pruned[1]["content"][0]
        self.assertEqual(первый["input"]["file_path"], "/x/0.cs")
        self.assertEqual(первый["input"]["content"], proxy.PRUNE_INPUT_PLACEHOLDER)
        self.assertEqual(первый["id"], "t0", "парность с tool_result держится на id")
        последний = pruned[-2]["content"][0]
        self.assertEqual(последний["input"]["content"], BIG, "свежий хвост не тронут")

    def test_edit_режет_обе_строки_multiedit_вложенные(self):
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(120):
            msgs += [{"role": "assistant", "content": [{"type": "tool_use", "id": f"t{i}", "name": "MultiEdit",
                                                        "input": {"file_path": "/x.cs",
                                                                  "edits": [{"old_string": BIG, "new_string": BIG},
                                                                            {"old_string": "a", "new_string": "b"}]}}]},
                     tool_result(i, "ok")]
        pruned, _, blocks = proxy.prune_tool_results(msgs, prune_inputs=True, **P)
        self.assertGreater(blocks, 0)
        правки = pruned[1]["content"][0]["input"]["edits"]
        self.assertEqual(правки[0], {"old_string": proxy.PRUNE_INPUT_PLACEHOLDER,
                                     "new_string": proxy.PRUNE_INPUT_PLACEHOLDER})
        self.assertEqual(правки[1], {"old_string": "a", "new_string": "b"}, "короткие поля не трогаем")

    def test_thinking_режется_только_по_флагу(self):
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(120):
            msgs += [{"role": "assistant", "content": [{"type": "thinking", "thinking": BIG, "signature": "sig"},
                                                        {"type": "tool_use", "id": f"t{i}", "name": "Bash",
                                                         "input": {"command": "ls"}}]},
                     tool_result(i, "ok")]
        выкл, _, blocks_off = proxy.prune_tool_results(msgs, prune_thinking=False, **P)
        self.assertEqual(blocks_off, 0, "без флага размышления не трогаем")
        вкл, _, blocks_on = proxy.prune_tool_results(msgs, prune_thinking=True, **P)
        self.assertGreater(blocks_on, 0)
        первый = вкл[1]["content"][0]
        self.assertEqual(первый, {"type": "thinking", "thinking": proxy.PRUNE_THINKING_PLACEHOLDER},
                         "текст в плейсхолдер, подпись снята — она к нему уже не относится")
        self.assertEqual(вкл[1]["content"][1]["input"], {"command": "ls"}, "соседний tool_use цел")

    def test_разные_виды_идут_в_одну_очередь_по_порядку(self):
        """Ступень и хвост считаются по общему объёму всех видов, а не по каждому отдельно."""
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(60):  # 60 Write по 1500 ток + 60 выводов по 1500 ток = 180k
            msgs += [{"role": "assistant", "content": [{"type": "tool_use", "id": f"t{i}", "name": "Write",
                                                        "input": {"file_path": "/x", "content": BIG}}]},
                     tool_result(i)]
        base, _, blocks = proxy.prune_tool_results(msgs, prune_inputs=True, **P)
        self.assertGreater(blocks, 0)
        for extra in range(1, 4):  # дописанная ПАРА тяжелее одиночного вывода: три — ещё та же полоса
            msgs2 = list(msgs)
            for j in range(extra):
                msgs2 += [{"role": "assistant", "content": [{"type": "tool_use", "id": f"e{j}", "name": "Write",
                                                             "input": {"file_path": "/y", "content": BIG}}]},
                          tool_result(100 + j)]
            longer, _, _ = proxy.prune_tool_results(msgs2, prune_inputs=True, **P)
            self.assertEqual(longer[:len(msgs)], base, f"префикс поехал после {extra} дописанных пар")

    def test_незнакомая_структура_проходит_насквозь(self):
        for msgs in ([], [{"role": "user"}], [{"role": "user", "content": None}],
                     [{"role": "user", "content": [{"type": "tool_result"}]}] * 200,
                     ["строка вместо словаря"] * 200,
                     [{"role": "user", "content": [None, 42]}] * 200):
            out, freed, blocks = proxy.prune_tool_results(msgs, **P)
            self.assertIs(out, msgs)
            self.assertEqual((freed, blocks), (0, 0))

    def test_мусор_в_полях_не_роняет_прунинг(self):
        """Тело чужое: число вместо текста не должно кончаться исключением.

        Исключение отсюда уходит в обработчик запроса, клиент остаётся без ответа и виснет —
        то есть опечатка в разборе чужого тела ломает ход пользователя молча.
        """
        msgs = history(120)
        msgs[2]["content"][0]["content"] = [{"type": "text", "text": 5}]
        msgs.insert(1, {"role": "assistant", "content": [{"type": "text", "text": 42}]})
        msgs.insert(1, {"role": "assistant", "content": [{"type": "thinking", "thinking": None}]})
        proxy.prune_tool_results(msgs, **P)  # падение = провал теста
        proxy._context_chars(msgs)
        proxy._signature_chars(msgs)

    def test_нулевая_ступень_не_делит_на_ноль(self):
        proxy.prune_tool_results(history(120), **dict(P, step_tokens=0))


class ПолосыКонтекстаTests(unittest.TestCase):
    """Цель обрезки задаётся полосой СЫРОГО КОНТЕКСТА, а не накопленным обрезаемым объёмом.

    Зачем так (2026-09-23): обрезаемый объём растёт своим темпом, и пока порог первого
    срабатывания равнялся ступени, чат успевал доехать до автосжатия CLI (232k) раньше, чем
    набиралась ступень — живой замер меры 4 кончился ровно этим. Теперь шкала одна: сколько
    ступеней контекста чат прошёл сверх гейта, столько ступеней и режем.
    """

    def test_ниже_гейта_не_режем_вовсе(self):
        msgs = history(120)
        высокий = dict(P, min_context_tokens=оценка(msgs) + 1)
        out, freed, blocks = proxy.prune_tool_results(msgs, **высокий)
        self.assertIs(out, msgs, "историю не трогали вовсе — вернули тот же объект")
        self.assertEqual((freed, blocks), (0, 0))

    def test_на_гейте_режем_ровно_ступень(self):
        """Первая граница — сразу полная ступень: отдельной «первой ступени» больше нет."""
        _, _, blocks = proxy.prune_tool_results(history(120), **P)
        self.assertEqual(blocks, 10, "ступень = 10 выводов")

    def test_каждая_ступень_контекста_добавляет_ступень_обрезки(self):
        for вызовов, ожидаем in ((120, 10), (130, 20), (140, 30)):
            with self.subTest(вызовов):
                _, _, blocks = proxy.prune_tool_results(history(вызовов), **P)
                self.assertEqual(blocks, ожидаем)

    EXTRA = 600_000  # системная часть, в разы тяжелее истории: контекст уходит в третью полосу

    def дефицит(self):
        """История, у которой цель обрезки заведомо больше всего обрезаемого вне хвоста.

        45 вызовов: вне хвоста 25 выводов (150k символов), а цель третьей полосы — 30 (180k).
        Гейт подобран так, чтобы история стояла в начале третьей полосы.
        """
        msgs = history(45)
        гейт = оценка(msgs, self.EXTRA) - ЗАПАС_НАД_ГЕЙТОМ - 2 * P["step_tokens"]
        return msgs, dict(P, min_context_tokens=гейт)

    def test_недостижимая_цель_не_тащит_границу_за_хвостом(self):
        """Регрессия: цель больше всего обрезаемого — граница обязана стоять, а не ползти.

        Так бывает у чата с жирной системной частью: по контексту он уже в третьей полосе, а
        история короче цели. Наивное «тогда режем всё обрезаемое вне хвоста» ставит границу на
        НАЧАЛО защищённого хвоста, а он съезжает с каждым новым выводом — граница ползёт
        каждый ход, и это ровно потеря кэша (симуляция: 52–67 сдвигов вместо 3).
        """
        msgs, ручки = self.дефицит()
        base, _, blocks = proxy.prune_tool_results(msgs, extra_chars=self.EXTRA, **ручки)
        вне_хвоста = 45 - 20
        self.assertGreater(blocks, 0, "обрезать всё же есть что")
        self.assertLess(blocks, вне_хвоста, "иначе это и есть «режем всё вне хвоста» — тест слеп")
        for дописано in range(1, 4):
            longer, _, ещё = proxy.prune_tool_results(history(45 + дописано),
                                                      extra_chars=self.EXTRA, **ручки)
            self.assertEqual(ещё, blocks, f"граница поехала после {дописано} дописанных вызовов")
            self.assertEqual(longer[:len(msgs)], base)

    def test_дефицит_режет_долю_цели_а_не_сколько_придётся(self):
        """Обрезанное — ровно доля ступени, а не «сколько нашлось вне хвоста».

        Полоса 3 при ступени в 10 выводов = цель 30 выводов, обрезаемого вне хвоста 25.
        При трёх долях наибольшая достижимая — две трети цели, то есть ровно 20 выводов.
        """
        msgs, ручки = self.дефицит()
        _, freed, blocks = proxy.prune_tool_results(msgs, extra_chars=self.EXTRA, **ручки)
        self.assertEqual(proxy.PRUNE_DEFICIT_PARTS, 3, "арифметика теста считана с трёх долей")
        self.assertEqual(blocks, 20)
        self.assertGreater(freed, 0)

    def test_идемпотентность_в_полосе(self):
        once, _, _ = proxy.prune_tool_results(history(120), **P)
        twice, freed, blocks = proxy.prune_tool_results(once, **P)
        self.assertEqual(twice, once)
        self.assertEqual((freed, blocks), (0, 0))


# Реальные тела запросов локальной модели, снятые 2026-09-22 (протокол — в README):
# символы частей и ФАКТ по токенизатору стенда `POST /tokenize`. Числа зашиты намеренно —
# это и есть предмет проверки: вернёшь единый делитель 4, и набор покраснеет.
ЗАМЕРЫ = [
    # имя,        история симв, системная симв, подписи симв, факт истории, факт системной,
    #                                           факт композита, история локальной модели
    ("12b4d619", 347051, 82769, 1568, 109015, 21898, 133352, True),
    ("68c21f3a", 461896, 75229, 192, 137425, 19662, 157756, True),
    ("2e482096", 399118, 75229, 160, 119365, 19662, 139640, True),
    ("12b4d619+MCP", 347109, 295111, 1568, 109030, 81986, 196451, True),
    # Чат, переехавший к нам с чужой модели: 689k символов base64-подписей thinking, а сам
    # текст плотнее нашего (2.85 симв/ток против 3.18–3.36). Средняя оценка на нём занижает —
    # это ровно тот случай, ради которого заведена консервативная, поэтому в проверках
    # средней он не участвует, а в проверке потолка участвует обязательно.
    ("035c3dd7", 905062, 0, 688752, 317493, 0, 823309, False),
]


class МеркаTests(unittest.TestCase):
    """Мерка контекста на реальных телах: два слагаемых, у каждого свой коэффициент."""

    def test_история_и_системная_часть_меряются_по_разному(self):
        """Один делитель на оба слагаемых — это и была прежняя ошибка."""
        одинаково_символов = 100_000
        self.assertNotEqual(proxy.estimate_tokens(одинаково_символов, 0),
                            proxy.estimate_tokens(0, одинаково_символов))

    def test_оценка_истории_сходится_с_токенизатором(self):
        for имя, история, _, _, факт, _, _, свой in ЗАМЕРЫ:
            if not свой:
                continue
            with self.subTest(имя):
                оценка = proxy.estimate_tokens(история)
                отклонение = abs(оценка - факт) / факт
                self.assertLess(отклонение, 0.10,
                                f"{имя}: {оценка} против факта {факт} ({отклонение:.1%})")

    def test_оценка_системной_части_сходится_с_токенизатором(self):
        for имя, _, системная, _, _, факт, _, свой in ЗАМЕРЫ:
            if not системная or not свой:
                continue
            with self.subTest(имя):
                оценка = proxy.estimate_tokens(0, системная)
                отклонение = abs(оценка - факт) / факт
                self.assertLess(отклонение, 0.06,
                                f"{имя}: {оценка} против факта {факт} ({отклонение:.1%})")

    def test_композит_не_хуже_прежней_сверки(self):
        """Точность композита (±4 % в старой сверке README) разведение не должно ухудшить."""
        for имя, история, системная, подписи, _, _, факт, свой in ЗАМЕРЫ:
            if not свой:
                continue
            with self.subTest(имя):
                оценка = proxy.estimate_tokens(история, системная, подписи)
                отклонение = abs(оценка - факт) / факт
                self.assertLess(отклонение, 0.08,
                                f"{имя}: {оценка} против факта {факт} ({отклонение:.1%})")

    def test_консервативная_оценка_нигде_не_ниже_факта(self):
        """Ею мера 2 отвечает на «влезет ли запрос в окно»: занижение = HTTP 400 и упавший ход."""
        for имя, история, системная, подписи, _, _, факт, _ in ЗАМЕРЫ:
            with self.subTest(имя):
                потолок = proxy.estimate_tokens(история, системная, подписи, conservative=True)
                self.assertGreater(потолок, факт, f"{имя}: потолок {потолок} ниже факта {факт}")

    def test_подписи_thinking_считаются_отдельно(self):
        """base64 токенизируется втрое плотнее текста — общим коэффициентом его мерить нельзя."""
        self.assertGreater(proxy.estimate_tokens(0, 0, 100_000),
                           2 * proxy.estimate_tokens(100_000))

    def test_геометрия_хвоста_и_ступени_в_символах_не_поехала(self):
        """Дефолты ручек пересчитаны так, чтобы защищённый хвост и ступень остались на месте.

        Тюнинг ступени снимался живым прогоном в СИМВОЛАХ (160k хвост, 400k ступень), и смена
        коэффициента не имеет права двигать его молча.
        """
        хвост = proxy.PRUNE_KEEP_TAIL_TOKENS * proxy.CHARS_PER_TOKEN_HISTORY
        ступень = proxy.PRUNE_STEP_TOKENS * proxy.CHARS_PER_TOKEN_HISTORY
        выгода = proxy.PRUNE_MIN_TOKENS * proxy.CHARS_PER_TOKEN_HISTORY
        self.assertAlmostEqual(хвост, 160_000, delta=160_000 * 0.02)
        self.assertAlmostEqual(ступень, 400_000, delta=400_000 * 0.02)
        self.assertAlmostEqual(выгода, 80_000, delta=80_000 * 0.02)

    def test_гейт_оставляет_запас_до_автосжатия_CLI(self):
        """Неравенство, которым выбран гейт: пока прунинг молчит, usage не доедет до порога
        автосжатия CLI — порога, а не базы окна 232k: на этом гейт 190k и промахнулся 15.10
        (лимит вывода 20000 опустил порог до 199k, за 1116 запросов ни одной обрезки).

        Гейт растягивают два недосчёта — средней оценки (до 5.3 %, «Мерка контекста») и
        огрубления системной части, оба включают прунинг ПОЗЖЕ, — а сверху ложится один большой
        `Read` (25k), который придёт следующим ходом. Сменишь лимит вывода, окно или огрубление,
        не пересчитав гейт, — тест покраснеет.
        """
        база, лимит_вывода, запас_CLI = 232_000, 20_000, 13_000
        порог = база - min(лимит_вывода, 20_000) - запас_CLI
        недосчёт_оценки = 1.05
        недосчёт_огрубления = proxy.SYSTEM_ROUNDING_CHARS / proxy.CHARS_PER_TOKEN_SYSTEM
        большой_read = 25_000
        self.assertLess(
            proxy.PRUNE_MIN_CONTEXT_TOKENS * недосчёт_оценки + недосчёт_огрубления + большой_read,
            порог)
        # …и гейт всё же не занижен до бессмыслицы: чат на 133k живёт без сжатия, резать там нечего
        self.assertGreater(proxy.PRUNE_MIN_CONTEXT_TOKENS, 133_000)

    def test_порог_распознавания_сжатия_не_привязан_к_гейту(self):
        """Гейт прунинга двигали трижды; утащив за собой порог распознавания сжатия, он молча
        отправил бы запросы автосжатия на локаль — те самые 330–430 с стоп-мира."""
        self.assertEqual(proxy.COMPACT_MIN_CONTEXT_TOKENS, 150_000)


class КомпактРаспознаваниеTests(unittest.TestCase):
    """Запрос автосжатия отличается от обычного хода по ДВУМ признакам сразу."""

    def тело(self, msgs, **поля):
        return dict({"model": "qwen3.8-27b", "max_tokens": 8192, "messages": msgs}, **поля)

    def большая_история(self):
        """~220k токенов: столько набирает чат к моменту автосжатия."""
        return history(120)

    def test_запрос_сжатия_распознан(self):
        msgs = self.большая_история()
        msgs.append({"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize the conversation."})
        self.assertTrue(proxy.is_compact_request(self.тело(msgs)))

    def test_обычный_ход_той_же_длины_не_распознан(self):
        """Первая сторона ошибки: большой чат сам по себе сжатием не является."""
        msgs = self.большая_история()
        msgs.append({"role": "user", "content": "продолжай, проверь сборку"})
        self.assertFalse(proxy.is_compact_request(self.тело(msgs)))

    def test_сигнатура_в_маленьком_чате_не_распознана(self):
        """Вторая сторона ошибки: строку можно процитировать, и тогда ход уехал бы в облако,
        вернулся текстом без tool_use — агентная петля сбилась бы."""
        msgs = [{"role": "user", "content": proxy.COMPACT_SIGNATURE + " (цитата из документации)"}]
        self.assertFalse(proxy.is_compact_request(self.тело(msgs)))

    def test_сигнатура_посреди_реплики_не_распознана(self):
        msgs = self.большая_история()
        msgs.append({"role": "user", "content": "в логе встретилось: " + proxy.COMPACT_SIGNATURE})
        self.assertFalse(proxy.is_compact_request(self.тело(msgs)))

    def test_сигнатура_в_ответе_ассистента_не_считается(self):
        """Смотрим хвост ПОЛЬЗОВАТЕЛЬСКОГО сообщения: свой же текст модели признаком не является."""
        msgs = self.большая_история()
        msgs.append({"role": "assistant", "content": [{"type": "text", "text": proxy.COMPACT_SIGNATURE}]})
        self.assertFalse(proxy.is_compact_request(self.тело(msgs)))

    def test_сигнатура_блоками_и_системная_часть_в_зачёт(self):
        """Хвост склеивается из текстовых блоков, а системная часть идёт в оценку контекста."""
        msgs = [{"role": "user", "content": "задача"}]
        for i in range(60):  # история ~110k токенов — одной её на порог не хватает
            msgs += [tool_use(i), tool_result(i)]
        msgs.append({"role": "user", "content": [
            {"type": "text", "text": proxy.COMPACT_SIGNATURE + " Summarize."},
            {"type": "text", "text": "хвостовой блок"}]})
        self.assertFalse(proxy.is_compact_request(self.тело(msgs)))
        с_тулсетом = self.тело(msgs, tools=[{"name": "Bash", "description": "d" * 300_000}])
        self.assertTrue(proxy.is_compact_request(с_тулсетом))

    def test_незнакомое_тело_не_распознаётся(self):
        for doc in ({}, {"messages": []}, {"messages": "строка"},
                    {"messages": [{"role": "user", "content": None}]}):
            self.assertFalse(proxy.is_compact_request(doc))

    def test_сигнатура_с_малым_телом_это_отдельный_вердикт(self):
        """Сигнатура пришла, а тело до порога не доросло — диагноз настройки, а не «не сжатие».

        Так выглядит слишком низкий `CLAUDE_CODE_AUTO_COMPACT_WINDOW`: CLI уже затеял сжатие,
        но история меньше порога распознавания. Отличать этот исход от обычного хода
        обязательно — иначе сбой невиден по счётчикам.
        """
        msgs = [{"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."}]
        вердикт, оценка = proxy.compact_verdict(self.тело(msgs))
        self.assertEqual(вердикт, "signature_small")
        self.assertLess(оценка, proxy.COMPACT_MIN_CONTEXT_TOKENS)
        # Обычный ход того же размера в этот исход не попадает — только «не сжатие».
        обычный = [{"role": "user", "content": "проверь сборку"}]
        self.assertEqual(proxy.compact_verdict(self.тело(обычный)), ("no", None))
        # А большое тело с сигнатурой — маршрут.
        большое = self.большая_история()
        большое.append({"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."})
        self.assertEqual(proxy.compact_verdict(self.тело(большое))[0], "route")


class КомпактТелоTests(unittest.TestCase):
    def test_подменяется_только_модель(self):
        doc = {"model": "qwen3.8-27b", "max_tokens": 8192, "stream": True,
               "system": [{"type": "text", "text": "сис"}],
               "tools": [{"name": "Bash"}], "output_config": {"effort": "high"},
               "messages": history(4)}
        готовое = json.loads(proxy.compact_body(doc))
        self.assertEqual(готовое.pop("model"), proxy.COMPACT_MODEL)
        исходное = dict(doc)
        исходное.pop("model")
        self.assertEqual(готовое, исходное, "в облако едет то же тело, только с другой моделью")

    def test_история_не_прунится(self):
        """Ради сырой истории маршрут и заведён: сводка пишется по оригиналам выводов."""
        doc = {"model": "qwen3.8-27b", "messages": history(120)}
        готовое = json.loads(proxy.compact_body(doc))
        тексты = [b.get("content") for m in готовое["messages"] if isinstance(m["content"], list)
                  for b in m["content"] if b.get("type") == "tool_result"]
        self.assertTrue(тексты)
        self.assertNotIn(proxy.PRUNE_PLACEHOLDER, тексты)

    def test_вход_не_мутирует(self):
        doc = {"model": "qwen3.8-27b", "messages": history(4)}
        до = copy.deepcopy(doc)
        proxy.compact_body(doc)
        self.assertEqual(doc, до)


class ОтветЗаглушка:
    def __init__(self, status, куски=(b"data: {}\n\n",)):
        self.status, self.reason, self._куски = status, "", list(куски)

    def read(self, n=None):
        return "ошибка провайдера".encode()

    def read1(self, n):
        return self._куски.pop(0) if self._куски else b""

    def getheaders(self):
        return [("Content-Type", "text/event-stream")]


class СоединениеЗаглушка:
    """Фейковый http.client-совместимый коннектор: сеть в тестах не трогаем."""
    последний = None

    def __init__(self, status=200, ошибка=None, куски=(b"data: {}\n\n",)):
        self.status, self.ошибка, self.куски = status, ошибка, куски
        self.запрос = None
        СоединениеЗаглушка.последний = self

    def фабрика(self):
        def создать(host, port, timeout=None):
            self.host, self.port, self.timeout = host, port, timeout
            return self
        return создать

    def request(self, method, path, body=None, headers=None):
        if self.ошибка:
            raise self.ошибка
        self.запрос = (method, path, body, headers)

    def getresponse(self):
        return ОтветЗаглушка(self.status, self.куски)

    def close(self):
        self.закрыто = True


class КомпактМаршрутTests(unittest.TestCase):
    def setUp(self):
        self.ключ_был = proxy._compact_key
        self.upstream_был = proxy.COMPACT_UPSTREAM
        proxy._compact_key = "sk-test"
        proxy.COMPACT_UPSTREAM = "https://api.minimax.io/anthropic"

    def tearDown(self):
        proxy._compact_key = self.ключ_был
        proxy.COMPACT_UPSTREAM = self.upstream_был

    def test_путь_базы_склеивается_с_путём_клиента(self):
        связь = СоединениеЗаглушка()
        итог = proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика())
        self.assertIsNotNone(итог)
        метод, путь, тело, заголовки = связь.запрос
        self.assertEqual((метод, путь), ("POST", "/anthropic/v1/messages"))
        self.assertEqual(заголовки["x-api-key"], "sk-test")
        self.assertEqual((связь.host, связь.port), ("api.minimax.io", None))

    def test_первый_кусок_прочитан_до_ответа_клиенту(self):
        связь = СоединениеЗаглушка(куски=(b"event: message_start\n",))
        _, _, первый = proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика())
        self.assertEqual(первый, b"event: message_start\n")

    def test_fail_closed_при_ошибке_сети(self):
        связь = СоединениеЗаглушка(ошибка=OSError("сеть недоступна"))
        self.assertIsNone(proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика()))

    def test_fail_closed_при_не_200(self):
        """Неверный ключ — это 401, и он обязан кончаться фолбэком, а не ошибкой у клиента."""
        for код in (401, 429, 500):
            with self.subTest(код):
                связь = СоединениеЗаглушка(status=код)
                self.assertIsNone(proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика()))

    def test_fail_closed_при_пустом_ответе(self):
        связь = СоединениеЗаглушка(куски=())
        self.assertIsNone(proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика()))

    def test_без_ключа_маршрут_не_едет(self):
        proxy._compact_key = ""
        связь = СоединениеЗаглушка()
        self.assertIsNone(proxy.open_compact(b"{}", "/v1/messages", connector=связь.фабрика()))
        self.assertIsNone(связь.запрос, "без ключа в облако вообще не стучимся")

    def test_ключ_читается_из_конфига_бэкенда(self):
        каталог = tempfile.mkdtemp()
        путь = os.path.join(каталог, "appsettings.Local.json")
        with open(путь, "w", encoding="utf-8") as f:
            json.dump({"LlmProviders": {"minimax": {"ApiKey": "sk-из-конфига"}}}, f)
        файл_был, proxy.COMPACT_KEY_FILE = proxy.COMPACT_KEY_FILE, путь
        try:
            proxy._compact_key = None
            self.assertEqual(proxy.compact_api_key(), "sk-из-конфига")
            proxy._compact_key = None
            proxy.COMPACT_KEY_FILE = os.path.join(каталог, "нет-такого.json")
            self.assertEqual(proxy.compact_api_key(), "", "нет файла — маршрут выключается сам")
        finally:
            proxy.COMPACT_KEY_FILE = файл_был
            shutil.rmtree(каталог, ignore_errors=True)


class ГраницаПамятьTests(unittest.TestCase):
    """Память «сессия → последняя граница»: карточка только на сдвиге."""

    def setUp(self):
        proxy._boundaries.clear()

    def test_первое_срабатывание_это_сдвиг(self):
        self.assertTrue(proxy.boundary_shifted("сессия-1", 66))

    def test_та_же_граница_второй_раз_не_сдвиг(self):
        proxy.boundary_shifted("сессия-1", 66)
        self.assertFalse(proxy.boundary_shifted("сессия-1", 66))
        self.assertFalse(proxy.boundary_shifted("сессия-1", 66))

    def test_новая_граница_это_сдвиг(self):
        proxy.boundary_shifted("сессия-1", 66)
        self.assertTrue(proxy.boundary_shifted("сессия-1", 133))
        self.assertFalse(proxy.boundary_shifted("сессия-1", 133))

    def test_сессии_не_мешают_друг_другу(self):
        proxy.boundary_shifted("сессия-1", 66)
        self.assertTrue(proxy.boundary_shifted("сессия-2", 66), "у второго чата своя граница")

    def test_без_сессии_и_без_блоков_событий_нет(self):
        self.assertFalse(proxy.boundary_shifted(None, 66), "нет заголовка — некому показывать")
        self.assertFalse(proxy.boundary_shifted("", 66))
        self.assertFalse(proxy.boundary_shifted("сессия-1", 0), "обрезки не было — и карточки нет")


class РазбивкаПоВидамTests(unittest.TestCase):
    def test_виды_считаются_отдельно(self):
        msgs = history(120)  # меньше — история не дотягивает до гейта, обрезать нечего
        msgs.insert(1, {"role": "assistant", "content": [
            {"type": "thinking", "thinking": BIG, "signature": "sig"}]})
        msgs.insert(2, {"role": "assistant", "content": [
            {"type": "tool_use", "id": "w", "name": "Write",
             "input": {"file_path": "/a.txt", "content": BIG}}]})
        виды = {}
        _, _, blocks = proxy.prune_tool_results(msgs, prune_inputs=True, prune_thinking=True,
                                                kinds_out=виды, **P)
        self.assertEqual(sum(виды.values()), blocks, "разбивка обязана сходиться с итогом")
        self.assertEqual(виды.get("thinking"), 1)
        self.assertEqual(виды.get("input"), 1)
        self.assertGreater(виды.get("result", 0), 0)

    def test_без_обрезки_разбивка_пустая(self):
        виды = {}
        proxy.prune_tool_results(history(3), kinds_out=виды, **P)
        self.assertEqual(виды, {})


class UsageИзОтветаTests(unittest.TestCase):
    def начало(self, usage):
        return ("event: message_start\ndata: "
                + json.dumps({"type": "message_start", "message": {"usage": usage}})
                + "\n\n").encode()

    def конец(self, usage):
        return ("event: message_delta\ndata: "
                + json.dumps({"type": "message_delta", "delta": {}, "usage": usage})
                + "\n\n").encode()

    def копилка(self, *куски):
        собранный = {}
        for кусок in куски:
            proxy.merge_usage(собранный, кусок)
        return собранный

    def test_промпт_это_сумма_трёх_слагаемых(self):
        """Форма `message_start`: `input_tokens` считает только то, что прошло МИМО кэша."""
        чтение, промпт = proxy.usage_totals(self.копилка(self.начало(
            {"input_tokens": 1000, "cache_read_input_tokens": 9000,
             "cache_creation_input_tokens": 500})))
        self.assertEqual(чтение, 9000)
        self.assertEqual(промпт, 10500)

    def test_в_финальной_форме_input_tokens_это_весь_промпт(self):
        """Форма `message_delta` роутера vLLM: слагаемые сложены — складывать их ещё раз нельзя."""
        чтение, промпт = proxy.usage_totals(self.копилка(self.конец(
            {"input_tokens": 10500, "cache_read_input_tokens": 9000})))
        self.assertEqual(чтение, 9000)
        self.assertEqual(промпт, 10500, "сложили бы вслепую — промпт удвоился бы, а доля соврала")

    def test_цифры_кэша_берутся_из_конца_потока(self):
        """Ровно то, из-за чего карточка ехала с `cacheReadTokens: null`: в `message_start`
        роутера vLLM цифр кэша нет вовсе, они приходят последним событием.

        Хвостовое событие с нулями — не выдумка: промежуточные `message_delta` приходят и
        после главного, и слияние затиранием обнулило бы уже известное.
        """
        копилка = self.копилка(self.начало({"input_tokens": 2000, "output_tokens": 1}),
                               b'data: {"type":"content_block_delta"}\n\n',
                               self.конец({"input_tokens": 20000, "output_tokens": 40,
                                           "cache_read_input_tokens": 18000}),
                               self.конец({"output_tokens": 0}))
        self.assertEqual(proxy.usage_totals(копилка), (18000, 20000))
        self.assertEqual(копилка["output_tokens"], 40, "по ходу потока цифры только растут")

    def test_без_кэша_цифры_всё_равно_есть(self):
        чтение, промпт = proxy.usage_totals(self.копилка(self.начало({"input_tokens": 1234})))
        self.assertIsNone(чтение)
        self.assertEqual(промпт, 1234)

    def test_чужой_формат_не_роняет(self):
        for кусок in (b"", "data: не json\n\n".encode(), b"event: ping\n\n",
                      b'data: {"type":"content_block_delta"}\n\n', b"\x00\xff"):
            with self.subTest(кусок):
                self.assertEqual(proxy.usage_totals(self.копилка(кусок)), (None, None))


class НормализацияUsageTests(unittest.TestCase):
    """Нормализация usage к семантике Anthropic: `input_tokens` — только то, что мимо кэша.

    Роутер vLLM в финальном `message_delta` кладёт в `input_tokens` ВЕСЬ промпт, кэш включительно,
    и CLI складывает `input + cache_read + cache_creation` вслепую, удваивая контекст. Нормализуем
    только локальный ответ: облачный (MiniMax) уже в семантике Anthropic.
    """

    def setUp(self):
        proxy.stats["usage_normalized"] = 0


    def test_normalize_usage_полная_форма_нормализуется(self):
        """Форма `message_delta` vLLM: `input` включает кэш — переписывается на `input − cache`."""
        u = {"input_tokens": 129024, "cache_read_input_tokens": 128128,
             "cache_creation_input_tokens": 896, "output_tokens": 40}
        n, changed = proxy.normalize_usage(u)
        self.assertTrue(changed)
        self.assertEqual(n["input_tokens"], 0)  # 129024 − 128128 − 896
        self.assertEqual(n["cache_read_input_tokens"], 128128)
        self.assertEqual(n["output_tokens"], 40)
        self.assertEqual(u["input_tokens"], 129024, "вход не мутирует")

    def test_normalize_usage_правильная_форма_не_трогается(self):
        """Форма `message_start`: `input < cache_read` — уже Anthropic-семантика, менять нечего."""
        u = {"input_tokens": 2000, "cache_read_input_tokens": 18000, "output_tokens": 40}
        n, changed = proxy.normalize_usage(u)
        self.assertFalse(changed)
        self.assertIs(n, u)

    def test_normalize_usage_без_кэша_не_трогается(self):
        u = {"input_tokens": 500, "output_tokens": 40}
        n, changed = proxy.normalize_usage(u)
        self.assertFalse(changed)
        self.assertIs(n, u)

    def test_normalize_usage_облачный_ответ_не_трогается(self):
        """Облачный (MiniMax) уже в семантике Anthropic: `input < cache_read` — не трогать."""
        u = {"input_tokens": 100, "cache_read_input_tokens": 18000,
             "cache_creation_input_tokens": 0, "output_tokens": 40}
        n, changed = proxy.normalize_usage(u)
        self.assertFalse(changed)
        self.assertIs(n, u)

    def test_normalize_usage_не_ниже_ноля(self):
        """`input ≥ cache_read` (полная форма), но `input − cache − creation` уходит в минус."""
        u = {"input_tokens": 250, "cache_read_input_tokens": 200,
             "cache_creation_input_tokens": 50, "output_tokens": 40}
        n, changed = proxy.normalize_usage(u)
        self.assertTrue(changed)
        self.assertEqual(n["input_tokens"], 0)  # 250 − 200 − 50 = 0, не −0

    def _sse(self, usage, тип="message_delta"):
        if тип == "message_start":
            data = {"type": "message_start", "message": {"usage": usage}}
        else:
            data = {"type": "message_delta", "delta": {"stop_reason": "end_turn"}, "usage": usage}
        return ("event: " + тип + "\ndata: " + json.dumps(data) + "\n\n").encode()

    def test_нормализатор_sse_message_delta_нормализуется(self):
        полный = {"input_tokens": 129024, "cache_read_input_tokens": 128128,
                  "cache_creation_input_tokens": 896, "output_tokens": 40}
        собрано = []
        n = proxy.НормализаторUsage(lambda c: собрано.append(c), sse=True)
        n.усвоить(self._sse(полный))
        n.дослать()
        self.assertEqual(len(собрано), 1)
        self.assertIn(b'"input_tokens": 0', собрано[0])
        self.assertEqual(proxy.stats["usage_normalized"], 1)

    def test_нормализатор_sse_message_start_не_трогается(self):
        собрано = []
        n = proxy.НормализаторUsage(lambda c: собрано.append(c), sse=True)
        n.усвоить(self._sse({"input_tokens": 2000, "output_tokens": 1}, "message_start"))
        n.дослать()
        self.assertIn(b'"input_tokens": 2000', собрано[0])
        self.assertEqual(proxy.stats["usage_normalized"], 0)

    def test_нормализатор_sse_message_stop_не_теряется(self):
        """Два события в одном чанке: `message_delta` нормализуется, `message_stop` доходит."""
        полный = {"input_tokens": 20000, "cache_read_input_tokens": 18000, "output_tokens": 40}
        чанк = (self._sse(полный) + "event: message_stop\ndata: {}\n\n".encode())
        собрано = []
        n = proxy.НормализаторUsage(lambda c: собрано.append(c), sse=True)
        n.усвоить(чанк)
        n.дослать()
        склейка = b"".join(собрано)
        self.assertIn(b"message_stop", склейка, "message_stop не должен потеряться")
        self.assertIn(b'"input_tokens": 2000', склейка)  # 20000 − 18000
        self.assertEqual(proxy.stats["usage_normalized"], 1)

    def test_нормализатор_json_нормализуется(self):
        полный = {"input_tokens": 129024, "cache_read_input_tokens": 128128,
                  "cache_creation_input_tokens": 896, "output_tokens": 40}
        собрано = []
        n = proxy.НормализаторUsage(lambda c: собрано.append(c), sse=False)
        n.усвоить(json.dumps({"type": "message", "usage": полный}).encode())
        n.дослать()
        doc = json.loads(собрано[0])
        self.assertEqual(doc["usage"]["input_tokens"], 0)
        self.assertEqual(proxy.stats["usage_normalized"], 1)

    def test_нормализатор_json_правильная_форма_не_трогается(self):
        u = {"input_tokens": 2000, "cache_read_input_tokens": 18000, "output_tokens": 40}
        собрано = []
        n = proxy.НормализаторUsage(lambda c: собрано.append(c), sse=False)
        n.усвоить(json.dumps({"type": "message", "usage": u}).encode())
        n.дослать()
        doc = json.loads(собрано[0])
        self.assertEqual(doc["usage"]["input_tokens"], 2000)
        self.assertEqual(proxy.stats["usage_normalized"], 0)

    def test_мутация_признака_формы_красит(self):
        """Признак полной формы — тот же, что в `usage_totals`: сломаешь его, нормализация сломается."""
        u = {"input_tokens": 129024, "cache_read_input_tokens": 128128,
             "cache_creation_input_tokens": 896, "output_tokens": 40}
        with unittest.mock.patch.object(proxy, "_полная_форма", lambda x: False):
            _, changed = proxy.normalize_usage(u)
            self.assertFalse(changed, "признак формы сломан — полная форма не распознана")
        with unittest.mock.patch.object(proxy, "_полная_форма", lambda x: True):
            n, changed = proxy.normalize_usage({"input_tokens": 2000, "cache_read_input_tokens": 18000})
            self.assertTrue(changed, "признак формы сломан — правильная форма распознана как полная")


class ОтправкаСобытияTests(unittest.TestCase):
    """Канал до бэкенда: выключен без адреса и без секрета, fail-open при любом отказе."""

    def setUp(self):
        self.сохранено = (proxy.BACKEND_EVENTS_URL, proxy._event_secret, dict(proxy.stats))
        proxy._event_secret = "секрет"
        proxy.stats.update(events_sent=0, events_failed=0)

    def tearDown(self):
        proxy.BACKEND_EVENTS_URL, proxy._event_secret, стат = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)

    def test_без_адреса_канал_молчит(self):
        proxy.BACKEND_EVENTS_URL = ""
        связь = СоединениеЗаглушка()
        self.assertFalse(proxy.post_event({"kind": "prune"}, connector=связь.фабрика()))
        self.assertIsNone(связь.запрос, "адрес не задан — никуда не стучимся вовсе")

    def test_без_секрета_канал_молчит(self):
        proxy.BACKEND_EVENTS_URL = "http://127.0.0.1:5000/api/internal/llm-proxy/events"
        proxy._event_secret = ""
        связь = СоединениеЗаглушка()
        self.assertFalse(proxy.post_event({"kind": "prune"}, connector=связь.фабрика()))
        self.assertIsNone(связь.запрос)

    def test_не_ascii_секрет_не_попадает_в_журнал(self):
        """Кириллический секрет заголовком не отправить — но и в журнале ему не место."""
        каталог = tempfile.mkdtemp()
        путь = os.path.join(каталог, "appsettings.Local.json")
        with open(путь, "w", encoding="utf-8") as f:
            json.dump({"LlmProxy": {"EventSecret": "секретище"}}, f)
        файл_был, proxy.EVENT_SECRET_FILE = proxy.EVENT_SECRET_FILE, путь
        try:
            proxy._event_secret = None
            журнал = io.StringIO()
            with contextlib.redirect_stderr(журнал):
                self.assertEqual(proxy.event_secret(), "", "канал выключается сам")
            self.assertNotIn("секретище", журнал.getvalue())
            self.assertIn("не-ASCII", журнал.getvalue())
        finally:
            proxy.EVENT_SECRET_FILE = файл_был
            shutil.rmtree(каталог, ignore_errors=True)

    def test_секрет_читается_из_конфига_бэкенда(self):
        каталог = tempfile.mkdtemp()
        путь = os.path.join(каталог, "appsettings.Local.json")
        with open(путь, "w", encoding="utf-8") as f:
            json.dump({"LlmProxy": {"EventSecret": "s3cr3t"}}, f)
        файл_был, proxy.EVENT_SECRET_FILE = proxy.EVENT_SECRET_FILE, путь
        try:
            proxy._event_secret = None
            self.assertEqual(proxy.event_secret(), "s3cr3t")
            proxy._event_secret = None
            proxy.EVENT_SECRET_FILE = os.path.join(каталог, "нет-такого.json")
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(proxy.event_secret(), "", "нет файла — канал выключается сам")
        finally:
            proxy.EVENT_SECRET_FILE = файл_был
            shutil.rmtree(каталог, ignore_errors=True)

    def test_отказ_бэкенда_не_ломает_ход(self):
        """Fail-open: и сеть, и не-200 кончаются строкой в журнал, а не исключением."""
        proxy.BACKEND_EVENTS_URL = "http://127.0.0.1:5000/api/internal/llm-proxy/events"
        for связь in (СоединениеЗаглушка(ошибка=OSError("бэкенд лежит")),
                      СоединениеЗаглушка(status=500), СоединениеЗаглушка(status=401)):
            with self.subTest(связь.status):
                журнал = io.StringIO()
                with contextlib.redirect_stderr(журнал):
                    self.assertFalse(proxy.post_event({"kind": "prune"},
                                                      connector=связь.фабрика()))
                self.assertIn("не доставлено", журнал.getvalue())
        self.assertEqual(proxy.stats["events_failed"], 3)
        self.assertEqual(proxy.stats["events_sent"], 0)


class Заглушка(BaseHTTPRequestHandler):
    """Сервер-заглушка на localhost: изображает то облако, то локальный vLLM."""
    протокол_версия = "HTTP/1.1"
    статус = 200
    метка = "локальный".encode()
    принятые = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        тело = self.rfile.read(n)
        type(self).принятые.append((self.path, тело, dict(self.headers)))
        ответ = b"data: " + type(self).метка + b"\n\n"
        self.send_response(type(self).статус)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Content-Length", str(len(ответ)))
        self.end_headers()
        self.wfile.write(ответ)


def поднять(обработчик):
    сервер = ThreadingHTTPServer(("127.0.0.1", 0), обработчик)
    сервер.daemon_threads = True
    threading.Thread(target=сервер.serve_forever, daemon=True).start()
    return сервер


class КомпактСквознойTests(unittest.TestCase):
    """Сквозная проверка через настоящий сокет: кто в итоге ответил клиенту.

    Внешней сети нет — оба upstream'а это localhost-заглушки, поэтому тест можно гонять
    где угодно, включая CI.
    """

    def setUp(self):
        class Облако(Заглушка):
            метка = "облако".encode()
            принятые = []

        class Локальный(Заглушка):
            метка = "локальный".encode()
            принятые = []

        self.Облако, self.Локальный = Облако, Локальный
        self.облако, self.локальный = поднять(Облако), поднять(Локальный)
        self.прокси = поднять(proxy.Handler)
        self.сохранено = (proxy.UP_HOST, proxy.UP_PORT, proxy.COMPACT_UPSTREAM,
                          proxy._compact_key, dict(proxy.stats))
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.локальный.server_address[1]
        proxy.COMPACT_UPSTREAM = f"http://127.0.0.1:{self.облако.server_address[1]}"
        proxy._compact_key = "sk-test"
        proxy.stats.update(compact_routed=0, compact_fallback=0, compact_signature_small=0)

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.COMPACT_UPSTREAM,
         proxy._compact_key, стат) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        for с in (self.облако, self.локальный, self.прокси):
            с.shutdown()
            с.server_close()

    def спросить(self, msgs):
        тело = json.dumps({"model": "qwen3.8-27b", "max_tokens": 8192,
                           "messages": msgs}).encode()
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("POST", "/v1/messages", body=тело,
                  headers={"Content-Type": "application/json", "Content-Length": str(len(тело))})
        ответ = c.getresponse().read()
        c.close()
        return ответ

    def запрос_сжатия(self):
        msgs = history(120)
        msgs.append({"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."})
        return msgs

    def test_сжатие_уходит_в_облако(self):
        ответ = self.спросить(self.запрос_сжатия())
        self.assertIn("облако".encode(), ответ)
        self.assertEqual(proxy.stats["compact_routed"], 1)
        self.assertEqual(proxy.stats["compact_fallback"], 0)
        путь, тело, _ = self.Облако.принятые[-1]
        self.assertEqual(путь, "/v1/messages")
        self.assertEqual(json.loads(тело)["model"], proxy.COMPACT_MODEL)

    def test_обычный_ход_идёт_в_локальный_upstream(self):
        msgs = history(120)
        msgs.append({"role": "user", "content": "проверь сборку"})
        self.assertIn("локальный".encode(), self.спросить(msgs))
        self.assertEqual(self.Облако.принятые, [], "обычный ход в облако не ходит вовсе")
        self.assertEqual(proxy.stats["compact_routed"], 0)

    def test_облако_упало_сжатие_идёт_локально(self):
        """Fail-closed целиком: клиент получает ответ локальной модели и разницы не видит."""
        self.Облако.статус = 401  # неверный ключ
        ответ = self.спросить(self.запрос_сжатия())
        self.assertIn("локальный".encode(), ответ)
        self.assertEqual(proxy.stats["compact_fallback"], 1)
        self.assertEqual(proxy.stats["compact_routed"], 0)
        self.assertTrue(self.Локальный.принятые, "фолбэк обязан дойти до локального upstream")

    def test_облака_нет_вовсе_сжатие_идёт_локально(self):
        self.облако.shutdown()
        self.облако.server_close()
        self.assertIn("локальный".encode(), self.спросить(self.запрос_сжатия()))
        self.assertEqual(proxy.stats["compact_fallback"], 1)

    def test_сигнатура_с_малым_телом_видна_в_счётчике_и_журнале(self):
        """Молчаливый уход сжатия на локаль обязан оставлять след: счётчик плюс строка в журнал.

        Сам маршрут при этом не меняется — запрос честно идёт на локальную модель, как и
        раньше; чинится он настройкой стенда, а прокси только показывает, что чинить.
        """
        msgs = [{"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."}]
        журнал = io.StringIO()
        with contextlib.redirect_stderr(журнал):
            ответ = self.спросить(msgs)
        self.assertIn("локальный".encode(), ответ)
        self.assertEqual(self.Облако.принятые, [], "нераспознанное сжатие в облако не ходит")
        self.assertEqual(proxy.stats["compact_signature_small"], 1)
        self.assertEqual((proxy.stats["compact_routed"], proxy.stats["compact_fallback"]), (0, 0))
        строка = журнал.getvalue()
        self.assertIn("автосжатие: сигнатура есть", строка)
        self.assertIn(str(proxy.COMPACT_MIN_CONTEXT_TOKENS), строка, "порог в журнале обязателен")

    def test_счётчик_нераспознанного_виден_в_stats(self):
        """Счётчик заведён в самом словаре, а не появляется после первого случая.

        Иначе наблюдать нечего до первого сбоя: `/__proxy/stats` свежего процесса ключа не
        показал бы вовсе, и «ноль» было бы не отличить от «такого счётчика тут нет».
        """
        исходный = self.сохранено[4]  # снимок stats до подмены в setUp
        self.assertIn("compact_signature_small", исходный)
        msgs = [{"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."}]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(msgs)
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("GET", "/__proxy/stats")
        отдано = json.loads(c.getresponse().read())
        c.close()
        self.assertEqual(отдано["compact_signature_small"], 1)

    def test_маршрут_выключен_по_умолчанию(self):
        proxy.COMPACT_UPSTREAM = ""
        self.assertIn("локальный".encode(), self.спросить(self.запрос_сжатия()))
        self.assertEqual(self.Облако.принятые, [])
        self.assertEqual((proxy.stats["compact_routed"], proxy.stats["compact_fallback"]), (0, 0))


class МодельЗаглушка(Заглушка):
    """vLLM-заглушка: отвечает SSE с `message_start`, помедлив — иначе prefill нечем мерить.

    Цифры кэша кладёт в ФИНАЛЬНЫЙ `message_delta`, как настоящий Anthropic-роутер vLLM
    (снято живьём 2026-09-23): в `message_start` у него только `input_tokens` мимо кэша и
    `output_tokens`. Заглушка, отдающая кэш сразу, скрыла бы ровно тот дефект, из-за которого
    карточка ехала с `cacheReadTokens: null`.
    """
    задержка = 0.05
    usage_старт = {"input_tokens": 2000, "output_tokens": 1}
    usage_конец = {"input_tokens": 20000, "output_tokens": 40,
                   "cache_read_input_tokens": 18000}
    принятые = []

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        тело = self.rfile.read(n)
        type(self).принятые.append((self.path, тело, dict(self.headers)))
        начало = json.dumps({"type": "message_start",
                             "message": {"usage": type(self).usage_старт}})
        конец = json.dumps({"type": "message_delta", "delta": {"stop_reason": "end_turn"},
                            "usage": type(self).usage_конец})
        # Двумя кусками с паузой, а не одним: иначе `message_start` и финальный
        # `message_delta` приехали бы в прокси одним чтением, и тест не отличил бы «копим
        # usage по всему потоку» от «взяли из первого куска» — то есть не проверял бы главное.
        куски = [f"event: message_start\ndata: {начало}\n\n".encode(),
                 (f"event: message_delta\ndata: {конец}\n\n"
                  "event: message_stop\ndata: {}\n\n").encode()]
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Content-Length", str(sum(len(к) for к in куски)))
        self.end_headers()
        time.sleep(type(self).задержка)  # prefill: модель думает перед первым байтом
        for кусок in куски:
            self.wfile.write(кусок)
            self.wfile.flush()
            time.sleep(0.02)  # decode: ответ идёт потоком, а не мгновением


class БэкендЗаглушка(BaseHTTPRequestHandler):
    """Ручка бэкенда /api/internal/llm-proxy/events: складывает принятые события."""
    принятые = []
    статус = 200

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        тело = self.rfile.read(n)
        type(self).принятые.append((self.path, json.loads(тело), dict(self.headers)))
        self.send_response(type(self).статус)
        self.send_header("Content-Length", "0")
        self.end_headers()


class СобытиеСквозноеTests(unittest.TestCase):
    """Полный путь карточки: CLI → прокси → ответ модели → событие на бэкенд.

    Сети наружу нет: и «vLLM», и «бэкенд» — localhost-заглушки.
    """

    def setUp(self):
        class Модель(МодельЗаглушка):
            принятые = []

        class Бэкенд(БэкендЗаглушка):
            принятые = []

        self.Модель, self.Бэкенд = Модель, Бэкенд
        self.модель, self.бэкенд = поднять(Модель), поднять(Бэкенд)
        self.прокси = поднять(proxy.Handler)
        self.сохранено = (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM,
                          proxy.BACKEND_EVENTS_URL, proxy._event_secret, dict(proxy.stats),
                          dict(proxy._boundaries), proxy.PRUNE_MIN_CONTEXT_TOKENS)
        # Полосы history(120/150/160/200) подобраны под гейт 190k: тест про карточки, а не про
        # гейт, поэтому смена дефолта гейта не должна двигать его геометрию.
        proxy.PRUNE_MIN_CONTEXT_TOKENS = 190_000
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.модель.server_address[1]
        proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM = "on", ""
        proxy.BACKEND_EVENTS_URL = (f"http://127.0.0.1:{self.бэкенд.server_address[1]}"
                                    "/api/internal/llm-proxy/events")
        proxy._event_secret = "secret-from-config"
        proxy._boundaries.clear()
        proxy.stats.update(events_sent=0, events_failed=0)

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM,
         proxy.BACKEND_EVENTS_URL, proxy._event_secret, стат, границы,
         proxy.PRUNE_MIN_CONTEXT_TOKENS) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        proxy._boundaries.clear()
        proxy._boundaries.update(границы)
        for с in (self.модель, self.бэкенд, self.прокси):
            с.shutdown()
            с.server_close()

    def спросить(self, msgs, сессия="sess-1", ждать_карточку=True):
        """Ход через прокси. `ждать_карточку` — дожидаться ли отправки события бэкенду.

        Ждать приходится ПОСЛЕ чтения ответа: карточка уходит по концу потока (цифры кэша
        приезжают финальным `message_delta`), то есть позже последнего байта, который прочитал
        клиент. Отправка вдобавок фоновая. Тест, где карточки быть не должно, просто даёт
        прокси мгновение — лишнее событие успело бы прийти и попасться.
        """
        прежний = proxy._last_event_thread
        тело = json.dumps({"model": "qwen3.8-27b", "max_tokens": 8192,
                           "messages": msgs}).encode()
        заголовки = {"Content-Type": "application/json", "Content-Length": str(len(тело))}
        if сессия is not None:
            заголовки[proxy.EVENT_SESSION_HEADER] = сессия
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("POST", "/v1/messages", body=тело, headers=заголовки)
        ответ = c.getresponse().read()
        c.close()
        срок = time.monotonic() + (10 if ждать_карточку else 0.3)
        while time.monotonic() < срок and proxy._last_event_thread is прежний:
            time.sleep(0.005)
        if proxy._last_event_thread is not прежний and proxy._last_event_thread is not None:
            proxy._last_event_thread.join(timeout=10)
        return ответ

    def test_событие_уходит_на_сдвиге_границы(self):
        self.спросить(history(120))
        self.assertEqual(len(self.Бэкенд.принятые), 1)
        путь, событие, заголовки = self.Бэкенд.принятые[0]
        self.assertEqual(путь, "/api/internal/llm-proxy/events")
        self.assertEqual(заголовки.get("X-Proxy-Secret"), "secret-from-config")
        self.assertEqual(событие["sessionId"], "sess-1")
        self.assertEqual(событие["kind"], "prune")
        self.assertEqual(событие["blocks"], 66)
        self.assertEqual(событие["resultBlocks"], 66)
        self.assertEqual((событие["inputBlocks"], событие["thinkingBlocks"]), (0, 0))
        self.assertGreater(событие["tokensBefore"], событие["tokensAfter"])
        self.assertEqual(proxy.stats["events_sent"], 1)

    def test_замер_prefill_и_цифры_кэша_из_ответа(self):
        self.спросить(history(120))
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertGreaterEqual(событие["prefillSeconds"], МодельЗаглушка.задержка,
                                "замер обязан покрывать ожидание первого байта")
        self.assertLess(событие["prefillSeconds"], 10)
        self.assertEqual(событие["cacheReadTokens"], 18000)
        self.assertEqual(событие["promptTokens"], 20000)

    def test_между_сдвигами_карточки_нет(self):
        """Граница стоит — обрезка бесплатна, и шуметь в ленте не о чем."""
        self.спросить(history(120))
        self.спросить(history(150), ждать_карточку=False)
        self.спросить(history(160), ждать_карточку=False)
        self.assertEqual(len(self.Бэкенд.принятые), 1, "одна граница — одна карточка")
        self.спросить(history(200))  # следующая полоса контекста
        self.assertEqual(len(self.Бэкенд.принятые), 2)
        self.assertEqual(self.Бэкенд.принятые[1][1]["blocks"], 133)

    def test_у_каждого_чата_своя_граница(self):
        self.спросить(history(120), сессия="sess-1")
        self.спросить(history(120), сессия="sess-2")
        self.assertEqual(len(self.Бэкенд.принятые), 2)
        self.assertEqual({с["sessionId"] for _, с, _ in self.Бэкенд.принятые}, {"sess-1", "sess-2"})

    def test_без_заголовка_сессии_событий_нет(self):
        """Ход без заголовка проксируется как раньше — показывать карточку всё равно некому."""
        self.спросить(history(120), сессия=None, ждать_карточку=False)
        self.assertEqual(self.Бэкенд.принятые, [])
        self.assertTrue(self.Модель.принятые, "сам ход при этом идёт как обычно")

    def test_упавший_бэкенд_не_ломает_ход(self):
        """Fail-open по-настоящему: ответ модели доходит до клиента целиком."""
        self.бэкенд.shutdown()
        self.бэкенд.server_close()
        журнал = io.StringIO()
        with contextlib.redirect_stderr(журнал):
            ответ = self.спросить(history(120))
        self.assertIn("message_start".encode(), ответ)
        self.assertIn("message_stop".encode(), ответ)
        self.assertEqual(proxy.stats["events_failed"], 1)
        self.assertIn("не доставлено", журнал.getvalue())

    def test_сжатие_в_облако_даёт_свою_карточку_а_не_прунинговую(self):
        """В облако едет СЫРАЯ история, значит рассказывать надо про облако, а не про обрезку.

        Иначе карточка «границу сдвинули, ждали столько-то» несла бы замер облачного ответа —
        при том, что обрезанное тело туда не отправлялось вовсе.
        """
        облако = поднять(МодельЗаглушка)
        proxy.COMPACT_UPSTREAM = f"http://127.0.0.1:{облако.server_address[1]}"
        proxy._compact_key = "sk-test"
        try:
            msgs = history(120)
            msgs.append({"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."})
            self.спросить(msgs)
        finally:
            облако.shutdown()
            облако.server_close()
        self.assertEqual(len(self.Бэкенд.принятые), 1)
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertEqual(событие["kind"], "compact_cloud")
        self.assertEqual(событие["blocks"], 0)
        self.assertGreater(событие["tokensBefore"], 0, "размер истории на входе в сжатие")
        self.assertEqual(proxy._boundaries, {}, "граница не запомнена — карточка ещё впереди")

    def test_канал_выключен_по_умолчанию(self):
        proxy.BACKEND_EVENTS_URL = ""
        ответ = self.спросить(history(120), ждать_карточку=False)
        self.assertIn("message_start".encode(), ответ)
        self.assertEqual(self.Бэкенд.принятые, [], "без адреса прокси никуда не стучится")


class ДампКаталогTests(unittest.TestCase):
    """Предохранитель на каталог: в телах лежат чужие чаты, и мимо /tmp и data/ им хода нет."""

    def tearDown(self):
        proxy._dump_dir = None

    def test_вне_разрешённых_корней_дамп_выключается(self):
        каталог = tempfile.mkdtemp(dir=os.path.dirname(os.path.abspath(__file__)))
        try:
            журнал = io.StringIO()
            with contextlib.redirect_stderr(журнал):
                self.assertEqual(proxy.prepare_dump_dir(каталог), "")
            self.assertIn("вне разрешённых", журнал.getvalue())
            self.assertEqual(os.listdir(каталог), [], "и ничего туда не пишем")
        finally:
            shutil.rmtree(каталог, ignore_errors=True)

    def test_пустая_настройка_это_выключенный_дамп(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(proxy.prepare_dump_dir(""), "")

    def test_каталог_под_tmp_годится_и_создаётся(self):
        путь = os.path.join(tempfile.mkdtemp(dir="/tmp"), "вложенный")
        try:
            with contextlib.redirect_stderr(io.StringIO()):
                готовый = proxy.prepare_dump_dir(путь)
            self.assertEqual(готовый, os.path.realpath(путь))
            self.assertTrue(os.path.isdir(путь))
        finally:
            shutil.rmtree(os.path.dirname(путь), ignore_errors=True)

    def test_data_репозитория_тоже_годится(self):
        корень = os.path.join(proxy.DUMP_ALLOWED_ROOTS[1], "prune-dump-тест")
        try:
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(proxy.prepare_dump_dir(корень), os.path.realpath(корень))
        finally:
            shutil.rmtree(корень, ignore_errors=True)


class ДампСквознойTests(unittest.TestCase):
    """Дамп пишет ТО, ЧТО УШЛО В ДВИЖОК: тело после правок плюс usage и замер ответа."""

    def setUp(self):
        class Модель(МодельЗаглушка):
            принятые = []

        self.Модель = Модель
        self.модель = поднять(Модель)
        self.прокси = поднять(proxy.Handler)
        self.каталог = tempfile.mkdtemp(dir="/tmp")
        self.сохранено = (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.DUMP_DIR,
                          proxy.COMPACT_UPSTREAM, proxy.BACKEND_EVENTS_URL, dict(proxy.stats))
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.модель.server_address[1]
        proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM, proxy.BACKEND_EVENTS_URL = "off", "", ""
        proxy.DUMP_DIR = self.каталог
        proxy._dump_dir = None
        with contextlib.redirect_stderr(io.StringIO()):
            proxy.prepare_dump_dir(self.каталог)

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.DUMP_DIR,
         proxy.COMPACT_UPSTREAM, proxy.BACKEND_EVENTS_URL, стат) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        proxy._dump_dir = None
        shutil.rmtree(self.каталог, ignore_errors=True)
        for с in (self.модель, self.прокси):
            с.shutdown()
            с.server_close()

    def спросить(self, msgs, сессия="sess-1"):
        тело = json.dumps({"model": "qwen3.8-27b", "max_tokens": 8192,
                           "messages": msgs}).encode()
        заголовки = {"Content-Type": "application/json", "Content-Length": str(len(тело))}
        if сессия is not None:
            заголовки[proxy.EVENT_SESSION_HEADER] = сессия
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("POST", "/v1/messages", body=тело, headers=заголовки)
        ответ = c.getresponse().read()
        c.close()
        return ответ

    def записи(self, сколько=1):
        """Записи дампа, дождавшись их появления.

        Мету прокси пишет в самом конце обработки — уже после того, как клиент получил
        ответ, — поэтому без ожидания тест читал бы каталог наперегонки с обработчиком.
        Ждём СОБЫТИЯ (файл появился), а не фиксированной паузы: на медленной машине пауза
        всё равно оказалась бы то короткой, то лишней.
        """
        предел = time.monotonic() + 5
        while time.monotonic() < предел:
            найдено = diff_miss.загрузить(self.каталог)
            if len(найдено) >= сколько:
                return найдено
            time.sleep(0.01)
        return diff_miss.загрузить(self.каталог)

    def test_тело_ложится_на_диск_уже_после_правок(self):
        """Иначе дамп врал бы о самом главном: движок видит тело ПОСЛЕ прокси, а не до него."""
        напоминание = {"role": "system",
                       "content": "<total_tokens>15000000 tokens left</total_tokens>"}
        self.спросить([{"role": "user", "content": "задача"}, напоминание])
        записи = self.записи(1)
        self.assertEqual(len(записи), 1)
        тело = diff_miss.тело(записи[0])
        self.assertEqual([m["role"] for m in тело["messages"]], ["user"],
                         "напоминание вырезано прокси — в дампе его тоже быть не должно")
        self.assertEqual(записи[0]["session"], "sess-1")
        self.assertEqual(записи[0]["upstream"], "local")

    def test_мета_несёт_usage_и_замер(self):
        self.спросить([{"role": "user", "content": "задача"}])
        мета = self.записи()[0]
        self.assertEqual(мета["usage"]["cache_read_input_tokens"], 18000)
        self.assertGreaterEqual(мета["ttfbSeconds"], МодельЗаглушка.задержка)
        self.assertEqual(diff_miss.цифры(мета)[0], 20000, "промпт — сумма слагаемых usage")

    def test_цифры_кэша_из_финального_message_delta(self):
        """Снято живьём 2026-09-23: роутер vLLM кладёт цифры кэша в КОНЕЦ потока.

        В `message_start` у него только `input_tokens` и `output_tokens`. Собирай дамп по
        первому куску — и главная цифра разбора («сколько взято из кэша») была бы пустой на
        каждом запросе; ровно так первый прогон дампа и вышел бесполезным.
        """
        class МодельСДельтой(МодельЗаглушка):
            принятые = []

            def do_POST(self):
                n = int(self.headers.get("Content-Length") or 0)
                self.rfile.read(n)
                начало = json.dumps({"type": "message_start", "message": {
                    "usage": {"input_tokens": 445, "output_tokens": 0}}})
                дельта = json.dumps({"type": "message_delta", "usage": {
                    "input_tokens": 445, "cache_creation_input_tokens": 19872,
                    "cache_read_input_tokens": 48816, "output_tokens": 557}})
                ответ = (f"event: message_start\ndata: {начало}\n\n"
                         f"event: message_delta\ndata: {дельта}\n\n").encode()
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(ответ)))
                self.end_headers()
                self.wfile.write(ответ)

        модель = поднять(МодельСДельтой)
        proxy.UP_PORT = модель.server_address[1]
        try:
            self.спросить([{"role": "user", "content": "задача"}])
        finally:
            модель.shutdown()
            модель.server_close()
        мета = self.записи()[0]
        self.assertEqual(мета["usage"]["cache_read_input_tokens"], 48816)
        self.assertEqual(мета["usage"]["output_tokens"], 557, "usage копится по всему потоку")
        промпт, чтение, доля = diff_miss.цифры(мета)
        self.assertEqual((промпт, чтение), (69133, 48816))
        self.assertAlmostEqual(доля, 48816 / 69133)

    def test_номера_растут_и_файлы_не_затирают_друг_друга(self):
        for _ in range(3):
            self.спросить([{"role": "user", "content": "задача"}])
        записи = self.записи(3)
        self.assertEqual(len(записи), 3)
        self.assertEqual([з["seq"] for з in записи], sorted(з["seq"] for з in записи))
        self.assertEqual(len({з["body"] for з in записи}), 3)

    def test_файлы_дампа_закрыты_от_чужих(self):
        """В теле чужая переписка — режим по умолчанию (0644) для неё не годится."""
        self.спросить([{"role": "user", "content": "задача"}])
        запись = self.записи()[0]
        for путь in (запись["тело_файл"], os.path.join(self.каталог, запись["мета_файл"])):
            self.assertEqual(os.stat(путь).st_mode & 0o777, 0o600, путь)

    def test_без_настройки_дамп_не_пишется(self):
        proxy.DUMP_DIR, proxy._dump_dir = "", None
        self.спросить([{"role": "user", "content": "задача"}])
        # Здесь ждать нечего — проверяем как раз отсутствие записи, поэтому читаем напрямую.
        self.assertEqual(diff_miss.загрузить(self.каталог), [],
                         "выключенный дамп не создаёт ни файла")


class DiffMissTests(unittest.TestCase):
    """Разбор синтетических пар тел: что именно инструмент называет причиной промаха."""

    def тело(self, msgs, system="системный промпт", tools=None):
        return {"model": "qwen3.8-27b", "system": system,
                "tools": tools if tools is not None else [{"name": "Bash"}], "messages": msgs}

    def test_дописанный_хвост_расхождением_не_считается(self):
        разбор = diff_miss.сравнить(self.тело(history(10)), self.тело(history(12)))
        self.assertEqual(разбор["вид"], "хвост")
        self.assertEqual(разбор["совпало_сообщений"], len(history(10)))

    def test_изменившаяся_системная_часть(self):
        разбор = diff_miss.сравнить(self.тело(history(10)),
                                    self.тело(history(10), system="системный промпт и ещё"))
        self.assertEqual(разбор["вид"], "системная")
        self.assertEqual(разбор["совпало_сообщений"], 0)
        self.assertIn("системная часть разошлась", разбор["строки"][0])

    def test_изменившийся_тулсет_это_тоже_системная_часть(self):
        разбор = diff_miss.сравнить(self.тело(history(10)),
                                    self.тело(history(10), tools=[{"name": "Read"}]))
        self.assertEqual(разбор["вид"], "системная")

    def test_вставка_сообщения_в_середину(self):
        было = history(10)
        стало = list(было)
        стало.insert(5, {"role": "user", "content": "внезапное напоминание"})
        разбор = diff_miss.сравнить(self.тело(было), self.тело(стало))
        self.assertEqual(разбор["вид"], "вставка")
        self.assertEqual(разбор["индекс"], 5)
        self.assertIn("вставлено 1 сообщений перед #5", разбор["строки"][0])

    def test_удаление_сообщения_из_середины(self):
        было = history(10)
        стало = [m for i, m in enumerate(было) if i != 4]
        разбор = diff_miss.сравнить(self.тело(было), self.тело(стало))
        self.assertEqual(разбор["вид"], "удаление")
        self.assertEqual(разбор["индекс"], 4)

    def test_замена_содержимого_показывает_первый_расходящийся_символ(self):
        было = history(10)
        стало = copy.deepcopy(было)
        стало[6]["content"][0]["content"] = "x" * 3000 + "ИНОЕ" + "x" * 2996
        разбор = diff_miss.сравнить(self.тело(было), self.тело(стало))
        self.assertEqual(разбор["вид"], "замена")
        self.assertEqual(разбор["индекс"], 6)
        self.assertIn("ИНОЕ", разбор["строки"][2], "окно вокруг расхождения обязано его показать")
        self.assertNotIn("ИНОЕ", разбор["строки"][1], "а в «было» его быть не должно")

    def test_перестановка_ключей_блока_расхождением_не_считается(self):
        """Движок читает JSON в объекты: порядок ключей на промпт не влияет, шуметь им нечего."""
        было = [{"role": "user", "content": [{"type": "text", "text": "привет"}]}]
        стало = [{"content": [{"text": "привет", "type": "text"}], "role": "user"}]
        self.assertEqual(diff_miss.сравнить(self.тело(было), self.тело(стало))["вид"], "совпало")

    def test_две_семантики_input_tokens_считаются_по_разному(self):
        """Живой факт 2026-09-23: в `message_delta` роутера vLLM `input_tokens` — ВЕСЬ промпт.

        Сложение слагаемых вслепую задваивает кэш: ход с 96 % попаданий читался как промах
        на 48 %, и разбор показывал промахи там, где их не было.
        """
        полный = {"usage": {"input_tokens": 133808, "cache_read_input_tokens": 127872,
                            "cache_creation_input_tokens": 5184}}
        промпт, чтение, доля = diff_miss.цифры(полный)
        self.assertEqual((промпт, чтение), (133808, 127872))
        self.assertGreater(доля, 0.95)
        мимо = {"usage": {"input_tokens": 2000, "cache_read_input_tokens": 18000}}
        self.assertEqual(diff_miss.цифры(мимо)[0], 20000, "форма message_start — сумма")
        пусто = {"usage": {"input_tokens": 500}}
        self.assertEqual(diff_miss.цифры(пусто), (500, None, None))

    def test_вердикт_разводит_расхождение_тела_и_вытеснение(self):
        """Три исхода, и путать их нельзя: чинятся они в разных местах."""
        self.assertTrue(diff_miss.вердикт(13000, 12960, "замена").startswith("ТЕЛО:"))
        self.assertTrue(diff_miss.вердикт(126000, 12960, "хвост").startswith("ВЫТЕСНЕНИЕ:"))
        self.assertTrue(diff_miss.вердикт(126000, 12960, "совпало").startswith("ВЫТЕСНЕНИЕ:"))
        # Кэш отдал весь совпавший префикс, а доля мала лишь из-за жирного нового хвоста —
        # это не промах вовсе. Без этой ветки разбор объявлял бы вытеснением здоровый ход
        # с одним большим Read (живой случай 2026-09-23, запрос 23).
        self.assertTrue(diff_miss.вердикт(19478, 20736, "хвост").startswith("НОРМА:"))
        self.assertTrue(diff_miss.вердикт(20000, 18000, "хвост").startswith("НОРМА:"))
        # Тела разошлись, но движок не отдал даже совпавшего — сложились обе причины.
        self.assertTrue(diff_miss.вердикт(126000, 12960, "замена").startswith("ТЕЛО + ВЫТЕСНЕНИЕ"))
        self.assertIn("usage", diff_miss.вердикт(126000, None, "замена"))

    def test_совпавший_префикс_меряется_меркой_прокси(self):
        тело = self.тело(history(10))
        весь = diff_miss.токенов_совпало(тело, len(тело["messages"]))
        часть = diff_miss.токенов_совпало(тело, 5)
        self.assertGreater(весь, часть)
        self.assertGreater(часть, 0, "системная часть входит в префикс всегда")


class DiffMissОтчётTests(unittest.TestCase):
    """Сквозной прогон по настоящей папке дампа: кого инструмент называет промахом и почему."""

    def setUp(self):
        self.каталог = tempfile.mkdtemp(dir="/tmp")

    def tearDown(self):
        shutil.rmtree(self.каталог, ignore_errors=True)

    def положить(self, seq, msgs, usage, session="sess-1", system="системный промпт",
                 path="/v1/messages"):
        тело = json.dumps({"system": system, "tools": [{"name": "Bash"}],
                           "messages": msgs}, ensure_ascii=False).encode()
        запись = proxy.ЗаписьДампа(self.каталог, seq, session, тело,
                                   {"path": path, "upstream": "local", "blocks": 0})
        запись.мета["usage"] = usage
        запись.мета["ttfbSeconds"] = 1.0
        запись.записать()

    def отчёт(self, **kw):
        буфер = io.StringIO()
        промахов = diff_miss.разобрать(self.каталог, вывод=буфер, **kw)
        return промахов, буфер.getvalue()

    def test_промах_при_дописанном_хвосте_объявлен_вытеснением(self):
        """Ровно случай живого прогона: граница стоит, тело совпадает, а из кэша — крохи."""
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        self.положить(2, history(42), {"input_tokens": 90000, "cache_read_input_tokens": 900})
        промахов, текст = self.отчёт()
        self.assertEqual(промахов, 1)
        self.assertIn("расхождений нет: дописан хвост", текст)
        self.assertIn("ВЫТЕСНЕНИЕ:", текст)
        self.assertIn("граница НЕПОДВИЖНА", текст)

    def test_промах_объяснённый_телом(self):
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        self.положить(2, history(42), {"input_tokens": 90000, "cache_read_input_tokens": 900},
                      system="другой системный промпт")
        _, текст = self.отчёт()
        self.assertIn("системная часть разошлась", текст)
        self.assertIn("ТЕЛО:", текст)

    def test_здоровые_ходы_в_разбор_не_попадают(self):
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        self.положить(2, history(42), {"input_tokens": 900, "cache_read_input_tokens": 91000})
        промахов, текст = self.отчёт()
        self.assertEqual(промахов, 0)
        self.assertIn("Промахов ниже 50% в дампе нет", текст)

    def test_чужая_сессия_в_сравнение_не_лезет_но_считается_соседом(self):
        """Сравнивать ход с фоновым одиночным запросом бессмысленно — а вот учесть его надо."""
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        self.положить(2, [{"role": "user", "content": "сводка файла"}],
                      {"input_tokens": 33000, "cache_read_input_tokens": 0}, session=None)
        self.положить(3, history(42), {"input_tokens": 90000, "cache_read_input_tokens": 900})
        промахов, текст = self.отчёт()
        self.assertIn("расхождений нет: дописан хвост", текст, "сравнили с ходом 1, а не с фоном")
        self.assertIn("между запросами прошло 1 чужих запросов на 33000 ток", текст)
        self.assertEqual(промахов, 2, "фоновый запрос без кэша — тоже промах, но без пары")
        self.assertIn("предыдущего запроса этой сессии в дампе нет", текст)

    def test_без_заголовка_ход_и_фон_разводятся_по_системной_части(self):
        """Заголовка в боевой сборке пока нет, а сравнивать ход с фоновым one-shot нельзя.

        Без этого «предыдущим запросом» хода оказывается фоновое действие продукта, у которого
        ни системной части, ни общей истории, — и разбор на каждом промахе показывал бы
        расхождение с первого байта, то есть врал бы в главном.
        """
        ход = json.dumps({"system": "промпт", "tools": [{"name": "Bash"}],
                          "messages": history(2)}, ensure_ascii=False).encode()
        фон = json.dumps({"messages": [{"role": "user", "content": "сводка файла"}]},
                         ensure_ascii=False).encode()
        for seq, тело in ((1, ход), (2, фон)):
            запись = proxy.ЗаписьДампа(self.каталог, seq, None, тело, {"upstream": "local"})
            запись.записать()
        записи = diff_miss.загрузить(self.каталог)
        self.assertEqual([diff_miss.ключ_сессии(з) for з in записи], ["ход", "фон"])
        записи[0]["session"] = "sess-1"  # заголовок появился — он и главнее
        self.assertEqual(diff_miss.ключ_сессии(записи[0]), "sess-1")

    def test_служебные_запросы_в_разбор_не_идут(self):
        """`count_tokens` CLI шлёт десятками; они ничего не генерируют и промахом не бывают."""
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        self.положить(2, history(40), {"input_tokens": 90000, "cache_read_input_tokens": 0},
                      path="/v1/messages/count_tokens?beta=true")
        self.положить(3, history(42), {"input_tokens": 91000, "cache_read_input_tokens": 90000})
        промахов, текст = self.отчёт()
        self.assertEqual(промахов, 0, "служебный запрос промахом не считается")
        self.assertIn("плюс 1 служебных", текст)

    def test_фильтр_по_сессии(self):
        self.положить(1, history(40), {"input_tokens": 90000}, session="другая")
        self.положить(2, history(40), {"input_tokens": 90000}, session="sess-1")
        _, текст = self.отчёт(сессия="sess-1")
        self.assertIn("sess-1", текст)
        self.assertNotIn("другая", текст)

    def test_таблица_всех_запросов(self):
        self.положить(1, history(40), {"input_tokens": 500, "cache_read_input_tokens": 90000})
        _, текст = self.отчёт(показать_все=True)
        self.assertIn("из кэша", текст)
        self.assertIn("99%", текст)


# --- Предохранитель от петли ---------------------------------------------------------------

# План из петли 15.10.2026 (чат спайка «хуки Claude Mods»): 50 слов, повторялся дословно.
ПЛАН = ("The web search is the highest-value action here. Let me do it. Actually, let me "
        "reconsider the approach and check the hooks documentation first. The hooks API changed "
        "in the latest release, so I should verify the event names. Then I will write the spike "
        "summary and commit the notes. ")
ВСТУПЛЕНИЕ = ("I looked at the repository layout and the settings file to see how the mods are "
              "wired into the harness today, and what the spike still has to answer. ")


def по_кусочкам(текст, шаг=7):
    """Режет текст как поток дельт: куски по `шаг` символов, слова рвутся где попало."""
    return [текст[i:i + шаг] for i in range(0, len(текст), шаг)]


def прогнать(текст, детектор=None, шаг=7):
    """Сколько слов детектор успел увидеть до срабатывания; None — не сработал."""
    д = детектор or proxy.ДетекторПетли()
    for кусок in по_кусочкам(текст, шаг):
        if д.усвоить(кусок):
            return д.всего
    д.новый_блок()
    return None if д.петля is None else д.всего


class ДетекторПетлиTests(unittest.TestCase):
    def test_петля_плана_ловится_быстро_после_начала_повтора(self):
        слов = прогнать(ВСТУПЛЕНИЕ + ПЛАН * 12)
        self.assertIsNotNone(слов)
        начало_повтора = len(ВСТУПЛЕНИЕ.split()) + len(ПЛАН.split())
        self.assertLessEqual(слов - начало_повтора, 400, "перехват обязан уложиться в окно")
        self.assertLess(слов, len((ВСТУПЛЕНИЕ + ПЛАН * 12).split()))

    def test_петля_из_инцидента_с_перефразом(self):
        """Текст сбоя 15.10 (чат 1428f5e9): повторы не дословны — «Actually, let me
        reconsider» / «Let me also reconsider», «the changelog file» / «the changelog»."""
        виток = ("The web search is the highest-value action here. Let me do it.\n"
                 "{a}, let me reconsider whether to use web{b} at all. The task says the hooks "
                 "came \"Из CLI 2.1.287\". The changelog file in the repo is the source. But it "
                 "doesn't contain {c}2.1.287 section with mod hooks. Maybe the changelog{d} is a "
                 "rolling window. Let me check the top of the changelog to see what version range "
                 "it covers.\n\nLet me be efficient and do a batch:\n1. Read top of changelog "
                 "(what version range)\n2. Check \"Из чатов\" folder\n3. Web search for {e}format "
                 "(web)\n4. claude --help for plugin subcommands\n{f}\n")
        варианты = [dict(a="Actually", b=" search", c="", d=" file", e="the ",
                         f="Let me do the web search since it's most likely to give the concrete "
                           "format, plus local checks."),
                    dict(a="Let me also", b="", c="the ", d="", e="mod hook ", f="Let me batch."),
                    dict(a="Actually", b="", c="the ", d="", e="mod hook ", f="Let me batch.")]
        начало = ("- Let me also reconsider: maybe I should look at whether the CLI has a `claude "
                  "plugin` subcommand that documents hooks, e.g. `claude plugin validate` or a "
                  "schema. Let me check `claude --help` and any mod-related subcommands.\n")
        текст = начало + "".join(виток.format(**варианты[i % 3]) for i in range(12))
        слов = прогнать(текст)
        self.assertIsNotNone(слов)
        self.assertLessEqual(слов - len((начало + виток.format(**варианты[0])).split()), 400)

    def test_короткая_петля_тоже_петля(self):
        self.assertIsNotNone(прогнать(ВСТУПЛЕНИЕ * 3 + "I will check the config and then run the tests now. " * 40))

    def test_два_повтора_это_ещё_не_петля(self):
        """Модель вправе пересказать план дважды (вывод → итог); петля — от третьего раза."""
        self.assertIsNone(прогнать(ВСТУПЛЕНИЕ * 3 + ПЛАН * 2 + "Done, the summary is committed."))

    def test_повтор_в_начале_ответа_не_раньше_минимальной_длины(self):
        д = proxy.ДетекторПетли(min_words=10_000)
        self.assertIsNone(прогнать(ПЛАН * 8, д))

    def test_цепочка_чтений_с_разными_файлами_не_петля(self):
        текст = " ".join(f"Let me read the file src/m{i}.py to understand how it works."
                         for i in range(80))
        self.assertIsNone(прогнать(текст))

    def test_нумерованный_список_однотипных_пунктов_не_петля(self):
        текст = "\n".join(f"{i}. Добавить тест для модуля Module{i} в файл test_module{i}.py и "
                          f"прогнать его локально перед коммитом в ветку feature/m{i}"
                          for i in range(1, 60))
        self.assertIsNone(прогнать(текст))

    def test_таблица_с_одинаковыми_ячейками_не_петля(self):
        шапка = "| Шаг | Статус | Сборка | Тесты | Линт | Ревью |\n|---|---|---|---|---|---|\n"
        строки = "".join(f"| {i} | готово | да | да | да | да |\n" for i in range(1, 80))
        таблица_одинаковых = "| готово | готово | готово | готово | готово |\n" * 80
        self.assertIsNone(прогнать(шапка + строки))
        self.assertIsNone(прогнать(таблица_одинаковых))

    def test_код_в_заборе_не_считается(self):
        код = ("Вот тест:\n```python\n" + "        self.assertEqual(proxy.stats['loops_detected'], "
               "proxy.stats['loops_retried'] + 0)  # проверяем счётчик петли и повтора\n" * 60
               + "```\nГотово.")
        self.assertIsNone(прогнать(код))
        # Забор, разорванный между дельтами, всё равно распознаётся
        self.assertIsNone(прогнать(код, шаг=2))

    def test_код_вне_забора_с_повтором_это_петля(self):
        """Обратная сторона: та же строка ВНЕ забора — дословный повтор, предохранитель обязан."""
        строка = ("self.assertEqual(proxy.stats loops detected, proxy.stats loops retried) "
                  "проверяем счётчик петли и повтора в одном месте\n")
        self.assertIsNotNone(прогнать(ВСТУПЛЕНИЕ * 2 + строка * 40))

    def test_живой_документ_без_срабатываний(self):
        """Длинная живая проза с кодом, списками и таблицами: README прокси целиком."""
        with open(os.path.join(os.path.dirname(__file__), "README.md"), encoding="utf-8") as f:
            self.assertIsNone(прогнать(f.read()))

    def test_слова_без_букв_и_разметка_не_слова(self):
        д = proxy.ДетекторПетли()
        д.усвоить("| --- | ✅ | - * ## |\n")
        self.assertEqual(д.всего, 0)


def sse(тип, данные):
    return f"event: {тип}\ndata: {json.dumps(данные, ensure_ascii=False)}\n\n".encode()


def поток_ответа(блоки, stop_reason="end_turn", output_tokens=100, шаг=40):
    """SSE-поток ответа vLLM: блоки — [(вид, текст)], вид thinking | text | tool_use."""
    out = [sse("message_start", {"type": "message_start", "message": {
        "id": "chatcmpl-x", "type": "message", "role": "assistant", "content": [],
        "usage": {"input_tokens": 50, "output_tokens": 0}}})]
    for i, (вид, текст) in enumerate(блоки):
        if вид == "tool_use":
            out.append(sse("content_block_start", {"type": "content_block_start", "index": i,
                           "content_block": {"type": "tool_use", "id": "t1", "name": "Bash",
                                             "input": {}}}))
            for кусок in по_кусочкам(текст, шаг):
                out.append(sse("content_block_delta", {"type": "content_block_delta", "index": i,
                               "delta": {"type": "input_json_delta", "partial_json": кусок}}))
        else:
            out.append(sse("content_block_start", {"type": "content_block_start", "index": i,
                           "content_block": {"type": вид, вид: ""}}))
            поле, дельта = ("thinking", "thinking_delta") if вид == "thinking" else ("text", "text_delta")
            for кусок in по_кусочкам(текст, шаг):
                out.append(sse("content_block_delta", {"type": "content_block_delta", "index": i,
                               "delta": {"type": дельта, поле: кусок}}))
            if вид == "thinking":
                out.append(sse("content_block_delta", {"type": "content_block_delta", "index": i,
                               "delta": {"type": "signature_delta", "signature": "abc"}}))
        out.append(sse("content_block_stop", {"type": "content_block_stop", "index": i}))
    out.append(sse("message_delta", {"type": "message_delta",
                   "delta": {"stop_reason": stop_reason, "stop_sequence": None},
                   "usage": {"output_tokens": output_tokens}}))
    out.append(sse("message_stop", {"type": "message_stop"}))
    return b"".join(out)


def события(сырое):
    """Разбор SSE-потока клиента в список (тип, данные)."""
    итог = []
    for событие in сырое.decode().split("\n\n"):
        if not событие.strip():
            continue
        тип = данные = None
        for строка in событие.split("\n"):
            if строка.startswith("event:"):
                тип = строка[6:].strip()
            elif строка.startswith("data:"):
                данные = json.loads(строка[5:])
        итог.append((тип, данные))
    return итог


def проверить_целостность(тест, список):
    """Поток, который примет CLI: одно начало, блоки открыты-закрыты по порядку, конец в конце."""
    типы = [т for т, _ in список]
    тест.assertEqual(типы.count("message_start"), 1)
    тест.assertEqual(типы[0], "message_start")
    тест.assertEqual(типы[-1], "message_stop")
    тест.assertEqual(типы.count("message_stop"), 1)
    тест.assertEqual(типы.count("message_delta"), 1)
    тест.assertEqual(типы[-2], "message_delta")
    открыт, прежний = None, -1
    for т, д in список:
        if т == "content_block_start":
            тест.assertIsNone(открыт, "блок открыт, не закрыв прежний")
            тест.assertGreater(д["index"], прежний, "индексы блоков обязаны расти")
            открыт = прежний = д["index"]
        elif т == "content_block_delta":
            тест.assertEqual(д["index"], открыт)
        elif т == "content_block_stop":
            тест.assertEqual(д["index"], открыт)
            открыт = None
    тест.assertIsNone(открыт)


class РелеПотокаTests(unittest.TestCase):
    def прогнать(self, сырое, шаг=97, **kw):
        отдано = []
        реле = proxy.РелеПотока(отдано.append, **kw)
        петля = False
        for i in range(0, len(сырое), шаг):
            if реле.усвоить(сырое[i:i + шаг]):
                петля = True
                break
        if not петля:
            реле.дослать()
        return реле, b"".join(отдано)

    def test_без_петли_поток_байт_в_байт(self):
        сырое = поток_ответа([("thinking", ВСТУПЛЕНИЕ * 3), ("text", "Готово.")])
        реле, отдано = self.прогнать(сырое)
        self.assertFalse(реле.петля)
        self.assertEqual(отдано, сырое)

    def test_json_вызова_инструмента_не_смотрим(self):
        команда = json.dumps({"command": "echo " + "same same same same same " * 200})
        сырое = поток_ответа([("text", "Запускаю."), ("tool_use", команда)], stop_reason="tool_use")
        реле, отдано = self.прогнать(сырое)
        self.assertFalse(реле.петля)
        self.assertEqual(отдано, сырое)

    def test_петля_в_размышлениях_обрывает_поток_и_закрывает_блок(self):
        сырое = поток_ответа([("thinking", ВСТУПЛЕНИЕ + ПЛАН * 20), ("text", "Ответ")])
        реле, отдано = self.прогнать(сырое)
        self.assertTrue(реле.петля)
        self.assertEqual(реле.петля_в, "thinking")
        список = события(отдано)
        self.assertNotIn("text", [д["content_block"]["type"] for т, д in список
                                  if т == "content_block_start"], "после петли ничего не отдано")
        self.assertLess(отдано.decode().count("highest-value"), 4)

    def test_сдвиг_индексов_и_начало_повтора_не_пересылается(self):
        сырое = поток_ответа([("thinking", "short"), ("text", "Ответ")])
        _, отдано = self.прогнать(сырое, сдвиг=3, повтор=True)
        список = события(отдано)
        self.assertNotIn("message_start", [т for т, _ in список])
        self.assertEqual(sorted({д["index"] for т, д in список if т.startswith("content_block")}), [3, 4])

    def test_чужой_формат_уходит_насквозь(self):
        сырое = b"data: not json\n\n" + поток_ответа([("text", ПЛАН * 20)])
        реле, отдано = self.прогнать(сырое)
        self.assertTrue(реле.петля, "непонятное событие не выключает детектор на следующих")
        сырое = b"data: x" * 200_000  # без разделителя: сдаёмся и отдаём как есть
        журнал = io.StringIO()
        with contextlib.redirect_stderr(журнал):
            реле, отдано = self.прогнать(сырое, шаг=65536)
        self.assertTrue(реле.сквозной)
        self.assertEqual(отдано, сырое)

    def test_ошибка_детектора_не_ломает_ход(self):
        class Сломанный(proxy.ДетекторПетли):
            def усвоить(self, текст):
                raise RuntimeError("баг детектора")

        сырое = поток_ответа([("text", ПЛАН * 3)])
        with contextlib.redirect_stderr(io.StringIO()):
            реле, отдано = self.прогнать(сырое, детектор=Сломанный())
        self.assertTrue(реле.сквозной)
        self.assertEqual(отдано, сырое)

    def test_тело_повтора_сдвигает_сэмплинг(self):
        тело = json.dumps({"model": "m", "temperature": 0.7, "presence_penalty": 1.0,
                           "messages": []}).encode()
        doc = json.loads(proxy.retry_body(тело))
        self.assertEqual((doc["temperature"], doc["presence_penalty"]), (0.8, 1.5))
        doc = json.loads(proxy.retry_body(json.dumps({"presence_penalty": 1.8}).encode()))
        self.assertEqual(doc["presence_penalty"], 2.0, "потолок vLLM")
        self.assertEqual(doc["temperature"], 1.1, "без поля — от дефолта vLLM")
        self.assertIsNone(proxy.retry_body(b"not json"))

    def test_повтор_несёт_соль_а_исходное_тело_нет(self):
        тело = json.dumps({"model": "m", "messages": []}).encode()
        self.assertNotIn("cache_salt", json.loads(тело), "исходное тело — как есть")
        # Повтор: retry_body сдвигает сэмплинг, salted_body кладёт соль карантина.
        # uuid4 на каждый повтор убран (сирота KV) — соль теперь из карантина сессии.
        doc = json.loads(proxy.salted_body(proxy.retry_body(тело), "q-sess-1"))
        self.assertEqual(doc["cache_salt"], "q-sess-1", "повтор несёт соль карантина")
        # retry_body сам соль не добавляет: без salted_body cache_salt отсутствует.
        self.assertNotIn("cache_salt", json.loads(proxy.retry_body(тело)))
        # Тело не JSON — повтор не строится, соль не добавляется.
        self.assertIsNone(proxy.retry_body(b"not json"))
        self.assertIsNone(proxy.salted_body(b"not json", "q-sess-1"))


class ОбрывКлиентаTests(unittest.TestCase):
    """Обрыв клиента (BrokenPipe/ConnectionReset) не должен инкрементировать loop_guard_errors."""

    def setUp(self):
        self.сохранено = proxy.stats.get("loop_guard_errors", 0)
        proxy.stats["loop_guard_errors"] = 0

    def tearDown(self):
        proxy.stats["loop_guard_errors"] = self.сохранено

    def _прогнать_с_обрывом(self, ошибка, после_n=0):
        """Прогоняет поток через РелеПотока, где отдать бросает `ошибка` после `после_n` вызовов."""
        вызовы = [0]

        def отдать(сырое):
            вызовы[0] += 1
            if вызовы[0] > после_n:
                raise ошибка("клиент ушёл")

        реле = proxy.РелеПотока(отдать)
        сырое = поток_ответа([("text", "Привет")])
        for i in range(0, len(сырое), 97):
            try:
                if реле.усвоить(сырое[i:i + 97]):
                    break
            except (BrokenPipeError, ConnectionResetError):
                break
        return реле

    def test_connection_reset_на_первом_байте_не_считается(self):
        """Upstream-поток: ConnectionResetError на первом байте — loop_guard_errors не дёргается."""
        реле = self._прогнать_с_обрывом(ConnectionResetError, после_n=0)
        self.assertEqual(proxy.stats["loop_guard_errors"], 0,
                         "ConnectionResetError на первом байте не должна инкрементировать loop_guard_errors")

    def test_broken_pipe_после_первого_байта_не_считается(self):
        """Клиент рвёт после отдачи первого события — loop_guard_errors не дёргается."""
        реле = self._прогнать_с_обрывом(BrokenPipeError, после_n=1)
        self.assertEqual(proxy.stats["loop_guard_errors"], 0,
                         "BrokenPipeError после первого байта не должна инкрементировать loop_guard_errors")


class ПетляЗаглушка(BaseHTTPRequestHandler):
    """vLLM-заглушка: отдаёт по очереди заготовленные потоки (петля, нормальный ответ...)."""
    протокол_версия = "HTTP/1.1"
    ответы = []
    принятые = []
    оборваны = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        type(self).принятые.append(json.loads(self.rfile.read(n)))
        номер = len(type(self).принятые)
        поток = type(self).ответы.pop(0)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        try:
            for i in range(0, len(поток), 4096):
                self.wfile.write(поток[i:i + 4096])
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            type(self).оборваны.append(номер)
        self.close_connection = True


class ПетляСквознаяTests(unittest.TestCase):
    """CLI → прокси → vLLM-заглушка с петлёй → повтор → валидный ответ клиенту и карточка."""

    def setUp(self):
        class Модель(ПетляЗаглушка):
            ответы, принятые, оборваны = [], [], []

        class Бэкенд(БэкендЗаглушка):
            принятые = []

        self.Модель, self.Бэкенд = Модель, Бэкенд
        self.модель, self.бэкенд = поднять(Модель), поднять(Бэкенд)
        self.прокси = поднять(proxy.Handler)
        self.сохранено = (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM,
                          proxy.BACKEND_EVENTS_URL, proxy._event_secret, dict(proxy.stats),
                          proxy.LOOP_GUARD, proxy.LOOP_RETRY, proxy.SAMPLING)
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.модель.server_address[1]
        proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM = "off", ""
        proxy.BACKEND_EVENTS_URL = (f"http://127.0.0.1:{self.бэкенд.server_address[1]}"
                                    "/api/internal/llm-proxy/events")
        proxy._event_secret = "secret-from-config"
        proxy.LOOP_GUARD, proxy.LOOP_RETRY = "on", "on"
        proxy.SAMPLING = {"temperature": 0.7, "presence_penalty": 1.0}
        for к in ("loops_detected", "loops_retried", "loops_truncated", "loop_guard_errors"):
            proxy.stats[к] = 0

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM,
         proxy.BACKEND_EVENTS_URL, proxy._event_secret, стат,
         proxy.LOOP_GUARD, proxy.LOOP_RETRY, proxy.SAMPLING) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        for с in (self.модель, self.бэкенд, self.прокси):
            с.shutdown()
            с.server_close()

    def спросить(self, сессия="sess-loop", ждать_карточку=True):
        прежний = proxy._last_event_thread
        тело = json.dumps({"model": "qwen3.8-27b", "max_tokens": 8192, "stream": True,
                           "messages": [{"role": "user", "content": "найди хуки"}]}).encode()
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("POST", "/v1/messages", body=тело, headers={
            "Content-Type": "application/json", "Content-Length": str(len(тело)),
            proxy.EVENT_SESSION_HEADER: сессия})
        ответ = c.getresponse().read()
        c.close()
        срок = time.monotonic() + (5 if ждать_карточку else 0.3)
        while time.monotonic() < срок and proxy._last_event_thread is прежний:
            time.sleep(0.005)
        if proxy._last_event_thread is not прежний and proxy._last_event_thread is not None:
            proxy._last_event_thread.join(timeout=10)
        return события(ответ)

    def test_петля_в_тексте_перехвачена_и_повтор_приклеен(self):
        self.Модель.ответы = [
            # Петля длиннее буферов сокета: иначе заглушка успела бы дописать её до обрыва
            поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("thinking", "Search first."), ("text", "Нашёл хуки."),
                          ("tool_use", '{"command": "ls"}')], stop_reason="tool_use",
                         output_tokens=77)]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить()
        проверить_целостность(self, список)
        self.assertEqual([д["delta"]["stop_reason"] for т, д in список if т == "message_delta"],
                         ["tool_use"], "конец ответа — от повтора")
        виды = [д["content_block"]["type"] for т, д in список if т == "content_block_start"]
        self.assertEqual(виды, ["thinking", "text", "thinking", "text", "tool_use"])
        текст = "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")
        self.assertLess(текст.count("highest-value action"), 4, "петля оборвана рано")
        self.assertIn("Нашёл хуки.", текст)
        self.assertEqual(len(self.Модель.принятые), 2)
        повтор = self.Модель.принятые[1]
        self.assertEqual((повтор["temperature"], повтор["presence_penalty"]), (0.8, 1.5))
        self.assertEqual(self.Модель.оборваны, [1], "первая генерация остановлена обрывом")
        self.assertEqual((proxy.stats["loops_detected"], proxy.stats["loops_retried"],
                          proxy.stats["loops_truncated"]), (1, 1, 0))
        self.assertEqual(len(self.Бэкенд.принятые), 1)
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertEqual((событие["kind"], событие["sessionId"], событие["blocks"],
                          событие["resultBlocks"], событие["thinkingBlocks"]),
                         ("loop_guard", "sess-loop", 1, 1, 0))

    def test_повтор_несёт_соль_а_исходное_тело_нет(self):
        # Две последовательные петли той же сессии: повтор идёт с солью карантина,
        # исходное тело первого запроса — как есть; сессия и дальше ходит со своей солью.
        было = proxy.CACHE_QUARANTINE
        proxy.CACHE_QUARANTINE = "on"
        try:
            self.Модель.ответы = [
                поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
                поток_ответа([("text", "Нашёл хуки.")]),
                поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
                поток_ответа([("text", "Готово.")])]
            with contextlib.redirect_stderr(io.StringIO()):
                список = self.спросить()
                self.спросить()
            проверить_целостность(self, список)
            принятые = self.Модель.принятые
            self.assertEqual(len(принятые), 4, "два запроса, каждый с повтором")
            # [0] исходное тело первого запроса — без соли; [1] его повтор — с солью S_A.
            self.assertNotIn("cache_salt", принятые[0], "исходное тело #0 — как есть")
            соль_a = принятые[1]["cache_salt"]
            self.assertIsInstance(соль_a, str) and self.assertTrue(соль_a)
            # [2] второй запрос той же сессии: parse_body подставляет соль S_A из _карантин.
            self.assertEqual(принятые[2].get("cache_salt"), соль_a,
                             "сессия и дальше ходит со своей солью")
            # [3] повтор второго запроса — та же соль S_A: петля в уже засоленной сессии
            # соль не меняет, _карантин не перезаписан (новый префилл не тратится).
            соль_b = принятые[3]["cache_salt"]
            self.assertEqual(соль_b, соль_a, "повтор в засоленной сессии — та же соль")
            self.assertEqual(proxy._карантин["sess-loop"], соль_a,
                             "после второй петли _карантин держит прежнюю соль")
        finally:
            proxy.CACHE_QUARANTINE = было
            proxy._карантин.clear()

    def test_петля_в_размышлениях_закрыта_с_подписью(self):
        self.Модель.ответы = [поток_ответа([("thinking", ВСТУПЛЕНИЕ + ПЛАН * 30)]),
                              поток_ответа([("text", "Ответ.")])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить()
        проверить_целостность(self, список)
        подписи = [д for т, д in список if т == "content_block_delta"
                   and д["delta"]["type"] == "signature_delta" and д["index"] == 0]
        self.assertEqual(len(подписи), 1, "оборванное размышление закрыто с подписью")
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertEqual(событие["thinkingBlocks"], 1)

    def test_петля_и_в_повторе_ответ_усечён_с_пометкой(self):
        self.Модель.ответы = [поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)]),
                              поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить()
        проверить_целостность(self, список)
        self.assertEqual([д["delta"]["stop_reason"] for т, д in список if т == "message_delta"],
                         ["end_turn"])
        текст = "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")
        self.assertIn("модель зациклилась, повтор запроса тоже", текст)
        self.assertEqual((proxy.stats["loops_detected"], proxy.stats["loops_retried"],
                          proxy.stats["loops_truncated"]), (2, 1, 1))
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertEqual((событие["blocks"], событие["resultBlocks"]), (2, 1))

    def test_без_повтора_усечение_сразу(self):
        proxy.LOOP_RETRY = "off"
        self.Модель.ответы = [поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить()
        проверить_целостность(self, список)
        self.assertEqual(len(self.Модель.принятые), 1)
        текст = "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")
        self.assertIn("модель зациклилась]", текст)
        self.assertEqual(proxy.stats["loops_truncated"], 1)

    def test_выключатель_отдаёт_поток_как_есть(self):
        proxy.LOOP_GUARD = "off"
        сырое = поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)])
        self.Модель.ответы = [сырое]
        список = self.спросить(ждать_карточку=False)
        self.assertEqual(список, события(сырое))
        self.assertEqual(proxy.stats["loops_detected"], 0)
        self.assertEqual(self.Бэкенд.принятые, [])

    def test_обычный_ответ_без_изменений_и_карточки(self):
        сырое = поток_ответа([("thinking", ВСТУПЛЕНИЕ * 2), ("text", "Сделано.")])
        self.Модель.ответы = [сырое]
        self.assertEqual(self.спросить(ждать_карточку=False), события(сырое))
        self.assertEqual(len(self.Модель.принятые), 1)
        self.assertEqual(self.Бэкенд.принятые, [])

    def test_счётчики_видны_в_stats(self):
        self.Модель.ответы = [поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)]),
                              поток_ответа([("text", "Ок.")])]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить()
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("GET", "/__proxy/stats")
        отдано = json.loads(c.getresponse().read())
        c.close()
        self.assertEqual((отдано["loops_detected"], отдано["loops_retried"]), (1, 1))
        self.assertTrue(отдано["loop_guard"].startswith("on"))


# --- Предохранитель от мусорной генерации -----------------------------------------------------

# Живой мусор из транскриптов 15.10.2026 (vLLM #60174, DFlash2 + prefix cache).
МУСОР_1 = ("compressа应急arco1ács Vat�业务能力igel)compress国强ogaács Vat_VMelm压adium吐血戎elm真假 Wat可以放心\n"
           "compressadiumcompress压0戎ona (emmaasha_TA国强�磕火烧粗暴�BUS逆变压wcs_VM大方的._TAelmcompress "
           "Punch, Punch 应急arco�真假adiumarco压orius Vat� Vat 태磕火烧文化知识ule punching文化知识马蹄elm_VM "
           "compression\"压\"炊�_TAonarise_TA牛的Tagfit_TAácsadiumитель磕业务能力�adium太过 "
           "punchingács_TAcompress_TA\n")
МУСОР_2 = ("戎国强iringluancompress/comcompress压adiumcompress/comång\"compressadiumadiumитель\"compressadium牛 "
           "Tags: compress逆变ång\"compressadium逆变compressadium真假\"compresscompresscompress compression "
           "PunchTransferTransferCompress吐血compresscompress\n")
# Первый инцидент начался с осмысленных слов и деградировал постепенно.
МУСОР_НАЧАЛО = "Charles enough to understandadium the "

РУССКАЯ_ПРОЗА = (
    "Я посмотрел, как прокси разбирает поток ответа, и нашёл место, где удобно поставить второй "
    "предохранитель. Детектор петли уже режет дельты на слова, держит окно и не смотрит в код "
    "внутри заборов, поэтому новый детектор повторяет ту же нарезку. Порог выбран с запасом: в "
    "обычном ответе длинных склеек почти не бывает, а в мусоре их больше половины. Дальше прогоню "
    "тесты, проверю, что README не даёт срабатываний, и опишу пороги рядом с разделом про петли. "
    "Если что-то пойдёт не так, откат простой — выключатель в mode.conf и рестарт службы.\n")
АНГЛИЙСКАЯ_ПРОЗА = (
    "I traced the stream relay and found a natural place for the second guard. The loop detector "
    "already splits deltas into words, keeps a sliding window and skips fenced code, so the new "
    "detector reuses the same tokenisation. The threshold is generous: ordinary answers almost "
    "never contain long glued tokens, while the garbage is more than half of them. Next I will run "
    "the tests, make sure the README stays clean, and document the thresholds next to the loop "
    "section. Rollback is a single switch in mode.conf followed by a service restart.\n")
ПУТИ_И_ХЕШИ = "".join(
    f"Файл C:\\Users\\naych\\.claude\\plans\\ClaudeCodeServer\\plan-2026-10-{i:02d}-junk-guard.md, "
    f"копия https://github.com/example/ClaudeCodeServer/blob/master/tools/thinking-strip-proxy/proxy.py#L{i}, "
    f"sha256 {'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'}, "
    f"модуль ClaudeCodeServer.Llm, движок vLLM-0.31, под vLLM-ом, ключ API-ключ.\n" for i in range(1, 30))
BASE64_В_КОДЕ = ("Вот картинка в base64:\n```\n"
                 + "".join(base64.b64encode(bytes(range(i, i + 120))).decode() + "\n" for i in range(60))
                 + "```\nГотово, файл сохранён.\n")
JSON_ВЫЗОВА = ("Вызываю инструмент так:\n"
               + json.dumps({"name": "Read", "input": {
                   "file_path": "/home/an/Sources/ClaudeCodeServer/tools/thinking-strip-proxy/proxy.py",
                   "offset": 1295, "limit": 260}}, ensure_ascii=False) + "\n") * 20
КИТАЙСКИЙ = ("这是一个用于测试的中文句子，它不应该被认为是垃圾文本，因为每个词只使用一种文字。\n"
             "模型在长上下文中有时会输出混乱的字符，但正常的中文段落没有空格也是完全正常的。\n") * 40


def прогнать_мусор(текст, детектор=None, шаг=7):
    """Сколько слов детектор мусора успел увидеть до срабатывания; None — не сработал."""
    д = детектор or proxy.ДетекторМусора()
    for кусок in по_кусочкам(текст, шаг):
        if д.усвоить(кусок):
            return д.всего
    д.новый_блок()
    return д.всего if д.сработал else None


class ДетекторМусораTests(unittest.TestCase):
    def test_образцы_инцидента_ловятся_за_окно(self):
        вступление = ВСТУПЛЕНИЕ * 3
        до_мусора = len(вступление.split())
        for мусор in (МУСОР_1, МУСОР_2, МУСОР_НАЧАЛО + МУСОР_1 + МУСОР_2):
            with self.subTest(мусор=мусор[:30]):
                слов = прогнать_мусор(вступление + мусор * 40)
                self.assertIsNotNone(слов)
                self.assertLessEqual(слов - до_мусора, 80, "перехват обязан уложиться в окно")
                # Мусор с первого слова — ровно на минимальной длине ответа
                self.assertEqual(прогнать_мусор(мусор * 40), proxy.JUNK_MIN_WORDS)

    def test_обычный_текст_не_мусор(self):
        случаи = {"русская проза": РУССКАЯ_ПРОЗА * 8, "английская проза": АНГЛИЙСКАЯ_ПРОЗА * 8,
                  "пути, URL, хеши, идентификаторы": ПУТИ_И_ХЕШИ, "base64 в коде": BASE64_В_КОДЕ,
                  "JSON вызова": JSON_ВЫЗОВА, "китайский": КИТАЙСКИЙ,
                  "петля — не мусор": ВСТУПЛЕНИЕ + ПЛАН * 20}
        for имя, текст in случаи.items():
            with self.subTest(имя):
                self.assertGreaterEqual(len(текст.split()), proxy.JUNK_MIN_WORDS, "проверка вакуумна")
                self.assertIsNone(прогнать_мусор(текст))

    def test_живой_документ_без_срабатываний(self):
        with open(os.path.join(os.path.dirname(__file__), "README.md"), encoding="utf-8") as f:
            self.assertIsNone(прогнать_мусор(f.read()))

    def test_код_в_заборе_не_считается(self):
        текст = РУССКАЯ_ПРОЗА + "```text\n" + МУСОР_1 * 20 + "```\n" + РУССКАЯ_ПРОЗА
        self.assertIsNone(прогнать_мусор(текст))
        self.assertIsNone(прогнать_мусор(текст, шаг=2), "забор, разорванный между дельтами")
        # Тот же мусор вне забора — срабатывание: тест выше не вакуумный
        self.assertIsNotNone(прогнать_мусор(текст.replace("```", "")))

    def test_мусорное_слово(self):
        мусор = ["compressа应急arco1ács", "Vat_VMelm压adium吐血戎elm真假", "Vat�", "\ufffd",
                 "PunchTransferTransferCompress", "compresscompresscompresscompress."]
        норма = ["ClaudeCodeServer.Llm", "vLLM-0.31", "vLLM-ом", "PR'а", "`ДетекторМусораTests`",
                 "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                 "C:\\Users\\naych\\.claude\\plans\\ClaudeCodeServer\\plan.md",
                 "https://github.com/example/ClaudeCodeServer", "这是一个用于测试的中文句子，它不应该被认为是垃圾文本。",
                 "высокопроизводительный", "Punch,", "compression"]
        for w in мусор:
            self.assertTrue(proxy.мусорное_слово(w), w)
        for w in норма:
            self.assertFalse(proxy.мусорное_слово(w), w)


# Ход 17.10 17:53, дословно до вырождения: дальше сотни строк из одного «-» до конца хода.
ВЫРОЖДЕНИЕ = ("ImageEditor.Tests зависит-зависит от `FakeImageEditor` (monoлит, "
              "`ImageEditor/FakeImageEditor.cs`) и `ImageEditorFakes.cs`. Про-типов. Про-ов-\nПроверя-\n")


class ЦепочкаБезБуквTests(unittest.TestCase):
    """Второй признак мусора: run одинаковых «слов» без букв и цифр подряд."""

    def test_вырождение_инцидента_ловится(self):
        текст = РУССКАЯ_ПРОЗА + ВЫРОЖДЕНИЕ + "-\n" * 150
        д = proxy.ДетекторМусора()
        self.assertIsNotNone(прогнать_мусор(текст, д))
        self.assertTrue(д.причина.startswith(f"цепочка {proxy.JUNK_RUN}×"), д.причина)
        # Доля мусорных слов тут ни при чём: без второго признака детектор молчит
        self.assertIsNone(прогнать_мусор(текст, proxy.ДетекторМусора(run=10 ** 6)))

    def test_повтор_одного_слова_ловится(self):
        # 17.10 22:38: размышление ушло в «like like like…» до среза бюджета, ответ — в тысячи «like»
        д = proxy.ДетекторМусора()
        self.assertIsNotNone(прогнать_мусор(РУССКАЯ_ПРОЗА + "Program.cs`-у`-у` " + "like " * 100, д))
        self.assertTrue(д.причина.startswith(f"цепочка {proxy.JUNK_RUN}× 'like'"), д.причина)
        self.assertIsNotNone(прогнать_мусор("Like, LIKE like. " * 30), "без регистра и пунктуации")

    def test_числа_и_обычные_повторы_не_цепочка(self):
        for имя, текст in {"нули матрицы": "0 " * 300, "единицы": "1, " * 300,
                           "очень-очень": (РУССКАЯ_ПРОЗА + " очень очень очень ") * 10}.items():
            with self.subTest(имя):
                self.assertIsNone(прогнать_мусор(текст))

    def test_цепочка_короче_порога_не_мусор(self):
        self.assertIsNone(прогнать_мусор(РУССКАЯ_ПРОЗА + "-\n" * (proxy.JUNK_RUN - 1) + РУССКАЯ_ПРОЗА))

    def test_разметка_не_цепочка(self):
        строки = 3 * proxy.JUNK_RUN
        случаи = {
            "список": "".join(f"- пункт {i}\n" for i in range(строки)),
            "таблица с пустыми ячейками": "| a | b | c |\n|---|---|---|\n" + "| - | - | - |\n" * строки,
            "дерево": "".join(f"│   ├── файл{i}.cs\n" for i in range(строки)),
            "разделители между абзацами": (РУССКАЯ_ПРОЗА + "\n---\n") * 10,
            "цепочка в заборе кода": "```\n" + "-\n" * строки + "```\n" + РУССКАЯ_ПРОЗА,
        }
        for имя, текст in случаи.items():
            with self.subTest(имя):
                self.assertIsNone(прогнать_мусор(текст))


class МусорСквознойTests(unittest.TestCase):
    """CLI → прокси → vLLM-заглушка с мусором → обрыв без повтора, пометка и счётчики."""

    спросить = ПетляСквознаяTests.спросить

    def setUp(self):
        ПетляСквознаяTests.setUp(self)
        self.мусор_был = proxy.JUNK_GUARD, proxy.GUARD_DUMP_DIR, proxy.GUARD_DUMP_KEEP
        proxy.JUNK_GUARD = "on"
        self.дампы = os.path.join(tempfile.mkdtemp(), "guard")
        proxy.GUARD_DUMP_DIR = self.дампы
        proxy.stats["junk_aborts"], proxy.stats["junk_words_max_ratio"] = 0, 0.0
        proxy.stats["guard_dumps"] = 0

    def tearDown(self):
        proxy.JUNK_GUARD, proxy.GUARD_DUMP_DIR, proxy.GUARD_DUMP_KEEP = self.мусор_был
        shutil.rmtree(os.path.dirname(self.дампы), ignore_errors=True)
        ПетляСквознаяTests.tearDown(self)

    def файлы_дампа(self):
        return sorted(os.listdir(self.дампы)) if os.path.isdir(self.дампы) else []

    def мета_дампа(self):
        тела = [f for f in self.файлы_дампа() if f.endswith(".req.json.gz")]
        self.assertEqual(len(тела), 1, self.файлы_дампа())
        основа = os.path.join(self.дампы, тела[0][:-len(".req.json.gz")])
        with gzip.open(основа + ".req.json.gz") as f:
            тело = json.loads(f.read())
        with open(основа + ".meta.json", encoding="utf-8") as f:
            мета = json.load(f)
        for путь in (основа + ".req.json.gz", основа + ".meta.json"):
            self.assertEqual(os.stat(путь).st_mode & 0o777, 0o600, путь)
        self.assertEqual(os.stat(self.дампы).st_mode & 0o777, 0o700)
        return тело, мета

    def test_дамп_сработки_мусора(self):
        self.Модель.ответы = [self.мусорный_поток()]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        тело, мета = self.мета_дампа()
        self.assertEqual(тело, self.Модель.принятые[0], "в дампе ровно то, что ушло в движок")
        self.assertEqual((мета["guard"], мета["path"], мета["session"]), ("мусор", "/v1/messages", "sess-loop"))
        self.assertTrue(мета["reason"].startswith("доля"), мета["reason"])
        self.assertEqual(proxy.stats["guard_dumps"], 1)

    def test_дамп_сработки_вырождения(self):
        self.Модель.ответы = [поток_ответа([("thinking", "Plan quickly."),
                                            ("text", ВСТУПЛЕНИЕ + ВЫРОЖДЕНИЕ + "-\n" * 300000)])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        текст = "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")
        self.assertIn("[прокси: ответ оборван — модель выдаёт мусор. Повтори ход.]", текст)
        _, мета = self.мета_дампа()
        self.assertTrue(мета["reason"].startswith("цепочка"), мета["reason"])
        self.assertEqual(len(self.Модель.принятые), 1, "повтора нет")

    def test_дамп_сработки_петли(self):
        self.Модель.ответы = [поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)]),
                              поток_ответа([("text", РУССКАЯ_ПРОЗА)])]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        тело, мета = self.мета_дампа()
        self.assertEqual(тело, self.Модель.принятые[0], "дамп первой попытки, не повтора")
        self.assertEqual((мета["guard"], мета["block"]), ("петля", "text"))
        self.assertTrue(мета["reason"])

    def test_без_сработки_дампа_нет(self):
        self.Модель.ответы = [поток_ответа([("text", РУССКАЯ_ПРОЗА)])]
        self.спросить(ждать_карточку=False)
        self.assertEqual(self.файлы_дампа(), [])
        self.assertEqual(proxy.stats["guard_dumps"], 0)

    def test_дамп_выключен_без_каталога(self):
        proxy.GUARD_DUMP_DIR = ""
        self.Модель.ответы = [self.мусорный_поток()]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        self.assertFalse(os.path.exists(self.дампы))
        self.assertEqual(proxy.stats["junk_aborts"], 1, "обрыв работает и без дампа")

    def test_хранятся_только_свежие(self):
        proxy.GUARD_DUMP_KEEP = 2
        for i in range(4):
            proxy.dump_guard(json.dumps({"n": i}).encode(), "s", {"guard": "мусор"})
            time.sleep(0.02)
        тела = [f for f in self.файлы_дампа() if f.endswith(".req.json.gz")]
        self.assertEqual(len(тела), 2)
        self.assertEqual(len(self.файлы_дампа()), 4, "у каждого тела своя мета, сироты удалены")
        номера = sorted(json.loads(gzip.open(os.path.join(self.дампы, f)).read())["n"] for f in тела)
        self.assertEqual(номера, [2, 3])

    def мусорный_поток(self):
        # Мусор длиннее буферов сокета (~2,5 МБ): иначе заглушка успела бы дописать его до обрыва
        return поток_ответа([("thinking", "Plan quickly."),
                             ("text", ВСТУПЛЕНИЕ * 3 + МУСОР_НАЧАЛО + (МУСОР_1 + МУСОР_2) * 1000)],
                            stop_reason="max_tokens", output_tokens=20000)

    def test_мусор_оборван_без_повтора(self):
        self.Модель.ответы = [self.мусорный_поток()]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        self.assertEqual([д["delta"]["stop_reason"] for т, д in список if т == "message_delta"],
                         ["end_turn"])
        текст = "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")
        self.assertIn("[прокси: ответ оборван — модель выдаёт мусор. Повтори ход.]", текст)
        self.assertLess(текст.count("PunchTransfer"), 20, "мусор оборван рано")
        self.assertEqual(len(self.Модель.принятые), 1, "повтора нет")
        # Обрыв заглушка замечает на следующей записи в сокет — уже после ответа клиенту
        срок = time.monotonic() + 5
        while not self.Модель.оборваны and time.monotonic() < срок:
            time.sleep(0.005)
        self.assertEqual(self.Модель.оборваны, [1], "генерация остановлена обрывом")
        self.assertEqual(proxy.stats["junk_aborts"], 1)
        self.assertGreaterEqual(proxy.stats["junk_words_max_ratio"], proxy.JUNK_RATIO)
        self.assertEqual(proxy.stats["loops_detected"], 0)

    def test_счётчики_видны_в_stats(self):
        self.Модель.ответы = [self.мусорный_поток()]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("GET", "/__proxy/stats")
        отдано = json.loads(c.getresponse().read())
        c.close()
        self.assertEqual(отдано["junk_aborts"], 1)
        self.assertGreaterEqual(отдано["junk_words_max_ratio"], 0.35)
        self.assertTrue(отдано["junk_guard"].startswith("on"))

    def test_петля_без_своего_выключателя_не_ловится(self):
        """JUNK_GUARD=on при LOOP_GUARD=off: реле есть, но детектор петли в нём выключен."""
        proxy.LOOP_GUARD = "off"
        сырое = поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 30)])
        self.Модель.ответы = [сырое]
        self.assertEqual(self.спросить(ждать_карточку=False), события(сырое))
        self.assertEqual((proxy.stats["loops_detected"], proxy.stats["junk_aborts"]), (0, 0))

    def test_обычный_ответ_без_изменений(self):
        # Без дословных повторов: предохранитель петли в этом наборе тоже включён
        сырое = поток_ответа([("thinking", АНГЛИЙСКАЯ_ПРОЗА),
                              ("text", РУССКАЯ_ПРОЗА + ПУТИ_И_ХЕШИ + BASE64_В_КОДЕ)])
        self.Модель.ответы = [сырое]
        self.assertEqual(self.спросить(ждать_карточку=False), события(сырое))
        self.assertEqual((proxy.stats["junk_aborts"], proxy.stats["loops_detected"]), (0, 0))


# --- Прунинг картинок ---------------------------------------------------------------------

import base64
import struct


def png(w, h):
    return b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + b"IHDR" + struct.pack(">II", w, h) + b"\x08\x02\x00\x00\x00" + b"\x00" * 64


def jpeg(w, h, exif=0, сегментов=1):
    app1 = (b"\xff\xe1" + struct.pack(">H", exif + 2) + b"\x00" * exif) * сегментов
    sof = b"\xff\xc0" + struct.pack(">HBHHB", 17, 8, h, w, 3) + b"\x00" * 9
    return b"\xff\xd8" + app1 + b"\xff\xdb\x00\x04\x00\x00" + sof + b"\xff\xda"


def webp_vp8(w, h):
    return (b"RIFF" + b"\x00" * 4 + b"WEBP" + b"VP8 " + b"\x00" * 4 + b"\x00" * 3
            + b"\x9d\x01\x2a" + struct.pack("<HH", w, h) + b"\x00" * 16)


def webp_vp8l(w, h):
    биты = (w - 1) | ((h - 1) << 14)
    return b"RIFF" + b"\x00" * 4 + b"WEBP" + b"VP8L" + b"\x00" * 4 + b"\x2f" + struct.pack("<I", биты) + b"\x00" * 16


def webp_vp8x(w, h):
    return (b"RIFF" + b"\x00" * 4 + b"WEBP" + b"VP8X" + b"\x00" * 8
            + (w - 1).to_bytes(3, "little") + (h - 1).to_bytes(3, "little") + b"\x00" * 16)


def картинка(w=1920, h=1080, сырьё=None):
    данные = base64.b64encode(сырьё if сырьё is not None else png(w, h)).decode()
    return {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": данные}}


def скриншот(i, w=1920, h=1080):
    """Пара «Read скриншота → вывод с картинкой», как её шлёт CLI."""
    return [{"role": "assistant", "content": [
                {"type": "tool_use", "id": f"r{i}", "name": "Read", "input": {"file_path": f"/s{i}.png"}}]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": f"r{i}", "content": [картинка(w, h)]}]}]


def чат_со_скриншотами(n, человек=None):
    msgs = [{"role": "user", "content": [{"type": "text", "text": "проверь страницу"}]
             + ([картинка(800, 600)] * человек if человек else [])}]
    for i in range(n):
        msgs += скриншот(i)
    return msgs


def картинок(msgs):
    n = 0
    for m in msgs:
        for b in m["content"] if isinstance(m["content"], list) else []:
            if b.get("type") == "image":
                n += 1
            elif b.get("type") == "tool_result" and isinstance(b.get("content"), list):
                n += sum(1 for x in b["content"] if x.get("type") == "image")
    return n


class РазмерКартинкиTests(unittest.TestCase):
    def test_png(self):
        self.assertEqual(proxy.image_size(png(1920, 1080)), (1920, 1080))

    def test_jpeg_с_exif(self):
        self.assertEqual(proxy.image_size(jpeg(1024, 768, exif=500)), (1024, 768))

    def test_jpeg_с_крупным_exif_за_пределами_префикса(self):
        блок = картинка(сырьё=jpeg(640, 480, exif=65000, сегментов=2) + b"\x00" * 200000)
        self.assertEqual(proxy._image_dims(блок), (640, 480))

    def test_webp_все_три_вида(self):
        self.assertEqual(proxy.image_size(webp_vp8(800, 600)), (800, 600))
        self.assertEqual(proxy.image_size(webp_vp8l(1000, 700)), (1000, 700))
        self.assertEqual(proxy.image_size(webp_vp8x(3000, 2000)), (3000, 2000))

    def test_мусор_и_ссылка(self):
        self.assertIsNone(proxy.image_size(b"not an image at all"))
        self.assertIsNone(proxy._image_dims({"type": "image", "source": {"type": "url", "url": "x"}}))
        self.assertIsNone(proxy._image_dims(картинка(сырьё=b"\x00" * 40)))

    def test_оценка_токенов_по_замерам(self):
        self.assertEqual(proxy.image_tokens((512, 512)), 256)    # замер 258
        self.assertEqual(proxy.image_tokens((1024, 768)), 768)   # замер 770
        self.assertEqual(proxy.image_tokens((1920, 1080)), 2040)  # замер 2042
        self.assertEqual(proxy.image_tokens((4000, 3000)), proxy.IMAGE_TOKENS_CAP)
        self.assertEqual(proxy.image_tokens(None), proxy.IMAGE_TOKENS_CAP)


class ПрунингКартинокTests(unittest.TestCase):
    def test_без_картинок_ничего_не_меняется(self):
        msgs = history(30)
        out, сводка = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertIs(out, msgs)
        self.assertEqual(сводка, {"pruned": 0, "kept": 0, "tokens": 0})

    def test_картинок_не_больше_keep_ничего_не_меняется(self):
        msgs = чат_со_скриншотами(4)
        снимок = json.dumps(msgs)
        out, сводка = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertIs(out, msgs)
        self.assertEqual(json.dumps(out), снимок)
        self.assertEqual(сводка["kept"], 4)

    def test_старые_вырезаются_свежие_остаются(self):
        msgs = чат_со_скриншотами(10)
        снимок = json.dumps(msgs)
        out, сводка = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertEqual(json.dumps(msgs), снимок, "вход не мутирован")
        self.assertEqual((сводка["pruned"], сводка["kept"]), (6, 4))
        self.assertEqual(сводка["tokens"], 6 * 2040)
        self.assertEqual(картинок(out), 4)
        # старейшие — заглушки той же формы (текстовый блок внутри tool_result)
        первый = out[2]["content"][0]
        self.assertEqual(первый["type"], "tool_result")
        self.assertEqual(первый["tool_use_id"], "r0")
        self.assertEqual(первый["content"],
                         [{"type": "text", "text": "[картинка 1920×1080, ~2040 токенов, вырезана "
                                                   "прокси — открой заново, если нужна]"}])
        # свежие — те же объекты, байт в байт
        for i in range(6, 10):
            self.assertIs(out[2 + 2 * i], msgs[2 + 2 * i])

    def test_картинки_последнего_сообщения_человека_не_трогаются(self):
        msgs = чат_со_скриншотами(10)
        msgs.append({"role": "user", "content": [{"type": "text", "text": "а вот так?"},
                                                  картинка(800, 600), картинка(800, 600)]})
        out, сводка = proxy.prune_images(msgs, keep=1, max_images=12)
        self.assertIs(out[-1], msgs[-1])
        self.assertEqual(сводка["kept"], 2, "две картинки человека, свежайшая из них же")
        self.assertEqual(сводка["pruned"], 10)

    def test_человек_защищён_весь_ход_агента(self):
        """Человек приложил картинку, дальше агент сделал шаги — она всё ещё «его»."""
        msgs = [{"role": "user", "content": [{"type": "text", "text": "вот макет"}, картинка(800, 600)]}]
        for i in range(8):
            msgs += скриншот(i)
        out, сводка = proxy.prune_images(msgs, keep=2, max_images=12)
        self.assertIs(out[0], msgs[0])
        self.assertEqual((сводка["pruned"], сводка["kept"]), (6, 3))

    def test_прошлое_сообщение_человека_не_защищено(self):
        msgs = чат_со_скриншотами(0, человек=1)
        for i in range(6):
            msgs += скриншот(i)
        msgs.append({"role": "user", "content": "дальше"})
        out, сводка = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertEqual(out[0]["content"][1]["type"], "text")
        self.assertEqual(сводка["pruned"], 3)

    def test_потолок_режет_даже_картинки_человека(self):
        msgs = чат_со_скриншотами(0, человек=20)
        out, сводка = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertEqual(картинок(out), 12)
        self.assertEqual((сводка["pruned"], сводка["kept"]), (8, 12))
        # режутся старейшие: первые 8 картинок сообщения
        блоки = out[0]["content"]
        self.assertTrue(all(b["type"] == "text" for b in блоки[1:9]))
        self.assertTrue(all(b["type"] == "image" for b in блоки[9:]))

    def test_потолок_при_большом_keep(self):
        out, сводка = proxy.prune_images(чат_со_скриншотами(40), keep=100, max_images=12)
        self.assertEqual(картинок(out), 12)

    def test_детерминизм_между_запросами(self):
        """Вырезанное остаётся вырезанным: общий префикс двух соседних ходов совпадает байт в
        байт до позиции, где вырезана новая картинка, — промах кэша один раз, а не каждый ход."""
        прошлый, _ = proxy.prune_images(чат_со_скриншотами(10), keep=4, max_images=12)
        текущий, _ = proxy.prune_images(чат_со_скриншотами(10), keep=4, max_images=12)
        self.assertEqual(json.dumps(прошлый), json.dumps(текущий))
        следующий, _ = proxy.prune_images(чат_со_скриншотами(11), keep=4, max_images=12)
        # первые 6 скриншотов были вырезаны и остались вырезанными бит в бит
        for i in range(1 + 2 * 6):
            self.assertEqual(json.dumps(прошлый[i]), json.dumps(следующий[i]))
        # уже вырезанная история не режется повторно и не меняется
        ещё, сводка = proxy.prune_images(следующий, keep=4, max_images=12)
        self.assertIs(ещё, следующий)
        self.assertEqual(сводка["pruned"], 0)

    def test_вывод_с_текстом_и_картинкой(self):
        msgs = чат_со_скриншотами(5)
        msgs[2]["content"][0]["content"].insert(0, {"type": "text", "text": "подпись"})
        out, _ = proxy.prune_images(msgs, keep=4, max_images=12)
        self.assertEqual(out[2]["content"][0]["content"][0], {"type": "text", "text": "подпись"})
        self.assertEqual(out[2]["content"][0]["content"][1]["type"], "text")
        self.assertEqual(msgs[2]["content"][0]["content"][1]["type"], "image", "вход не мутирован")


class КартинкиСквозныеTests(unittest.TestCase):
    """Тот же стенд, что у карточки: CLI → прокси → заглушка модели, событие на бэкенд."""
    setUp_базовый = СобытиеСквозноеTests.setUp
    tearDown_базовый = СобытиеСквозноеTests.tearDown
    спросить = СобытиеСквозноеTests.спросить

    def setUp(self):
        self.setUp_базовый()
        self.картинки_было = (proxy.IMAGE_PRUNE, proxy.IMAGE_KEEP, proxy.IMAGE_MAX, dict(proxy._image_cuts))
        proxy.IMAGE_PRUNE, proxy.IMAGE_KEEP, proxy.IMAGE_MAX = "on", 4, 12
        proxy._image_cuts.clear()
        proxy.stats.update(images_pruned=0, images_kept=0, image_tokens_saved=0)

    def tearDown(self):
        proxy.IMAGE_PRUNE, proxy.IMAGE_KEEP, proxy.IMAGE_MAX, разрезы = self.картинки_было
        proxy._image_cuts.clear()
        proxy._image_cuts.update(разрезы)
        self.tearDown_базовый()

    def test_тело_без_картинок_уходит_байт_в_байт(self):
        proxy.PRUNE_MODE = "off"
        msgs = history(5)
        тело = json.dumps({"model": "qwen3.8-27b", "max_tokens": 8192, "messages": msgs}).encode()
        self.спросить(msgs, ждать_карточку=False)
        self.assertEqual(self.Модель.принятые[-1][1], тело)

    def test_чат_с_двадцатью_скриншотами(self):
        self.спросить(чат_со_скриншотами(25))
        _, тело, _ = self.Модель.принятые[-1]
        self.assertEqual(картинок(json.loads(тело)["messages"]), 4)
        self.assertEqual(len(self.Бэкенд.принятые), 1)
        _, событие, _ = self.Бэкенд.принятые[0]
        self.assertEqual((событие["kind"], событие["imageBlocks"], событие["imageTokens"]),
                         ("prune", 21, 21 * 2040))
        self.assertEqual(событие["blocks"], 0)
        self.assertEqual(proxy.stats["images_pruned"], 21)

    def test_карточка_только_на_новом_вырезании(self):
        self.спросить(чат_со_скриншотами(10))
        self.спросить(чат_со_скриншотами(10), ждать_карточку=False)
        self.assertEqual(len(self.Бэкенд.принятые), 1)
        self.спросить(чат_со_скриншотами(11))
        self.assertEqual(len(self.Бэкенд.принятые), 2)
        self.assertEqual(self.Бэкенд.принятые[1][1]["imageBlocks"], 7)

    def test_выключено_картинки_едут_как_есть(self):
        proxy.IMAGE_PRUNE = "off"
        self.спросить(чат_со_скриншотами(20), ждать_карточку=False)
        _, тело, _ = self.Модель.принятые[-1]
        self.assertEqual(картинок(json.loads(тело)["messages"]), 20)

    def test_счётчики_в_stats(self):
        self.спросить(чат_со_скриншотами(6))
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=10)
        c.request("GET", "/__proxy/stats")
        отдано = json.loads(c.getresponse().read())
        c.close()
        self.assertEqual((отдано["images_pruned"], отдано["images_kept"], отдано["image_tokens_saved"]),
                         (2, 4, 2 * 2040))
        self.assertTrue(отдано["image_prune"].startswith("on"))


class СжатиеЗаглушка(BaseHTTPRequestHandler):
    """Upstream ответа сжатия: отдаёт заготовленное тело с заданными статусом и типом."""
    протокол_версия = "HTTP/1.1"
    статус = 200
    вид = "text/event-stream"
    тело = b""
    принятые = []
    заголовки = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        type(self).принятые.append(json.loads(self.rfile.read(n)))
        type(self).заголовки.append({k.lower(): v for k, v in self.headers.items()})
        тело = type(self).тело
        self.send_response(type(self).статус)
        self.send_header("Content-Type", type(self).вид)
        self.send_header("Content-Length", str(len(тело)))
        self.end_headers()
        # Мелкими кусками: событие режется посреди, вставка обязана склеивать.
        try:
            for i in range(0, len(тело), 97):
                self.wfile.write(тело[i:i + 97])
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass  # прокси бросил ответ-ошибку облака, прочитав начало, — так и задумано


def проверить_индексы(тест, список):
    """Индексы блоков у клиента — 0, 1, 2… без дыр и повторов."""
    индексы = [д["index"] for т, д in список if т == "content_block_start"]
    тест.assertEqual(индексы, list(range(len(индексы))))


def как_cli(список):
    """Что CLI 2.1.301 положит в запись isCompactSummary из потока ответа сжатия.

    Повторяет бинарник: поток раскладывается по блокам в отдельные сообщения; `KEe` берёт
    последнее сообщение с текстом, где есть `<summary>` (иначе последнее с текстом); `QEe` —
    первый текстовый блок, `trim`; `BWr` вырезает <analysis> и меняет <summary> на «Summary:».
    """
    import re
    блоки, открыт = [], None
    for т, д in список:
        if т == "content_block_start":
            открыт = dict(д["content_block"])
            блоки.append(открыт)
        elif т == "content_block_delta" and открыт is not None and открыт.get("type") == "text":
            открыт["text"] = открыт.get("text", "") + д["delta"].get("text", "")
        elif т == "content_block_stop":
            открыт = None
    тексты = [б["text"] for б in блоки if б.get("type") == "text"]
    выбран = next((т for т in reversed(тексты) if "<summary>" in т), тексты[-1] if тексты else None)
    if выбран is None or not выбран.strip():
        return None
    т = re.sub(r"<analysis>[\s\S]*?</analysis>", "", выбран.strip(), count=1)
    т = re.sub(r"<summary>([\s\S]*?)</summary>", lambda m: "Summary:\n" + m.group(1).strip(), т,
               count=1)
    return re.sub(r"\n\n+", "\n\n", т).strip()


class СохранениеДословногоTests(unittest.TestCase):
    """COMPACT_PRESERVE: <ccs-preserved> в конце текстового блока модели со сводкой."""

    def setUp(self):
        class Облако(СжатиеЗаглушка):
            принятые, заголовки = [], []

        class Локальный(СжатиеЗаглушка):
            принятые, заголовки = [], []

        self.Облако, self.Локальный = Облако, Локальный
        self.облако, self.локальный = поднять(Облако), поднять(Локальный)
        self.прокси = поднять(proxy.Handler)
        self.сохранено = (proxy.UP_HOST, proxy.UP_PORT, proxy.COMPACT_UPSTREAM, proxy._compact_key,
                          proxy.COMPACT_PRESERVE, proxy.PRUNE_MODE, proxy.LOOP_GUARD,
                          proxy.JUNK_GUARD, proxy.SAMPLING, dict(proxy.stats))
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.локальный.server_address[1]
        proxy.COMPACT_UPSTREAM = f"http://127.0.0.1:{self.облако.server_address[1]}"
        proxy._compact_key = "sk-test"
        proxy.COMPACT_PRESERVE = "on"
        proxy.PRUNE_MODE, proxy.LOOP_GUARD, proxy.JUNK_GUARD = "off", "off", "off"
        proxy.SAMPLING = {}
        for к in ("compact_routed", "compact_fallback", "compact_preserved",
                  "compact_preserve_failed"):
            proxy.stats[к] = 0
        # Модель отвечает размышлением на index 0 и сводкой на index 1 — как MiniMax.
        self.поток = поток_ответа([("thinking", "Let me analyze."),
                                   ("text", "<analysis>a</analysis>\n<summary>сводка</summary>")])
        self.Облако.тело = self.Локальный.тело = self.поток

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.COMPACT_UPSTREAM, proxy._compact_key,
         proxy.COMPACT_PRESERVE, proxy.PRUNE_MODE, proxy.LOOP_GUARD, proxy.JUNK_GUARD,
         proxy.SAMPLING, стат) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        for с in (self.облако, self.локальный, self.прокси):
            с.shutdown()
            с.server_close()

    def запрос(self):
        msgs = history(120)
        msgs.append({"role": "user", "content": proxy.COMPACT_SIGNATURE + " Summarize."})
        return {"model": "qwen3.8-27b", "max_tokens": 8192, "stream": True, "messages": msgs}

    def спросить(self, doc, заголовки=None):
        тело = json.dumps(doc).encode()
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        self.журнал = io.StringIO()
        with contextlib.redirect_stderr(self.журнал):
            c.request("POST", "/v1/messages", body=тело, headers=dict({
                "Content-Type": "application/json", "Content-Length": str(len(тело))},
                **(заголовки or {})))
            ответ = c.getresponse().read()
        c.close()
        return ответ

    def ожидаемый_блок(self, doc):
        # Последнее сообщение в этих тестах — чистая инструкция сжатия: блок по истории без неё.
        return "\n\n" + proxy.compactor.build_preserved(dict(doc, messages=doc["messages"][:-1]))

    def проверить_вставку(self, ответ, блок):
        список = события(ответ)
        проверить_целостность(self, список)
        проверить_индексы(self, список)
        модель = события(self.поток)
        # Ровно одно лишнее событие — дельта с блоком перед стопом текстового блока (index 1).
        self.assertEqual(len(список), len(модель) + 1)
        стоп = список.index(("content_block_stop", {"type": "content_block_stop", "index": 1}))
        self.assertEqual(список[стоп - 1], ("content_block_delta", {
            "type": "content_block_delta", "index": 1,
            "delta": {"type": "text_delta", "text": блок}}))
        self.assertEqual(список[:стоп - 1] + список[стоп:], модель,
                         "остальной поток модели — как есть, индексы не тронуты")
        # Главное: блок доживает до записи сжатия CLI.
        запись = как_cli(список)
        self.assertTrue(запись.startswith("Summary:\nсводка"))
        self.assertTrue(запись.endswith(блок.strip()))
        self.assertIn("<ccs-preserved>", запись)

    def последняя_реплика(self, принятое):
        return принятое["messages"][-1]["content"]

    def test_облако_блок_дописан_к_сводке(self):
        doc = self.запрос()
        блок = self.ожидаемый_блок(doc)
        self.assertIn("<ccs-preserved>", блок)
        ответ = self.спросить(doc)
        self.проверить_вставку(ответ, блок)
        self.assertEqual(proxy.stats["compact_routed"], 1)
        self.assertEqual((proxy.stats["compact_preserved"], proxy.stats["compact_preserve_failed"]),
                         (1, 0))
        реплика = self.последняя_реплика(self.Облако.принятые[-1])
        self.assertEqual(реплика, proxy.COMPACT_SIGNATURE + " Summarize.\n\n"
                         + proxy.compactor.instruction_addendum())
        # Середина истории не тронута — в облако едет сырая история.
        self.assertEqual(self.Облако.принятые[-1]["messages"][:-1], doc["messages"][:-1])

    def test_инструкция_сжатия_не_попадает_в_блок(self):
        """Живая проверка 2026-10-16: без вырезки инструкция CLI шла в «Реплики человека»."""
        doc = self.запрос()
        doc["messages"].pop()  # своя инструкция ниже — формой списка
        doc["messages"].append({"role": "assistant", "content": "ок"})
        doc["messages"].append({"role": "user", "content": "свежая реплика человека"})
        doc["messages"].append({"role": "user", "content": [
            {"type": "text", "text": proxy.COMPACT_SIGNATURE + " Your task is to create a summary."}]})
        запись = как_cli(события(self.спросить(doc)))
        блок = запись[запись.index("<ccs-preserved>"):]
        self.assertNotIn(proxy.COMPACT_SIGNATURE, блок)
        self.assertNotIn("Your task is to create", блок)
        self.assertIn("свежая реплика человека", блок)

    def test_вырезка_инструкции_оставляет_прочее(self):
        msgs = [{"role": "user", "content": "задача"},
                {"role": "user", "content": [
                    {"type": "tool_result", "tool_use_id": "t", "content": "вывод"},
                    {"type": "text", "text": proxy.COMPACT_SIGNATURE + " x"}]}]
        снимок = copy.deepcopy(msgs)
        итог = proxy._без_инструкции_сжатия(msgs)
        self.assertEqual(msgs, снимок, "вход не мутирует")
        self.assertEqual(итог, [msgs[0], {"role": "user", "content": [msgs[1]["content"][0]]}])
        self.assertEqual(proxy._без_инструкции_сжатия(
            [{"role": "user", "content": proxy.COMPACT_SIGNATURE}]), [])

    def test_сводка_во_втором_текстовом_блоке(self):
        """CLI берёт блок с <summary>, а не первый текстовый — блок едет туда же."""
        self.поток = поток_ответа([("thinking", "t"), ("text", "<analysis>a</analysis>"),
                                   ("text", "<summary>сводка</summary>")])
        self.Облако.тело = self.поток
        doc = self.запрос()
        список = события(self.спросить(doc))
        проверить_целостность(self, список)
        запись = как_cli(список)
        self.assertTrue(запись.startswith("Summary:\nсводка"))
        self.assertIn("<ccs-preserved>", запись)
        вставки = [д["index"] for т, д in список if т == "content_block_delta"
                   and "<ccs-preserved>" in д["delta"].get("text", "")]
        self.assertEqual(вставки, [2])

    def test_тег_summary_разрезан_дельтами(self):
        self.поток = поток_ответа([("text", "<analysis>a</analysis><summary>сводка</summary>")],
                                  шаг=3)
        self.Облако.тело = self.поток
        список = события(self.спросить(self.запрос()))
        self.assertIn("<ccs-preserved>", как_cli(список))
        self.assertEqual(proxy.stats["compact_preserved"], 1)

    def test_ответ_без_сводки_как_есть_и_счётчик(self):
        self.поток = поток_ответа([("text", "просто текст")])
        self.Облако.тело = self.поток
        self.assertEqual(self.спросить(self.запрос()), self.поток)
        self.assertEqual((proxy.stats["compact_preserved"], proxy.stats["compact_preserve_failed"]),
                         (0, 1))
        self.assertIn("нет <summary>", self.журнал.getvalue())

    def test_фолбэк_на_локаль_тоже_получает_блок(self):
        self.Облако.статус = 401
        doc = self.запрос()
        блок = self.ожидаемый_блок(doc)
        ответ = self.спросить(doc)
        self.проверить_вставку(ответ, блок)
        self.assertEqual(proxy.stats["compact_fallback"], 1)
        self.assertEqual(proxy.stats["compact_preserved"], 1)
        реплика = self.последняя_реплика(self.Локальный.принятые[-1])
        self.assertTrue(реплика.endswith(proxy.compactor.instruction_addendum()))

    def test_фолбэк_через_реле_без_петли(self):
        """Реле предохранителя на пути не мешает вставке: поток через него приходит тот же."""
        proxy.LOOP_GUARD = proxy.JUNK_GUARD = "on"
        self.Облако.статус = 500
        doc = self.запрос()
        self.проверить_вставку(self.спросить(doc), self.ожидаемый_блок(doc))

    def test_фолбэк_петля_после_сводки_и_повтор(self):
        """Петля после <summary> → обрыв, повтор; в запись CLI идёт сводка повтора с блоком."""
        class Модель(ПетляЗаглушка):
            ответы, принятые, оборваны = [], [], []
        модель = поднять(Модель)
        self.addCleanup(lambda: (модель.shutdown(), модель.server_close()))
        proxy.UP_PORT = модель.server_address[1]
        proxy.LOOP_GUARD = "on"
        self.Облако.статус = 500
        Модель.ответы = [
            поток_ответа([("thinking", "t"), ("text", "<summary>первая</summary>\n" + ВСТУПЛЕНИЕ
                                                   + ПЛАН * 400)]),
            поток_ответа([("thinking", "t"), ("text", "<summary>повтор</summary>")])]
        doc = self.запрос()
        список = события(self.спросить(doc))
        проверить_целостность(self, список)
        self.assertEqual(len(Модель.принятые), 2, "петля поймана, запрос повторён")
        self.assertTrue(Модель.принятые[1]["messages"][-1]["content"].endswith(
            proxy.compactor.instruction_addendum()), "повтор несёт то же дополнение")
        запись = как_cli(список)
        self.assertTrue(запись.startswith("Summary:\nповтор"))
        self.assertTrue(запись.endswith(self.ожидаемый_блок(doc).strip()))
        self.assertEqual(proxy.stats["compact_preserved"], 1)

    def test_accept_encoding_в_локаль_не_едет(self):
        self.Облако.статус = 500
        self.спросить(self.запрос(), {"Accept-Encoding": "gzip, br"})
        # Своего заголовка нет — http.client сам ставит identity: сжатого ответа не будет.
        self.assertEqual(self.Локальный.заголовки[-1].get("accept-encoding", "identity"), "identity")
        proxy.COMPACT_PRESERVE = "off"
        self.спросить(self.запрос(), {"Accept-Encoding": "gzip, br"})
        self.assertEqual(self.Локальный.заголовки[-1]["accept-encoding"], "gzip, br",
                         "без блока заголовки идут как раньше")

    def test_без_облака_блок_тоже_вставляется(self):
        proxy.COMPACT_UPSTREAM = ""
        doc = self.запрос()
        self.проверить_вставку(self.спросить(doc), self.ожидаемый_блок(doc))
        self.assertEqual(self.Облако.принятые, [])

    def test_выключено_поток_байт_в_байт(self):
        proxy.COMPACT_PRESERVE = "off"
        doc = self.запрос()
        self.assertEqual(self.спросить(doc), self.поток)
        self.assertEqual(self.последняя_реплика(self.Облако.принятые[-1]),
                         proxy.COMPACT_SIGNATURE + " Summarize.")
        self.assertEqual((proxy.stats["compact_preserved"], proxy.stats["compact_preserve_failed"]),
                         (0, 0))

    def test_обычный_ход_не_трогается(self):
        doc = self.запрос()
        doc["messages"][-1] = {"role": "user", "content": "проверь сборку"}
        self.assertEqual(self.спросить(doc), self.поток)
        self.assertEqual(self.Облако.принятые, [])

    def test_исключение_compactor_запрос_проходит(self):
        doc = self.запрос()
        with unittest.mock.patch.object(proxy.compactor, "build_preserved",
                                        side_effect=RuntimeError("бах")):
            ответ = self.спросить(doc)
        self.assertEqual(ответ, self.поток)
        self.assertEqual(proxy.stats["compact_preserve_failed"], 1)
        self.assertEqual(proxy.stats["compact_preserved"], 0)
        self.assertEqual(proxy.stats["compact_routed"], 1)
        self.assertEqual(self.последняя_реплика(self.Облако.принятые[-1]),
                         proxy.COMPACT_SIGNATURE + " Summarize.", "тело не тронуто")
        self.assertIn("сохранение дословного: сборка блока не удалось", self.журнал.getvalue())

    def test_исключение_в_добавке_тело_не_тронуто(self):
        doc = self.запрос()
        with unittest.mock.patch.object(proxy.compactor, "instruction_addendum",
                                        side_effect=ValueError("x")):
            ответ = self.спросить(doc)
        self.assertEqual(ответ, self.поток)
        self.assertEqual(proxy.stats["compact_preserve_failed"], 1)
        self.assertEqual(self.последняя_реплика(self.Облако.принятые[-1]),
                         proxy.COMPACT_SIGNATURE + " Summarize.")

    def test_none_без_вставки(self):
        doc = self.запрос()
        with unittest.mock.patch.object(proxy.compactor, "build_preserved", return_value=None):
            ответ = self.спросить(doc)
        self.assertEqual(ответ, self.поток)
        self.assertEqual(self.последняя_реплика(self.Облако.принятые[-1]),
                         proxy.COMPACT_SIGNATURE + " Summarize.")
        self.assertEqual((proxy.stats["compact_preserved"], proxy.stats["compact_preserve_failed"]),
                         (0, 0))

    def test_ответ_ошибка_не_трогается(self):
        """Облако упало, локаль тоже ответила ошибкой — клиент видит её как есть."""
        self.Облако.статус = 500
        self.Локальный.статус, self.Локальный.вид = 400, "application/json"
        self.Локальный.тело = b'{"type":"error","error":{"type":"x","message":"y"}}'
        self.assertEqual(self.спросить(self.запрос()), self.Локальный.тело)
        self.assertEqual(proxy.stats["compact_preserved"], 0)

    def test_не_потоковый_ответ_блок_в_тексте_со_сводкой(self):
        doc = self.запрос()
        doc["stream"] = False
        модель = {"id": "m", "type": "message", "role": "assistant", "content": [
            {"type": "thinking", "thinking": "t", "signature": "s"},
            {"type": "text", "text": "<summary>сводка</summary>"}], "stop_reason": "end_turn"}
        self.Облако.вид = "application/json"
        self.Облако.тело = json.dumps(модель).encode()
        ответ = json.loads(self.спросить(doc))
        self.assertEqual(ответ["content"][0], модель["content"][0])
        self.assertEqual(ответ["content"][1], {"type": "text", "text": "<summary>сводка</summary>"
                                               + self.ожидаемый_блок(doc)})
        self.assertEqual(len(ответ["content"]), 2)
        self.assertEqual(proxy.stats["compact_preserved"], 1)

    def test_блок_списком_дописывается_в_последний_текст(self):
        doc = self.запрос()
        doc["messages"][-1] = {"role": "user", "content": [
            {"type": "text", "text": proxy.COMPACT_SIGNATURE + " Summarize."},
            {"type": "text", "text": "Additional: keep it short."}]}
        self.спросить(doc)
        реплика = self.последняя_реплика(self.Облако.принятые[-1])
        self.assertEqual(реплика[0], doc["messages"][-1]["content"][0])
        self.assertEqual(реплика[1]["text"], "Additional: keep it short.\n\n"
                         + proxy.compactor.instruction_addendum())

    def test_счётчики_в_stats(self):
        исходный = self.сохранено[-1]
        self.assertIn("compact_preserved", исходный)
        self.assertIn("compact_preserve_failed", исходный)
        self.спросить(self.запрос())
        c = http.client.HTTPConnection("127.0.0.1", self.прокси.server_address[1], timeout=30)
        c.request("GET", "/__proxy/stats")
        отдано = json.loads(c.getresponse().read())
        c.close()
        self.assertEqual(отдано["compact_preserved"], 1)
        self.assertEqual(отдано["compact_preserve"], "on")


class ВставкаБлокаTests(unittest.TestCase):
    """Вставка в поток без сокета: куски режут события где угодно."""

    def прогнать(self, сырое, кусок):
        out = []
        в = proxy.ВставкаБлока(out.append, "БЛОК")
        for i in range(0, len(сырое), кусок):
            в.усвоить(сырое[i:i + кусок])
        в.дослать()
        return b"".join(out)

    def test_любая_нарезка_даёт_тот_же_поток(self):
        сырое = поток_ответа([("thinking", "a" * 300), ("text", "b" * 300 + "<summary>c</summary>"),
                              ("tool_use", '{"x":1}')])
        эталон = self.прогнать(сырое, len(сырое))
        for кусок in (1, 7, 64, 1000):
            with self.subTest(кусок):
                self.assertEqual(self.прогнать(сырое, кусок), эталон)
        список = события(эталон)
        проверить_целостность(self, список)
        проверить_индексы(self, список)
        self.assertTrue(как_cli(список).endswith("БЛОК"))

    def test_сводка_в_начальном_тексте_блока(self):
        сырое = (sse("message_start", {"type": "message_start", "message": {}})
                 + sse("content_block_start", {"type": "content_block_start", "index": 0,
                       "content_block": {"type": "text", "text": "<summary>с</summary>"}})
                 + sse("content_block_stop", {"type": "content_block_stop", "index": 0})
                 + sse("message_stop", {"type": "message_stop"}))
        self.assertIn("БЛОК", self.прогнать(сырое, 11).decode())

    def test_без_message_start_вставки_нет(self):
        сырое = b'event: error\ndata: {"type":"error"}\n\n'
        self.assertEqual(self.прогнать(сырое, 5), сырое)


if __name__ == "__main__":
    unittest.main()


class КарантинСессииTests(unittest.TestCase):
    """Мусор → повтор с cache_salt мимо испорченного кэша; сессия и дальше ходит со своей солью."""

    спросить = ПетляСквознаяTests.спросить
    мусорный_поток = МусорСквознойTests.мусорный_поток

    def setUp(self):
        ПетляСквознаяTests.setUp(self)
        self.было = proxy.JUNK_GUARD, proxy.CACHE_QUARANTINE, dict(proxy._карантин)
        proxy.JUNK_GUARD, proxy.CACHE_QUARANTINE = "on", "on"
        proxy._карантин.clear()
        for к in ("junk_aborts", "quarantine_retries", "quarantine_failed", "quarantine_salted",
                  "quarantine_loop_reused"):
            proxy.stats[к] = 0

    def tearDown(self):
        proxy.JUNK_GUARD, proxy.CACHE_QUARANTINE, карантин = self.было
        proxy._карантин.clear()
        proxy._карантин.update(карантин)
        ПетляСквознаяTests.tearDown(self)

    def текст(self, список):
        return "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")

    def test_повтор_мимо_кэша_и_соль_сессии(self):
        self.Модель.ответы = [self.мусорный_поток(), поток_ответа([("text", РУССКАЯ_ПРОЗА)]),
                              поток_ответа([("text", "второй ход")]), поток_ответа([("text", "чужой")])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        текст = self.текст(список)
        self.assertIn("ответ пересчитывается заново", текст)
        self.assertIn(РУССКАЯ_ПРОЗА.strip()[:40], текст, "ответ повтора приклеен к тому же сообщению")
        self.assertNotIn("Повтори ход", текст)
        первый, повтор = self.Модель.принятые[:2]
        self.assertNotIn("cache_salt", первый)
        соль = повтор["cache_salt"]
        self.assertEqual({k: v for k, v in повтор.items() if k != "cache_salt"}, первый,
                         "повтор — то же тело, только с солью")
        self.assertEqual(proxy._карантин["sess-loop"], соль)
        # Следующий ход той же сессии — с той же солью: кэш работает, но в чистом пространстве
        self.спросить(ждать_карточку=False)
        self.assertEqual(self.Модель.принятые[2]["cache_salt"], соль)
        # Чужая сессия соли не получает
        self.спросить(сессия="sess-other", ждать_карточку=False)
        self.assertNotIn("cache_salt", self.Модель.принятые[3])
        self.assertEqual((proxy.stats["quarantine_retries"], proxy.stats["quarantine_failed"],
                          proxy.stats["quarantine_salted"]), (1, 0, 1))

    def test_мусор_в_засоленной_сессии_соль_новая(self):
        # Мусор в сессии, которая уже ходит с солью: новая соль на каждый срыв,
        # _карантин перезаписан (испорченный кэш мог появиться и в засоленном пространстве).
        proxy._карантин["sess-loop"] = "q-sess-loop-предварительная"
        self.Модель.ответы = [self.мусорный_поток(), поток_ответа([("text", РУССКАЯ_ПРОЗА)])]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        повтор = self.Модель.принятые[1]["cache_salt"]
        self.assertNotEqual(повтор, "q-sess-loop-предварительная", "мусор берёт новую соль")
        self.assertEqual(proxy._карантин["sess-loop"], повтор, "_карантин перезаписан новой солью")

    def test_мусор_и_в_повторе_обрыв(self):
        self.Модель.ответы = [self.мусорный_поток(), self.мусорный_поток()]
        журнал = io.StringIO()
        with contextlib.redirect_stderr(журнал):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        текст = self.текст(список)
        self.assertIn("ответ пересчитывается заново", текст)
        self.assertIn("[прокси: ответ оборван — модель выдаёт мусор. Повтори ход.]", текст)
        # Оператор видит в журнале, что повтор тоже не ушёл: строка master.
        self.assertIn("карантин: мусор или петля и в повторе — ответ оборван", журнал.getvalue())
        self.assertEqual(len(self.Модель.принятые), 2, "повтор один")
        self.assertEqual((proxy.stats["quarantine_retries"], proxy.stats["quarantine_failed"]), (0, 1))

    def test_без_карантина_повтора_нет(self):
        proxy.CACHE_QUARANTINE = "off"
        self.Модель.ответы = [self.мусорный_поток()]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        self.assertIn("Повтори ход", self.текст(список))
        self.assertEqual(len(self.Модель.принятые), 1)
        self.assertEqual(proxy._карантин, {})


class КарантинLoopTests(unittest.TestCase):
    """Петля → повтор с cache_salt мимо испорченного кэша; сессия и дальше ходит со своей солью.

    Зеркало КарантинСессииTests, но для петли: повтор идёт с солью карантина (не uuid4),
    и следующий ход той же сессии получает ту же соль через parse_body."""

    спросить = ПетляСквознаяTests.спросить

    def setUp(self):
        ПетляСквознаяTests.setUp(self)
        self.было = (proxy.LOOP_GUARD, proxy.LOOP_RETRY, proxy.CACHE_QUARANTINE,
                     dict(proxy._карантин))
        proxy.LOOP_GUARD, proxy.LOOP_RETRY, proxy.CACHE_QUARANTINE = "on", "on", "on"
        proxy._карантин.clear()
        for к in ("loops_detected", "loops_retried", "loops_truncated",
                  "quarantine_retries", "quarantine_failed", "quarantine_salted",
                  "quarantine_loop_reused"):
            proxy.stats[к] = 0

    def tearDown(self):
        (proxy.LOOP_GUARD, proxy.LOOP_RETRY, proxy.CACHE_QUARANTINE, карантин) = self.было
        proxy._карантин.clear()
        proxy._карантин.update(карантин)
        ПетляСквознаяTests.tearDown(self)

    def текст(self, список):
        return "".join(д["delta"].get("text", "") for т, д in список if т == "content_block_delta")

    def test_повтор_мимо_кэша_и_соль_сессии(self):
        # Петля → повтор с солью; сессия и дальше ходит со своей солью; чужая — нет.
        self.Модель.ответы = [
            поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", "Нашёл хуки.")], stop_reason="tool_use", output_tokens=77),
            поток_ответа([("text", "второй ход")]),
            поток_ответа([("text", "чужой")])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        self.assertIn("Нашёл хуки.", self.текст(список), "повтор приклеен к тому же сообщению")
        первый, повтор = self.Модель.принятые[:2]
        self.assertNotIn("cache_salt", первый, "исходное тело — как есть")
        соль = повтор["cache_salt"]
        self.assertIsInstance(соль, str) and self.assertTrue(соль, "повтор несёт соль карантина")
        self.assertEqual(proxy._карантин["sess-loop"], соль, "повтор кладёт соль в _карантин")
        # Следующий ход той же сессии — с той же солью: кэш работает, но в чистом пространстве.
        self.спросить(ждать_карточку=False)
        self.assertEqual(self.Модель.принятые[2]["cache_salt"], соль)
        # Чужая сессия соли не получает.
        self.спросить(сессия="sess-other", ждать_карточку=False)
        self.assertNotIn("cache_salt", self.Модель.принятые[3])
        self.assertEqual(proxy.stats["quarantine_salted"], 1, "подстановка соли в parse_body")

    def test_повторная_петля_в_засоленной_сессии_соль_та_же(self):
        # Две петли подряд в одной сессии: первый срыв выдаёт соль, второй — берёт её,
        # новую не выдаёт и _карантин не перезаписывает (quarantine_loop_reused == 1).
        self.Модель.ответы = [
            поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", "Нашёл хуки.")], stop_reason="tool_use", output_tokens=77),
            поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", "Нашёл хуки.")], stop_reason="tool_use", output_tokens=77)]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
            self.спросить(ждать_карточку=False)
        соль = self.Модель.принятые[1]["cache_salt"]
        self.assertEqual(self.Модель.принятые[3]["cache_salt"], соль,
                         "второй повтор — та же соль, что в _карантин")
        self.assertEqual(proxy._карантин["sess-loop"], соль, "_карантин не перезаписан")
        self.assertEqual(proxy.stats["quarantine_loop_reused"], 1,
                         "повторная петля в засоленной сессии учтена")

    def test_первая_петля_без_соли_повторное_поведение(self):
        # Первая петля в сессии без соли: новая соль, как раньше (quarantine_loop_reused == 0).
        self.Модель.ответы = [
            поток_ответа([("thinking", "Plan quickly."), ("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", "Нашёл хуки.")], stop_reason="tool_use", output_tokens=77)]
        with contextlib.redirect_stderr(io.StringIO()):
            self.спросить(ждать_карточку=False)
        соль = self.Модель.принятые[1]["cache_salt"]
        self.assertTrue(соль.startswith("q-sess-loop-"), "первая петля выдаёт новую соль")
        self.assertEqual(proxy._карантин["sess-loop"], соль)
        self.assertEqual(proxy.stats["quarantine_loop_reused"], 0,
                         "первая петля — не переиспользование")

    def test_петля_и_в_повторе_обрыв(self):
        # Петля и в повторе: ответ усечён, соль всё равно в _карантин (сессия не ходит в испорченный кэш).
        self.Модель.ответы = [
            поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 400)])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        self.assertIn("модель зациклилась, повтор запроса тоже", self.текст(список))
        self.assertEqual(len(self.Модель.принятые), 2, "повтор один")
        self.assertEqual(self.Модель.принятые[1]["cache_salt"],
                         proxy._карантин["sess-loop"], "соль повторения в _карантин")
        self.assertEqual((proxy.stats["loops_detected"], proxy.stats["loops_retried"],
                          proxy.stats["loops_truncated"]), (2, 1, 1))

    def test_без_карантина_повтор_без_соли(self):
        # CACHE_QUARANTINE=off: повтор петли идёт со сдвигом сэмплинга, но без cache_salt.
        proxy.CACHE_QUARANTINE = "off"
        self.Модель.ответы = [
            поток_ответа([("text", ВСТУПЛЕНИЕ + ПЛАН * 400)]),
            поток_ответа([("text", "Готово.")])]
        with contextlib.redirect_stderr(io.StringIO()):
            список = self.спросить(ждать_карточку=False)
        проверить_целостность(self, список)
        self.assertEqual(len(self.Модель.принятые), 2)
        self.assertNotIn("cache_salt", self.Модель.принятые[1], "без карантина — без соли")
        self.assertEqual(proxy._карантин, {})
