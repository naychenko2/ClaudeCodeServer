#!/usr/bin/env python3
# Тесты чистых функций прокси. Без сети и без поднятого vLLM:
#   python3 -m unittest discover -s tools/thinking-strip-proxy
import copy
import http.client
import json
import os
import shutil
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import proxy

# Параметры прунинга в тестах задаём явно: дефолты живут в переменных окружения и
# меняются на стенде, а тест обязан проверять поведение, а не текущую настройку.
# min_context_tokens=0 — условие «чат уже большой» здесь отключено намеренно: у него свои
# тесты ниже, а эти проверяют границу, и один тест должен проверять одну вещь.
# Ручки границы — в ТОКЕНАХ, а рассуждает тест в выводах, поэтому переводим через тот же
# коэффициент, что и прокси: хвост 120k символов = 20 последних выводов, ступень 60k = 10
# выводов. Жёстко зашить числа в токенах нельзя — они поехали бы вместе с коэффициентом, и
# тесты границы начали бы проверять другую геометрию. Мерку саму по себе проверяет
# ОЦЕНОЧНЫЙ набор ниже, там числа зашиты намеренно.
def _в_токенах(символов, вверх=False):
    точно = символов / proxy.CHARS_PER_TOKEN_HISTORY
    return int(точно) + 1 if вверх and int(точно) != точно else int(точно)


BIG = "x" * 6000  # один «крупный» вывод
P = dict(keep_tail_tokens=_в_токенах(20 * len(BIG)),
         step_tokens=_в_токенах(10 * len(BIG), вверх=True),
         min_chars=2000, min_tokens=_в_токенах(80000), min_context_tokens=0)


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
        """
        base = history(120)
        pruned_base, _, blocks = proxy.prune_tool_results(base, **P)
        self.assertGreater(blocks, 0, "иначе тест сравнивает две нетронутые истории и всегда зелён")
        for extra in range(1, 10):  # внутри одного кванта граница обязана стоять
            longer, _, _ = proxy.prune_tool_results(history(120 + extra), **P)
            self.assertEqual(longer[:len(base)], pruned_base,
                             f"префикс поехал после {extra} дописанных вызовов")

    def test_граница_двигается_квантом(self):
        """…и всё же двигается: через квант вызовов обрезанных блоков становится больше."""
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
        for extra in range(1, 5):
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
        proxy.stats.update(compact_routed=0, compact_fallback=0)

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

    def test_маршрут_выключен_по_умолчанию(self):
        proxy.COMPACT_UPSTREAM = ""
        self.assertIn("локальный".encode(), self.спросить(self.запрос_сжатия()))
        self.assertEqual(self.Облако.принятые, [])
        self.assertEqual((proxy.stats["compact_routed"], proxy.stats["compact_fallback"]), (0, 0))


if __name__ == "__main__":
    unittest.main()
