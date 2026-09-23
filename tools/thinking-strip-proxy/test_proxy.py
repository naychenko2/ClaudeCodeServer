#!/usr/bin/env python3
# Тесты чистых функций прокси. Без сети и без поднятого vLLM:
#   python3 -m unittest discover -s tools/thinking-strip-proxy
import contextlib
import copy
import http.client
import io
import json
import os
import shutil
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import diff_miss
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
         min_chars=2000, min_tokens=_в_токенах(80000), min_context_tokens=0,
         first_step_tokens=0)  # первая ступень = обычной: у неё свои тесты ниже


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


# Первая ступень 5 выводов при обычной 10; порог выгоды снят — он не предмет этих тестов.
P1 = dict(P, first_step_tokens=_в_токенах(5 * len(BIG), вверх=True), min_tokens=0)


class ПерваяСтупеньTests(unittest.TestCase):
    """Первая граница встаёт раньше полной ступени, дальше квант обычный.

    Зачем: при пороге первого срабатывания = ступени (121k) прунинг включался на 185–215k, а
    автосжатие CLI — на 232k; один большой Read перепрыгивал окно (живой замер меры 4).
    """

    def test_первая_ступень_срабатывает_раньше_полной(self):
        _, _, blocks = proxy.prune_tool_results(history(25), **P1)  # вне хвоста 5 выводов
        self.assertEqual(blocks, 5)
        _, _, blocks = proxy.prune_tool_results(history(24), **P1)  # 4 — первой ступени нет
        self.assertEqual(blocks, 0)

    def test_между_первой_и_полной_граница_стоит(self):
        """Главное свойство сохраняется: пока полная ступень не набралась, префикс тот же."""
        base, _, _ = proxy.prune_tool_results(history(25), **P1)
        for extra in range(1, 5):
            longer, _, blocks = proxy.prune_tool_results(history(25 + extra), **P1)
            self.assertEqual(blocks, 5, f"граница поехала после {extra} дописанных вызовов")
            self.assertEqual(longer[:len(base)], base)

    def test_после_полной_ступени_квант_обычный(self):
        _, _, blocks = proxy.prune_tool_results(history(30), **P1)   # 10 вне хвоста
        self.assertEqual(blocks, 10)
        _, _, blocks = proxy.prune_tool_results(history(39), **P1)   # 19 — вторая полная ещё нет
        self.assertEqual(blocks, 10)
        _, _, blocks = proxy.prune_tool_results(history(40), **P1)   # 20 — две полных
        self.assertEqual(blocks, 20)

    def test_первая_ступень_не_больше_обычной(self):
        """Значение выше ступени обрезается до неё — иначе первая граница не наступала бы никогда."""
        P2 = dict(P1, first_step_tokens=P["step_tokens"] * 3)
        _, _, blocks = proxy.prune_tool_results(history(25), **P2)
        self.assertEqual(blocks, 0)
        _, _, blocks = proxy.prune_tool_results(history(30), **P2)
        self.assertEqual(blocks, 10)

    def test_ноль_выключает_первую_ступень(self):
        _, _, blocks = proxy.prune_tool_results(history(29), **dict(P1, first_step_tokens=0))
        self.assertEqual(blocks, 0)

    def test_идемпотентность_на_первой_ступени(self):
        once, _, _ = proxy.prune_tool_results(history(27), **P1)
        twice, freed, blocks = proxy.prune_tool_results(once, **P1)
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
        # первая ступень: половина обычной, чтобы первая граница вставала на пороге «чат уже
        # большой» (150k), а не на 185–215k, и всегда выше порога выгоды — иначе он гасил бы её
        первая = proxy.PRUNE_FIRST_STEP_TOKENS * proxy.CHARS_PER_TOKEN_HISTORY
        self.assertAlmostEqual(первая, 200_000, delta=200_000 * 0.02)
        self.assertGreater(proxy.PRUNE_FIRST_STEP_TOKENS, proxy.PRUNE_MIN_TOKENS)


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
        msgs = history(40)  # меньше — не набирается порог выгоды, обрезать нечего
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
    def сообщение(self, usage):
        return ("event: message_start\ndata: "
                + json.dumps({"type": "message_start", "message": {"usage": usage}})
                + "\n\n").encode()

    def test_промпт_это_сумма_трёх_слагаемых(self):
        чтение, промпт = proxy.usage_from_chunk(self.сообщение(
            {"input_tokens": 1000, "cache_read_input_tokens": 9000,
             "cache_creation_input_tokens": 500}))
        self.assertEqual(чтение, 9000)
        self.assertEqual(промпт, 10500, "input_tokens считает только мимо кэша")

    def test_без_кэша_цифры_всё_равно_есть(self):
        чтение, промпт = proxy.usage_from_chunk(self.сообщение({"input_tokens": 1234}))
        self.assertIsNone(чтение)
        self.assertEqual(промпт, 1234)

    def test_чужой_формат_не_роняет(self):
        for кусок in (b"", "data: не json\n\n".encode(), b"event: ping\n\n",
                      b'data: {"type":"content_block_delta"}\n\n', b"\x00\xff"):
            with self.subTest(кусок):
                self.assertEqual(proxy.usage_from_chunk(кусок), (None, None))


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
    """vLLM-заглушка: отвечает SSE с `message_start`, помедлив — иначе prefill нечем мерить."""
    задержка = 0.05
    usage = {"input_tokens": 2000, "cache_read_input_tokens": 18000}
    принятые = []

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        тело = self.rfile.read(n)
        type(self).принятые.append((self.path, тело, dict(self.headers)))
        начало = json.dumps({"type": "message_start",
                             "message": {"usage": type(self).usage}})
        ответ = (f"event: message_start\ndata: {начало}\n\n"
                 "event: message_stop\ndata: {}\n\n").encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Content-Length", str(len(ответ)))
        self.end_headers()
        time.sleep(type(self).задержка)  # prefill: модель думает перед первым байтом
        self.wfile.write(ответ)


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
                          dict(proxy._boundaries))
        proxy.UP_HOST, proxy.UP_PORT = "127.0.0.1", self.модель.server_address[1]
        proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM = "on", ""
        proxy.BACKEND_EVENTS_URL = (f"http://127.0.0.1:{self.бэкенд.server_address[1]}"
                                    "/api/internal/llm-proxy/events")
        proxy._event_secret = "secret-from-config"
        proxy._boundaries.clear()
        proxy.stats.update(events_sent=0, events_failed=0)

    def tearDown(self):
        (proxy.UP_HOST, proxy.UP_PORT, proxy.PRUNE_MODE, proxy.COMPACT_UPSTREAM,
         proxy.BACKEND_EVENTS_URL, proxy._event_secret, стат, границы) = self.сохранено
        proxy.stats.clear()
        proxy.stats.update(стат)
        proxy._boundaries.clear()
        proxy._boundaries.update(границы)
        for с in (self.модель, self.бэкенд, self.прокси):
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
        if proxy._last_event_thread is not None:
            proxy._last_event_thread.join(timeout=10)  # отправка фоновая — дожидаемся её
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
        self.спросить(history(150))
        self.спросить(history(160))
        self.assertEqual(len(self.Бэкенд.принятые), 1, "одна граница — одна карточка")
        self.спросить(history(200))  # следующая ступень
        self.assertEqual(len(self.Бэкенд.принятые), 2)
        self.assertEqual(self.Бэкенд.принятые[1][1]["blocks"], 133)

    def test_у_каждого_чата_своя_граница(self):
        self.спросить(history(120), сессия="sess-1")
        self.спросить(history(120), сессия="sess-2")
        self.assertEqual(len(self.Бэкенд.принятые), 2)
        self.assertEqual({с["sessionId"] for _, с, _ in self.Бэкенд.принятые}, {"sess-1", "sess-2"})

    def test_без_заголовка_сессии_событий_нет(self):
        """Ход без заголовка проксируется как раньше — показывать карточку всё равно некому."""
        self.спросить(history(120), сессия=None)
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
        ответ = self.спросить(history(120))
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


if __name__ == "__main__":
    unittest.main()
