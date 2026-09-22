#!/usr/bin/env python3
# Тесты чистых функций прокси. Без сети и без поднятого vLLM:
#   python3 -m unittest discover -s tools/thinking-strip-proxy
import copy
import unittest

import proxy

# Параметры прунинга в тестах задаём явно: дефолты живут в переменных окружения и
# меняются на стенде, а тест обязан проверять поведение, а не текущую настройку.
P = dict(protect_last=20, quantum=10, min_chars=2000, min_tokens=20000)
BIG = "x" * 6000  # один «крупный» вывод ≈ 1500 токенов


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
        pruned_base, _, _ = proxy.prune_tool_results(base, **P)
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

    def test_картинки_не_трогаем(self):
        msgs = history(120)
        image = [{"type": "image", "source": {"type": "base64", "data": "A" * 9000}}]
        msgs[2]["content"][0]["content"] = image
        pruned, _, _ = proxy.prune_tool_results(msgs, **P)
        self.assertEqual(pruned[2]["content"][0]["content"], image)

    def test_незнакомая_структура_проходит_насквозь(self):
        for msgs in ([], [{"role": "user"}], [{"role": "user", "content": None}],
                     [{"role": "user", "content": [{"type": "tool_result"}]}] * 200,
                     ["строка вместо словаря"] * 200,
                     [{"role": "user", "content": [None, 42]}] * 200):
            out, freed, blocks = proxy.prune_tool_results(msgs, **P)
            self.assertIs(out, msgs)
            self.assertEqual((freed, blocks), (0, 0))


if __name__ == "__main__":
    unittest.main()
