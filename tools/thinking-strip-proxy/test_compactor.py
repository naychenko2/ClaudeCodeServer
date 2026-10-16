#!/usr/bin/env python3
# Тесты чистых функций compactor.py (шаг 3 сжатия: голова + реплики человека).
# Без сети и состояния:   python3 -m unittest test_compactor
import hashlib
import json
import os
import re
import unittest

import compactor


# --- Вспомогательные ---------------------------------------------------------------------------

def _marker(raw):
    """Верный маркер: N = кодовые точки, sha = 8 hex SHA-256(UTF-8) — как ClaudeSession."""
    n = len(raw)
    sha = hashlib.sha256(raw.encode("utf-8")).hexdigest()[:8]
    return compactor.MARKER_FORMAT.format(n, sha) + "\n" + raw


def _fake_marker(raw, sha="00000000"):
    """Маркер с неверным sha (цитата/подделка): N верный, хеш не совпадает."""
    return compactor.MARKER_FORMAT.format(len(raw), sha) + "\n" + raw


def _user(content):
    return {"role": "user", "content": content}


def _assistant(text="ответ"):
    return {"role": "assistant", "content": [{"type": "text", "text": text}]}


class CompactorTests(unittest.TestCase):
    # --- Тест-константа: формат маркера совпадает с ClaudeSession.cs ---------------------------

    def test_константа_маркер_совпадает_s_claude_session(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        cs = os.path.join(root, "backend", "ClaudeHomeServer.Llm", "Claude", "ClaudeSession.cs")
        with open(cs, encoding="utf-8") as f:
            src = f.read()
        m = re.search(r'HumanTextMarkerFormat\s*=\s*"([^"]+)"', src)
        self.assertIsNotNone(m, "HumanTextMarkerFormat не найдена в ClaudeSession.cs")
        self.assertEqual(m.group(1), compactor.MARKER_FORMAT,
                         "формат маркера в compactor.py разошёлся с ClaudeSession.cs")

    # --- Маркер: принятие ---------------------------------------------------------------------

    def test_маркер_кириллица_эмодзи(self):
        raw = "привет мир 🚀"
        self.assertEqual(compactor.human_texts([_user(_marker(raw))]), [raw])

    def test_маркер_несколько_в_хронологическом_порядке(self):
        a, b = "первая реплика", "вторая реплика"
        msgs = [_user(_marker(a)), _assistant(), _user(_marker(b))]
        self.assertEqual(compactor.human_texts(msgs), [a, b])

    def test_маркер_в_массиве_блоков(self):
        # content — массив блоков (как в реальном Anthropic Messages), маркер в text-блоке
        raw = "реплика в блоке"
        content = [{"type": "text", "text": _marker(raw)}]
        self.assertEqual(compactor.human_texts([_user(content)]), [raw])

    # --- Маркер: отвержение подделки ----------------------------------------------------------

    def test_маркер_поддельный_sha_отвергнут(self):
        # размеченный чат: один верный + один поддельный (неверный sha) → поддельная отвергнута
        good = "верная реплика"
        msgs = [_user(_marker(good)), _user(_fake_marker("поддельная"))]
        self.assertEqual(compactor.human_texts(msgs), [good])

    def test_маркер_цитата_в_реплике_отвергнута(self):
        # человек цитирует маркер в своей реплике (неверный sha) — цитата хеш не пройдёт
        raw = 'смотри: <!-- ccs:human-text chars=3 sha=00000000 --> тут'
        good = "настоящая реплика"
        msgs = [_user(raw), _user(_marker(good))]
        self.assertEqual(compactor.human_texts(msgs), [good])

    def test_маркер_цитата_в_recall_отвергнута(self):
        # recall (заметка) цитирует маркер; чат размечен (есть верный маркер) → recall не реплика
        recall = "## Память команды проекта\n- цитата: " + _fake_marker("фрагмент", "12345678")
        good = "реплика"
        msgs = [_user(recall), _user(_marker(good))]
        self.assertEqual(compactor.human_texts(msgs), [good])

    # --- Картинки в content -------------------------------------------------------------------

    def test_картинки_в_content_text_блок_реплика(self):
        # user-сообщение с картинкой (content — массив) + text-блок → text-блок = реплика
        content = [{"type": "image", "source": {"type": "base64", "data": "AA=="}},
                   {"type": "text", "text": "привет с картинкой"}]
        self.assertEqual(compactor.human_texts([_user(content)]), ["привет с картинкой"])

    def test_только_картинка_без_текста_не_реплика(self):
        content = [{"type": "image", "source": {"type": "base64", "data": "AA=="}}]
        self.assertEqual(compactor.human_texts([_user(content)]), [])

    # --- Размеченный чат игнорирует неразмеченные ---------------------------------------------

    def test_размеченный_игнорирует_неразмеченные(self):
        marked = "размеченная реплика"
        msgs = [_user(_marker(marked)), _user("неразмеченная реплика")]
        self.assertEqual(compactor.human_texts(msgs), [marked])

    # --- Старый чат: эвристика ----------------------------------------------------------------

    def test_старый_чат_эвристика_разделитель_после_последнего(self):
        text = "## Память команды проекта\n- факт 1\n- факт 2\n\n---\n\nреплика человека"
        self.assertEqual(compactor.human_texts([_user(text)]), ["реплика человека"])

    def test_старый_чат_несколько_разделителей_берёт_последний(self):
        # несколько разделителей: режем на последнем, у которого СЛЕВА (в сегменте)
        # заголовок хвоста — здесь это разделитель перед «финальной репликой».
        text = ("обвязка 1\n\n---\n\n"
                "## Возможно релевантные заметки\n- заметка\n\n---\n\n"
                "финальная реплика")
        self.assertEqual(compactor.human_texts([_user(text)]), ["финальная реплика"])

    def test_старый_чат_реплика_сама_содержит_разделитель(self):
        # реплика человека сама содержит "\n\n---\n\n": заголовок хвоста — только в
        # сегменте перед ПЕРВЫМ разделителем, поэтому режем там, а не на последнем
        # (иначе отвалилась бы «часть 1» реплики).
        text = ("## Память команды проекта\n- факт\n\n---\n\n"
                "часть 1 реплики\n\n---\n\nчасть 2 реплики")
        self.assertEqual(
            compactor.human_texts([_user(text)]),
            ["часть 1 реплики\n\n---\n\nчасть 2 реплики"])

    def test_старый_чат_system_reminder_вырезан(self):
        text = "<system-reminder>служебная обвязка</system-reminder>привет"
        self.assertEqual(compactor.human_texts([_user(text)]), ["привет"])

    def test_старый_чат_сводка_cli_пропущена(self):
        summary = ("This session is being continued from a previous conversation that ran out of "
                   "context. Summary of the conversation, whose transcript may be found at:\n"
                   "- Primary request: ...")
        # сводка CLI — ни реплика, ни голова
        self.assertEqual(compactor.human_texts([_user(summary)]), [])

    def test_старый_чат_обычный_текст_без_заголовков_весь(self):
        # без заголовков хвоста — не режем, оставляем весь текст
        self.assertEqual(compactor.human_texts([_user("просто текст")]), ["просто текст"])

    # --- tool_result — не реплика -------------------------------------------------------------

    def test_tool_result_не_реплика(self):
        # user-сообщение, состоящее из tool_result (вывод инструмента) — не человек
        content = [{"type": "tool_result", "tool_use_id": "t1", "content": "результат"}]
        self.assertEqual(compactor.human_texts([_user(content)]), [])

    # --- head: первая реплика + потолок + метка пропуска --------------------------------------

    def test_head_первая_реплика(self):
        a, b = "первая", "вторая"
        msgs = [_user(_marker(a)), _user(_marker(b))]
        self.assertEqual(compactor.head(msgs), a)

    def test_head_нет_реплик_none(self):
        self.assertIsNone(compactor.head([_assistant()]))

    def test_head_длинная_с_меткой_пропуска(self):
        raw = "А" * 20000  # длиннее HEAD_CAP=15000
        msgs = [_user(_marker(raw))]
        h = compactor.head(msgs)
        self.assertIn("[… пропущено 5000 символов …]", h)
        self.assertTrue(h.startswith("А"))
        self.assertTrue(h.rstrip().endswith("А"))

    def test_head_короткая_без_метки(self):
        raw = "короткая реплика"
        self.assertEqual(compactor.head([_user(_marker(raw))]), raw)

    # --- recent_humans: от новых к старым + потолок -------------------------------------------

    def test_recent_от_новых_к_старым(self):
        a, b, c = "первая", "вторая", "третья"
        msgs = [_user(_marker(a)), _user(_marker(b)), _user(_marker(c))]
        self.assertEqual(compactor.recent_humans(msgs), [c, b, a])

    def test_recent_потолок_обрезает_но_не_отбрасывает(self):
        # одна длинная реплика: влезает в бюджет только с обрезкой → метка пропуска, не отброс
        long = "Б" * 40000
        short = "короткая"
        msgs = [_user(_marker(short)), _user(_marker(long))]
        out = compactor.recent_humans(msgs, cap=10000)
        # от новых к старым: сначала long (обрезана), budget исчерпан → stop
        self.assertEqual(len(out), 1)
        self.assertIn("[… пропущено", out[0])

    def test_recent_всё_влезает(self):
        a, b = "первая", "вторая"
        msgs = [_user(_marker(a)), _user(_marker(b))]
        self.assertEqual(compactor.recent_humans(msgs, cap=compactor.HUMANS_CAP), [b, a])


# --- Корпус Веры: реальные чаты (skipUnless, если каталога нет) ------------------------------

def _corpus_dir():
    home = os.environ.get("BENCH_HOME", "/srv/ccs/data/compact-bench")
    return os.path.join(home, "corpus")


# Явные исключения (имя_фикстуры, индекс в humans): служебные <task-notification>,
# которые стенд считает «humans» по сырому транскрипту, а в реконструированном history
# их нет/они иные — это не реплики человека, покрытие найденной репликой не требуется.
# (Задача предвидела c03 и c07; с двунаправленной проверкой c03 покрывается, остаётся c07.)
_CORPUS_EXCEPTIONS = {
    ("c07.json", 1): "<task-notification> b4vdpd0xy — служебное уведомление, не человек",
}


def _related(stand, found):
    """Реплика стенда «покрыта» найденной: одно — подстрока другого (в любую сторону)
    или есть общее подстроковое окно >= 20 (сырой vs реконструированный текст расходятся
    обвязкой, а ядро реплики совпадает)."""
    if not stand or not found:
        return False
    if stand in found or found in stand:
        return True
    L = 20
    for s in range(0, max(1, len(found) - L + 1), L):
        if found[s:s + L] in stand:
            return True
    return False


def _uncovered(item, filename, found):
    """Индексы реплик стенда, не покрытых ни одной найденной (кроме исключений)."""
    hum = item.get("humans") or []
    out = []
    for i, h in enumerate(hum):
        t = h.get("text", "") if isinstance(h, dict) else str(h)
        if any(_related(t, f) for f in found):
            continue
        if (filename, i) in _CORPUS_EXCEPTIONS:
            continue
        out.append(i)
    return out


class CorpusTests(unittest.TestCase):
    """human_texts на корпусе: (а) >=1 реплики, (б) каждая реплика стенда покрыта."""

    @classmethod
    def setUpClass(cls):
        cls.dir = _corpus_dir()
        if not os.path.isdir(cls.dir):
            raise unittest.SkipTest(f"корпус не найден: {cls.dir}")

    def _items(self):
        for fn in sorted(os.listdir(self.dir)):
            if not (fn.startswith("c") and fn.endswith(".json")):
                continue
            with open(os.path.join(self.dir, fn), encoding="utf-8") as f:
                yield fn, json.load(f)

    def test_корпус_реплики_покрыты(self):
        for fn, item in self._items():
            history = item.get("history") or []
            found = [x for x in compactor.human_texts(history) if x]
            stand = len(item.get("humans") or [])
            # (а) на каждой фикстуре найдено >= 1 реплики
            self.assertGreaterEqual(len(found), 1, f"{fn}: не найдено ни одной реплики")
            # (б) каждая реплика стенда (кроме исключений) покрыта найденной
            unc = _uncovered(item, fn, found)
            self.assertEqual(unc, [], f"{fn}: непокрытые реплики стенда: {unc}")
            print(f"  {fn}: humans(стенд)={stand}  human_texts={len(found)}")

    def test_корпус_мутация_каждая_вторая_краснеет(self):
        # мутация: human_texts теряет каждую вторую реплику -> (б) краснеет на
        # нескольких фикстурах (не только c01)
        orig = compactor.human_texts
        compactor.human_texts = lambda msgs: orig(msgs)[::2]
        try:
            red = []
            for fn, item in self._items():
                history = item.get("history") or []
                found = [x for x in compactor.human_texts(history) if x]
                if len(found) < 1 or _uncovered(item, fn, found):
                    red.append(fn)
            self.assertGreaterEqual(len(red), 2,
                                     f"мутация не покраснела достаточно: {red}")
        finally:
            compactor.human_texts = orig


if __name__ == "__main__":
    unittest.main()
