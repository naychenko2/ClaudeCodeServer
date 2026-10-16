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


# --- Шаг 4: changed_files / last_checks / last_plan / anchors / build_preserved --------------

def _tool_use(tid, name, **inp):
    return {"type": "tool_use", "id": tid, "name": name, "input": inp}


def _tool_result(tid, content, is_error=False):
    """tool_result в user-сообщении (как в реальном Anthropic Messages)."""
    return {"role": "user", "content": [{"type": "tool_result", "tool_use_id": tid,
                                         "content": content, "is_error": is_error}]}


def _assistant(*blocks):
    return {"role": "assistant", "content": list(blocks)}


class Step4ChangedFilesTests(unittest.TestCase):
    def test_изменённые_файлы_пути_правки_write(self):
        msgs = [
            _assistant(_tool_use("1", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("2", "Write", file_path="c/d.py")),
            _assistant(_tool_use("3", "Edit", file_path="a/b.py")),
        ]
        out = compactor.changed_files(msgs)
        # новые сверху: a/b.py (последнее касание) перед c/d.py
        self.assertEqual(out[0]["path"], "a/b.py")
        self.assertEqual(out[0]["edits"], 2)
        self.assertFalse(out[0]["write"])
        self.assertEqual(out[1]["path"], "c/d.py")
        self.assertEqual(out[1]["edits"], 0)
        self.assertTrue(out[1]["write"])

    def test_notebook_использует_notebook_path(self):
        msgs = [_assistant(_tool_use("1", "NotebookEdit", notebook_path="nb/cells.ipynb"))]
        out = compactor.changed_files(msgs)
        self.assertEqual(out[0]["path"], "nb/cells.ipynb")

    def test_пустая_история(self):
        self.assertEqual(compactor.changed_files([]), [])

    def test_мульти_edit_считается_правкой(self):
        msgs = [_assistant(_tool_use("1", "MultiEdit", file_path="x/y.py"))]
        self.assertEqual(compactor.changed_files(msgs)[0]["edits"], 1)


class Step4LastChecksTests(unittest.TestCase):
    def test_парование_по_tool_use_id(self):
        # is_error берётся из PARНОГО tool_result (по tool_use_id); сломать парование —
        # is_error будет False, тест краснеет.
        msgs = [
            _assistant(_tool_use("t1", "Bash", command="dotnet test -m:4")),
            _tool_result("t1", "3 failed", is_error=True),
        ]
        checks = compactor.last_checks(msgs)
        self.assertEqual(len(checks), 1)
        self.assertTrue(checks[0]["is_error"], "is_error из парного tool_result")
        self.assertIn("3 failed", checks[0]["result"])

    def test_последний_запуск_каждого_вида(self):
        # два запуска dotnet build: последний (is_error=True) вытесняет первый
        msgs = [
            _assistant(_tool_use("a1", "Bash", command="dotnet build -m:4")),
            _tool_result("a1", "ok", is_error=False),
            _assistant(_tool_use("a2", "Bash", command="dotnet build -m:4")),
            _tool_result("a2", "err", is_error=True),
        ]
        checks = compactor.last_checks(msgs)
        build = [c for c in checks if c["kind"] == "dotnet build"]
        self.assertEqual(len(build), 1)
        self.assertTrue(build[0]["is_error"], "последний запуск (с ошибкой) вытеснил первый")

    def test_mcp_build_и_tests(self):
        msgs = [
            _assistant(_tool_use("m1", "mcp__dev__build", target="backend/X")),
            _tool_result("m1", "built", is_error=False),
            _assistant(_tool_use("m2", "mcp__tests__run_tests", target="backend/X.Tests")),
            _tool_result("m2", "1 failed", is_error=True),
        ]
        checks = compactor.last_checks(msgs)
        kinds = {c["kind"] for c in checks}
        self.assertIn("mcp__dev__build", kinds)
        self.assertIn("mcp__tests__run_tests", kinds)

    def test_result_обрезан_до_600(self):
        msgs = [
            _assistant(_tool_use("z1", "Bash", command="pytest -q")),
            _tool_result("z1", "x" * 2000, is_error=False),
        ]
        checks = compactor.last_checks(msgs)
        self.assertEqual(len(checks[0]["result"]), 600)

    def test_пустая_история(self):
        self.assertEqual(compactor.last_checks([]), [])


class Step4LastPlanTests(unittest.TestCase):
    def test_todowrite_компактно_по_строке(self):
        msgs = [_assistant(_tool_use("p", "TodoWrite", todos=[
            {"content": "шаг 1", "status": "completed"},
            {"content": "шаг 2", "status": "pending"},
        ]))]
        plan = compactor.last_plan(msgs)
        self.assertIn("[x] шаг 1", plan)
        self.assertIn("[ ] шаг 2", plan)

    def test_exit_plan_mode_включён(self):
        msgs = [_assistant(_tool_use("p", "ExitPlanMode", plan="план из ExitPlanMode"))]
        self.assertIn("план из ExitPlanMode", compactor.last_plan(msgs))

    def test_потолок_6000(self):
        msgs = [_assistant(_tool_use("p", "ExitPlanMode", plan="П" * 7000))]
        self.assertEqual(len(compactor.last_plan(msgs)), 6000)

    def test_none_на_пустой(self):
        self.assertIsNone(compactor.last_plan([]))


class Step4AnchorsTests(unittest.TestCase):
    def test_пути_url_uuid_taskid(self):
        text = ("см. /home/an/src/app.py, commit abc1234, id "
                "123e4567-e89b-12d3-a456-426614174000, <task-id>abc123</task-id>, "
                "https://example.com/x")
        a = compactor.anchors([{"role": "user", "content": text}])
        self.assertIn("/home/an/src/app.py", a)
        self.assertIn("abc1234", a)
        self.assertIn("123e4567-e89b-12d3-a456-426614174000", a)
        self.assertIn("abc123", a)
        self.assertIn("https://example.com/x", a)

    def test_sha_только_рядом_с_commit(self):
        # hex 7-40 без слова commit/в git-выводе — не якорь
        a = compactor.anchors([{"role": "user", "content": "число 1234567 без контекста"}])
        self.assertNotIn("1234567", a)
        b = compactor.anchors([{"role": "user", "content": "commit 1234567 сделан"}])
        self.assertIn("1234567", b)

    def test_строки_ошибок(self):
        text = "всё хорошо\nerror CS0123: member not found\nFAIL: test_x"
        a = compactor.anchors([{"role": "user", "content": text}])
        self.assertTrue(any("error CS0123" in x for x in a))
        self.assertTrue(any("FAIL: test_x" in x for x in a))

    def test_дедуп_и_новые_сверху(self):
        # путь в старом и новом сообщении: держим свежее, новые сверху
        msgs = [
            {"role": "user", "content": "старое: /a/b.py"},
            {"role": "user", "content": "новое: /c/d.py"},
        ]
        a = compactor.anchors(msgs)
        self.assertEqual(a[0], "/c/d.py", "новое сверху")

    def test_cap_обрезка(self):
        msgs = [{"role": "user", "content": f"/path/f{i}.py"} for i in range(200)]
        a = compactor.anchors(msgs, cap=100)
        self.assertGreater(len(a), 0)
        self.assertLessEqual(sum(len(x) for x in a), 100 + max(len(x) for x in a))

    def test_пустая_история(self):
        self.assertEqual(compactor.anchors([]), [])


class Step4BuildPreservedTests(unittest.TestCase):
    def test_структура_блока(self):
        msgs = [
            _user(_marker("постановка задачи")),
            _assistant(_tool_use("1", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("2", "Bash", command="dotnet build -m:4")),
            _tool_result("2", "Build succeeded", is_error=False),
        ]
        block = compactor.build_preserved({"messages": msgs})
        self.assertTrue(block.startswith("<ccs-preserved>"))
        self.assertTrue(block.endswith("</ccs-preserved>"))
        self.assertIn("## Постановка задачи (дословно)", block)
        self.assertIn("## Реплики человека", block)
        self.assertIn("## Изменённые файлы", block)
        self.assertIn("## Последние проверки", block)
        self.assertIn("a/b.py", block)

    def test_none_на_пустой(self):
        self.assertIsNone(compactor.build_preserved({"messages": []}))

    def test_пустые_разделы_не_выводятся(self):
        # только постановка: файлов/проверок/плана/якорей нет
        msgs = [_user(_marker("только постановка"))]
        block = compactor.build_preserved({"messages": msgs})
        self.assertIn("## Постановка задачи", block)
        self.assertNotIn("## Изменённые файлы", block)
        self.assertNotIn("## Последние проверки", block)
        self.assertNotIn("## Якоря", block)

    def test_cap_порядок_урезания(self):
        # блок > cap: режем якоря → проверки → файлы → старые реплики;
        # постановка и самая свежая реплика — никогда
        msgs = [
            _user(_marker("постановка")),
            _user(_marker("старая реплика " + "С" * 5000)),
            _user(_marker("свежая реплика")),
            _assistant(_tool_use("1", "Bash", command="dotnet build -m:4")),
            _tool_result("1", "x" * 5000, is_error=False),
        ]
        # большой cap-превышение: урезаем до cap=1000
        orig_cap = compactor.PRESERVED_CAP
        compactor.PRESERVED_CAP = 1000
        try:
            block = compactor.build_preserved({"messages": msgs})
        finally:
            compactor.PRESERVED_CAP = orig_cap
        self.assertLessEqual(len(block), 1000)
        # постановка и свежая реплика — никогда
        self.assertIn("постановка", block)
        self.assertIn("свежая реплика", block)

    def test_instruction_addendum_английский(self):
        text = compactor.instruction_addendum()
        self.assertIsInstance(text, str)
        self.assertIn("<ccs-preserved>", text)
        self.assertIn("reasoning", text)


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

    def test_корпус_build_preserved(self):
        # на всех фикстурах: не падает, укладывается в PRESERVED_CAP, содержит постановку
        for fn, item in self._items():
            history = item.get("history") or []
            block = compactor.build_preserved({"messages": history})
            if block is None:
                continue
            self.assertLessEqual(len(block), compactor.PRESERVED_CAP,
                                  f"{fn}: блок {len(block)} > PRESERVED_CAP")
            head = compactor.head(history)
            if head:
                self.assertIn(head[:200], block, f"{fn}: постановка не найдена в блоке")
            print(f"  {fn}: block={len(block)} code points")


if __name__ == "__main__":
    unittest.main()
