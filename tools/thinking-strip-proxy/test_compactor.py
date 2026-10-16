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
        text = ("## Возможно релевантные заметки\n- заметка\n\n---\n\n"
                "средняя часть\n\n---\n\nфинальная реплика")
        self.assertEqual(compactor.human_texts([_user(text)]), ["финальная реплика"])

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


class CorpusTests(unittest.TestCase):
    """human_texts на корпусе настоящих чатов: число реплик ~ числу humans стенда."""

    @classmethod
    def setUpClass(cls):
        cls.dir = _corpus_dir()
        if not os.path.isdir(cls.dir):
            raise unittest.SkipTest(f"корпус не найден: {cls.dir}")

    def test_корпус_число_реплик(self):
        rows = []
        for fn in sorted(os.listdir(self.dir)):
            if not (fn.startswith("c") and fn.endswith(".json")):
                continue
            with open(os.path.join(self.dir, fn), encoding="utf-8") as f:
                item = json.load(f)
            history = item.get("history") or []
            stand = len(item.get("humans") or [])
            found = len(compactor.human_texts(history))
            rows.append((fn, stand, found))
            # Стенд считает humans по сырому транскрипту (is_human/isMeta), мы — по
            # реконструированному history (Messages body): на краевых (сводка CLI, system-
            # инъекты) методы расходятся на единицы. Sanity-граница, не точное совпадение:
            # ловит катастрофу (0 или 10×), а не требует равенства.
            tol = max(4, int(0.35 * stand))
            self.assertLessEqual(abs(found - stand), tol,
                                  f"{fn}: human_texts={found} vs humans={stand} (допуск {tol})")
        # отчёт: сколько реплик нашлось на каждой фикстуре
        for fn, stand, found in rows:
            print(f"  {fn}: humans(стенд)={stand}  human_texts={found}")


if __name__ == "__main__":
    unittest.main()
