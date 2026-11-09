#!/usr/bin/env python3
# Тесты чистых функций compactor.py (шаг 3 сжатия: голова + реплики человека).
# Без сети и состояния:   python3 -m unittest test_compactor
import hashlib
import json
import os
import re
import unittest

import compactor
import test_proxy


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

    def test_старый_чат_хвост_только_всегда_секции(self):
        # Дефект 2: хвост хода состоит ТОЛЬКО из «всегда-хвостовых» секций
        # (Контекст хода, Картинки) — без «Память команды»/«заметки». Эвристика
        # должна всё равно узнать хвост и вырезать его, оставив реплику человека.
        text = ("## Контекст хода\nГде: master\n\n---\n\n"
                "## Картинки в этом чате\n- картинка 1\n\n---\n\n"
                "реплика человека")
        self.assertEqual(compactor.human_texts([_user(text)]), ["реплика человека"])

    def test_старый_чат_хвост_все_виды_секций(self):
        # Дефект 2: хвост со ВСЕМИ новыми заголовками (звук, видео, локальная модель,
        # структура кода, привязанные знания) — каждый узнаётся, реплика — после.
        text = ("## Контекст хода\nГде: master\n\n---\n\n"
                "## Картинки в этом чате\n- к1\n\n---\n\n"
                "## Звук в этом чате\n- з1\n\n---\n\n"
                "## Видео в этом чате\n- в1\n\n---\n\n"
                "## Картинки и видео: локальная модель по умолчанию\nправило\n\n---\n\n"
                "## Структура кода проекта (Code Graph)\nхабы\n\n---\n\n"
                "### Привязанные знания и правила\n- источник\n\n---\n\n"
                "реплика человека")
        self.assertEqual(compactor.human_texts([_user(text)]), ["реплика человека"])

    def test_мутация_новые_заголовки_краснеет(self):
        # Мутация: убрать НОВЫЕ заголовки из _TAIL_HEADERS (оставить только исходные два) →
        # эвристика перестаёт узнавать хвост из «всегда-хвостовых» секций → хвост НЕ вырезается,
        # синтетика краснеет (вместо «реплика человека» остаётся весь текст с хвостом).
        orig = compactor._TAIL_HEADERS
        compactor._TAIL_HEADERS = ("## Память команды проекта", "Возможно релевантные заметки")
        try:
            text = ("## Контекст хода\nГде: master\n\n---\n\n"
                    "## Картинки в этом чате\n- картинка 1\n\n---\n\n"
                    "реплика человека")
            out = compactor.human_texts([_user(text)])
            self.assertEqual(out, [text.strip()],
                             "мутация (новые заголовки убраны) не покраснела: хвост всё равно вырезан")
        finally:
            compactor._TAIL_HEADERS = orig

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

    def test_статус_ошибка_run_tests_упало(self):
        # mcp__tests__run_tests: is_error=False, но «упало 3» → ошибка
        msgs = [
            _assistant(_tool_use("m1", "mcp__tests__run_tests", target="X.Tests")),
            _tool_result("m1", "упало 3", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ошибка")

    def test_статус_ошибка_dotnet_test_failed(self):
        # Bash dotnet test: is_error=False, но «Failed: 3» → ошибка
        msgs = [
            _assistant(_tool_use("b1", "Bash", command="dotnet test -m:4")),
            _tool_result("b1", "Failed: 3", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ошибка")

    def test_статус_ошибка_build_failed(self):
        # mcp__dev__build: is_error=False, но «Build FAILED» → ошибка
        msgs = [
            _assistant(_tool_use("m2", "mcp__dev__build", target="backend/X")),
            _tool_result("m2", "Build FAILED", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ошибка")

    def test_статус_ok_failed_0(self):
        # «Failed: 0» → не падение (ноль) → ok
        msgs = [
            _assistant(_tool_use("b2", "Bash", command="dotnet test -m:4")),
            _tool_result("b2", "Failed: 0", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ok")

    def test_статус_ok_fail_safe(self):
        # «FAIL_SAFE ok» в начале строки — имя, а не xUnit-маркер падения
        msgs = [
            _assistant(_tool_use("b4", "Bash", command="python3 -m unittest test_x")),
            _tool_result("b4", "FAIL_SAFE ok\nRan 3 tests\n\nOK", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ok")

    def test_статус_xunit_fail_ошибка(self):
        msgs = [
            _assistant(_tool_use("b5", "Bash", command="dotnet test")),
            _tool_result("b5", "FAIL Ns.Tests.X\n", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ошибка")

    def test_статус_traceback_при_зелёном_итоге_ok(self):
        # Тест печатает трассу ожидаемого исключения, но прогон зелёный
        вывод = "Traceback (most recent call last):\n  ...\nValueError: x\nRan 5 tests\n\nOK"
        msgs = [
            _assistant(_tool_use("b6", "Bash", command="python3 -m unittest t")),
            _tool_result("b6", вывод, is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ok")

    def test_статус_traceback_без_итога_ошибка(self):
        msgs = [
            _assistant(_tool_use("b7", "Bash", command="pytest -q")),
            _tool_result("b7", "Traceback (most recent call last):\nImportError: y",
                         is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "ошибка")

    def test_статус_неизвестно_без_пары(self):
        # tool_use без парного tool_result → неизвестно
        msgs = [_assistant(_tool_use("b3", "Bash", command="dotnet test -m:4"))]
        self.assertEqual(compactor.last_checks(msgs)[0]["status"], "неизвестно")

    def test_npm_run_dev_не_проверка(self):
        # npm run dev (dev-сервер) → не проверка
        msgs = [
            _assistant(_tool_use("n1", "Bash", command="npm run dev")),
            _tool_result("n1", "listening on :3000", is_error=False),
        ]
        self.assertEqual(compactor.last_checks(msgs), [])

    def test_npm_run_build_проверка(self):
        # npm run build → проверка (отдельный вид)
        msgs = [
            _assistant(_tool_use("n2", "Bash", command="npm run build")),
            _tool_result("n2", "built", is_error=False),
        ]
        checks = compactor.last_checks(msgs)
        self.assertEqual(len(checks), 1)
        self.assertEqual(checks[0]["kind"], "npm run build")

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
        # SHA: hex 7-40, хотя бы одна буква a-f, рядом со словом git/commit/коммит
        # (слово целиком, не подстрока «digit»/«legit»).
        # «commit 3ac8eb44d» → SHA (есть буквы a, c, e, b, d)
        a = compactor.anchors([{"role": "user", "content": "commit 3ac8eb44d сделан"}])
        self.assertIn("3ac8eb44d", a)
        # «digit 1234567» → не SHA (только цифры, нет букв a-f; «digit» ≠ «git»)
        b = compactor.anchors([{"role": "user", "content": "digit 1234567"}])
        self.assertNotIn("1234567", b)
        # «20260412» → не SHA (только цифры, нет контекста)
        c = compactor.anchors([{"role": "user", "content": "20260412"}])
        self.assertNotIn("20260412", c)
        # «git log 98765432» → не SHA (есть «git», но только цифры, нет букв a-f)
        d = compactor.anchors([{"role": "user", "content": "git log 98765432"}])
        self.assertNotIn("98765432", d)

    def test_строки_ошибок_из_tool_result(self):
        # строки ошибок — ТОЛЬКО из tool_result (не из прозы человека/ассистента)
        msgs = [
            {"role": "user", "content": "всё хорошо"},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "t1",
                 "content": "error CS0123: member not found\nFAIL: test_x",
                 "is_error": False},
            ]},
        ]
        a = compactor.anchors(msgs)
        self.assertTrue(any("error CS0123" in x for x in a),
                        "строка ошибки из tool_result не найдена")
        self.assertTrue(any("FAIL: test_x" in x for x in a),
                        "строка ошибки из tool_result не найдена")

    def test_строки_ошибок_не_из_прозы(self):
        # проза человека: «ошибка в обработчике, не fail-safe» → не якорь
        a = compactor.anchors([{"role": "user", "content": "ошибка в обработчике, не fail-safe"}])
        self.assertFalse(any("ошибка в обработчике" in x for x in a),
                         "проза человека стала строкой ошибки")

    def test_строка_ошибки_урезана_до_300(self):
        # длинная строка ошибки в tool_result → урезана до 300 кодовых точек
        long_line = "error: " + "x" * 500
        msgs = [
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "t1",
                 "content": long_line, "is_error": False},
            ]},
        ]
        a = compactor.anchors(msgs)
        matching = [x for x in a if x.startswith("error:")]
        self.assertTrue(matching, "длинная строка ошибки не найдена")
        self.assertLessEqual(len(matching[0]), 300,
                            "строка ошибки не урезана до 300")

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
        # постановка (head) + вторая реплика: «Реплики» содержит только вторую (head не
        # дублируется — дефект 1).
        msgs = [
            _user(_marker("постановка задачи")),
            _user(_marker("продолжи с правок")),
            _assistant(_tool_use("1", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("2", "Bash", command="dotnet build -m:4")),
            _tool_result("2", "Build succeeded", is_error=False),
        ]
        block = compactor.build_preserved({"messages": msgs})
        self.assertTrue(block.startswith("<ccs-preserved>"))
        self.assertTrue(block.endswith("</ccs-preserved>"))
        self.assertIn("## Постановка задачи (дословно)", block)
        self.assertIn("## Реплики человека", block)
        self.assertIn("продолжи с правок", block)
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

    def test_cap_порядок_урезания_ступенчато(self):
        # Порядок урезания: якоря → проверки → файлы → старые реплики (постановка и
        # свежая реплика — никогда). Мутация ("anchors","checks","files") → ("plan",)
        # должна покраснеть.
        msgs = [
            _user(_marker("постановка")),
            _user(_marker("старая реплика " + "С" * 2000)),
            _user(_marker("свежая реплика")),
            _assistant(_tool_use("1", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("2", "Bash", command="dotnet build -m:4")),
            _tool_result("2", "ok", is_error=False),
            _assistant(_tool_use("3", "Bash", command="dotnet test -m:4")),
            _tool_result("3", "x" * 500, is_error=False),
            _user("см. /home/an/src/a.py, /home/an/src/b.py, /home/an/src/c.py"),
        ]
        orig_cap = compactor.PRESERVED_CAP
        try:
            # Уровень 1: умеренное превышение → исчезают только якоря
            # (проверки, файлы, старые реплики на месте)
            compactor.PRESERVED_CAP = 2800
            block = compactor.build_preserved({"messages": msgs})
            self.assertLessEqual(len(block), 2800)
            self.assertNotIn("## Якоря", block, "якоря должны исчезнуть на уровне 1")
            self.assertIn("## Последние проверки", block, "проверки на месте на уровне 1")
            self.assertIn("## Изменённые файлы", block, "файлы на месте на уровне 1")
            self.assertIn("старая реплика", block, "старая реплика на месте на уровне 1")
            self.assertIn("постановка", block)
            self.assertIn("свежая реплика", block)

            # Уровень 2: больше → исчезают и проверки (файлы, старые реплики на месте)
            compactor.PRESERVED_CAP = 2195
            block = compactor.build_preserved({"messages": msgs})
            self.assertLessEqual(len(block), 2195)
            self.assertNotIn("## Якоря", block)
            self.assertNotIn("## Последние проверки", block, "проверки должны исчезнуть на уровне 2")
            self.assertIn("## Изменённые файлы", block, "файлы на месте на уровне 2")
            self.assertIn("старая реплика", block, "старая реплика на месте на уровне 2")
            self.assertIn("постановка", block)
            self.assertIn("свежая реплика", block)

            # Уровень 3: ещё больше → исчезают и файлы (старые реплики на месте)
            compactor.PRESERVED_CAP = 2170
            block = compactor.build_preserved({"messages": msgs})
            self.assertLessEqual(len(block), 2170)
            self.assertNotIn("## Якоря", block)
            self.assertNotIn("## Последние проверки", block)
            self.assertNotIn("## Изменённые файлы", block, "файлы должны исчезнуть на уровне 3")
            self.assertIn("старая реплика", block, "старая реплика на месте на уровне 3")
            self.assertIn("постановка", block)
            self.assertIn("свежая реплика", block)

            # Уровень 4: много → исчезают и старые реплики (постановка + свежая — всегда)
            compactor.PRESERVED_CAP = 300
            block = compactor.build_preserved({"messages": msgs})
            self.assertLessEqual(len(block), 300)
            self.assertNotIn("## Якоря", block)
            self.assertNotIn("## Последние проверки", block)
            self.assertNotIn("## Изменённые файлы", block)
            self.assertNotIn("старая реплика", block, "старая реплика должна исчезнуть на уровне 4")
            self.assertIn("постановка", block, "постановка — всегда")
            self.assertIn("свежая реплика", block, "свежая реплика — всегда")
        finally:
            compactor.PRESERVED_CAP = orig_cap

    def test_instruction_addendum_английский(self):
        text = compactor.instruction_addendum()
        self.assertIsInstance(text, str)
        self.assertIn("<ccs-preserved>", text)
        self.assertIn("reasoning", text)
        self.assertIn("Keep your answer in the required", text)
        self.assertIn("<analysis>/<summary>", text)
        self.assertIn("after your summary", text, "блок встаёт после сводки, а не перед ней")

    def test_постановка_не_дублируется_в_репликах(self):
        # Дефект 1: постановка (head, index 0) уже в «Постановке» — из «Реплик» её
        # исключаем. Две одинаковые короткие «Продолжай» (index 1, 2) НЕ схлопываются:
        # идентичность — по позиции в истории, не по тексту.
        msgs = [
            _user(_marker("постановка задачи")),
            _user(_marker("Продолжай")),
            _user(_marker("Продолжай")),
        ]
        block = compactor.build_preserved({"messages": msgs})
        self.assertIn("## Постановка задачи (дословно)", block)
        self.assertIn("## Реплики человека", block)
        # постановка (нижний регистр) встречается ровно один раз — только в «Постановке»
        self.assertEqual(block.count("постановка задачи"), 1,
                         "постановка продублирована в «Репликах»")
        # обе «Продолжай» остались (не схлопнулись)
        self.assertEqual(block.count("Продолжай"), 2,
                         "одинаковые короткие реплики схлопнулись")

    def test_одна_реплика_равна_постановке(self):
        # Дефект 1: единственная реплика = постановка; «Реплики» пустые, блок — только
        # «Постановка» (без дубля).
        msgs = [_user(_marker("единственная постановка"))]
        block = compactor.build_preserved({"messages": msgs})
        self.assertIn("## Постановка задачи", block)
        self.assertNotIn("## Реплики человека", block,
                         "единственная реплика-постановка продублирована в «Репликах»")


# --- Шаг 5: экранирование <analysis>/<summary> в блоке <ccs-preserved> ----------------------

class Step5EscapeCliTagsTests(unittest.TestCase):
    """CLI 2.1.301 режет первый <analysis>…</analysis> и первый <summary>(…)</summary> по
    ПЕРВОМУ вхождению; блок <ccs-preserved> дописывается ПОСЛЕ сводки. Если модель не написала
    свой <analysis>/<summary>, а в блоке есть такие теги (дословная реплика, якорь, вывод
    проверки), CLI вырежет кусок блока. Экранируем их (< → ‹), чтобы разбор CLI не задел."""

    def _блок_с_тегами(self):
        # дословная реплика содержит <analysis>x</analysis> и <summary>y</summary>
        raw = "обсуждение: <analysis>x</analysis> и <summary>y</summary> конец"
        msgs = [_user(_marker(raw))]
        return compactor.build_preserved({"messages": msgs})

    def test_реплика_с_тегами_экранированы(self):
        block = self._блок_с_тегами()
        # ни одного НАСТОЯЩЕГО тега (с <) — разбор CLI по первому вхождению их не задел
        self.assertNotIn("<analysis>", block)
        self.assertNotIn("</analysis>", block)
        self.assertNotIn("<summary>", block)
        self.assertNotIn("</summary>", block)
        # текст читаем: экранированные теги на месте, wrapper <ccs-preserved> не тронут
        self.assertIn("‹analysis>x‹/analysis>", block)
        self.assertIn("‹summary>y‹/summary>", block)
        self.assertTrue(block.startswith("<ccs-preserved>"))
        self.assertTrue(block.endswith("</ccs-preserved>"))

    def test_мутация_убрать_экранирование_краснеет(self):
        # мутация: экранирование → идентичность → в блоке настоящие теги → assertNotIn краснеет
        orig = compactor._escape_cli_tags
        compactor._escape_cli_tags = lambda t: t
        try:
            block = self._блок_с_тегами()
        finally:
            compactor._escape_cli_tags = orig
        self.assertIn("<analysis>", block,
                      "мутация (экранирование убрано) не покраснела: настоящих тегов нет")
        self.assertIn("<summary>", block,
                     "мутация (экранирование убрано) не покраснела: настоящих тегов нет")

    def test_как_cli_блок_целиком_сохранён(self):
        # Разбор «как CLI» (test_proxy.как_cli) на ответе модели БЕЗ <analysis>: модель
        # написала только <summary>сводка</summary>, блок с тегами дописан после. CLI вырезает
        # ПЕРВЫЙ <analysis>…</analysis> — если в блоке настоящие теги, вырежет кусок блока.
        # С экранированием (< → ‹) разбор их не видит → блок целиком сохранён.
        блок = self._блок_с_тегами()
        # один текстовый блок: сводка модели + блок (как в реальном ответе сжатия)
        список = [
            ("content_block_start", {"type": "content_block_start", "index": 0,
                                     "content_block": {"type": "text", "text": ""}}),
            ("content_block_delta", {"type": "content_block_delta", "index": 0,
                                      "delta": {"type": "text_delta",
                                                 "text": "<summary>сводка</summary>\n" + блок}}),
            ("content_block_stop", {"type": "content_block_stop", "index": 0}),
        ]
        запись = test_proxy.как_cli(список)
        # сводка модели распознана (Summary:), блок целиком дожил: wrapper и экранированные
        # теги на месте, кусок блока не вырезан
        self.assertTrue(запись.startswith("Summary:\nсводка"),
                        "сводка модели не распознана: " + repr(запись[:80]))
        self.assertIn("<ccs-preserved>", запись)
        self.assertIn("</ccs-preserved>", запись)
        self.assertIn("‹analysis>x‹/analysis>", запись,
                      "CLI вырезал <analysis> из блока: экранирование не сработало")
        self.assertIn("‹summary>y‹/summary>", запись,
                      "CLI вырезал <summary> из блока: экранирование не сработало")

    def test_как_cli_мутация_убрать_экранирование_краснеет(self):
        # мутация: экранирование → идентичность → в блоке настоящие <analysis>/<summary> →
        # как_cli вырезает первый <analysis>…</analysis> (кусок блока) → экранированная форма
        # в записи отсутствует → тест краснеет.
        orig = compactor._escape_cli_tags
        compactor._escape_cli_tags = lambda t: t
        try:
            блок = self._блок_с_тегами()
        finally:
            compactor._escape_cli_tags = orig
        # sanity: без экранирования в блоке настоящие теги
        self.assertIn("<analysis>", блок)
        список = [
            ("content_block_start", {"type": "content_block_start", "index": 0,
                                     "content_block": {"type": "text", "text": ""}}),
            ("content_block_delta", {"type": "content_block_delta", "index": 0,
                                      "delta": {"type": "text_delta",
                                                 "text": "<summary>сводка</summary>\n" + блок}}),
            ("content_block_stop", {"type": "content_block_stop", "index": 0}),
        ]
        запись = test_proxy.как_cli(список)
        # без экранирования CLI вырезает <analysis>x</analysis> из блока → экранированной
        # (‹) формы в записи нет
        self.assertNotIn("‹analysis>x‹/analysis>", запись,
                         "мутация (экранирование убрано) не покраснела: CLI не вырезал тег")


# --- Шаг 8: повторное сжатие — перенос прошлого <ccs-preserved> в новый блок ---------------

def _cli_summary_msg(block, summary="сводка модели"):
    """Запись сжатия CLI: user-сообщение с фиксированным префиксом, сводкой и нашим блоком
    (как её раскладывает CLI: «Summary:» + текст + блок вне тегов)."""
    return _user(
        "This session is being continued from a previous conversation that ran out of "
        "context. Summary of the conversation, whose transcript may be found at:\n"
        "- Primary request: ...\n\n"
        "Summary:\n" + summary + "\n" + block)


def _section(block, title):
    """Текст секции блока по заголовку (для проверок); None — секции нет."""
    lines = block.split("\n")
    out, found = [], False
    for ln in lines:
        if ln == title:
            found = True
            continue
        if found and (ln.startswith("## ") or ln == "</ccs-preserved>"):
            break
        if found:
            out.append(ln)
    return "\n".join(out).strip() if found else None


class Step8RepeatCompactionTests(unittest.TestCase):
    """Повторное сжатие: прошлый блок переносится в новый (постановка, реплики, файлы,
    проверки, план, якоря)."""

    def _первое_сжатие(self):
        """Блок первого сжатия по оригинальной истории (постановка, ранняя поправка, файл,
        падающая проверка)."""
        orig = [
            _user(_marker("исходная постановка задачи")),
            _user(_marker("ранняя поправка: чини парсер")),
            _assistant(_tool_use("1", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("2", "Bash", command="dotnet test -m:4")),
            _tool_result("2", "Failed: 3", is_error=False),
        ]
        return compactor.build_preserved({"messages": orig})

    def test_двойное_сжатие_перенос(self):
        block1 = self._первое_сжатие()
        self.assertIsNotNone(block1)
        # sanity: в первом блоке постановка = исходная, проверка упала
        self.assertEqual(_section(block1, compactor.SEC_HEAD), "исходная постановка задачи")
        self.assertIn("Failed: 3", _section(block1, compactor.SEC_CHECKS))

        # Второе сжатие: история = запись CLI (с блоком1) + новые реплики/файлы/проверки.
        msgs2 = [
            _cli_summary_msg(block1),
            _user(_marker("продолжай")),
            _user(_marker("новая поправка: и тесты тоже")),
            _assistant(_tool_use("3", "Edit", file_path="a/b.py")),
            _assistant(_tool_use("4", "Edit", file_path="c/d.py")),
            _assistant(_tool_use("5", "Bash", command="dotnet test -m:4")),
            _tool_result("5", "Failed: 0", is_error=False),
        ]
        block2 = compactor.build_preserved({"messages": msgs2})
        self.assertIsNotNone(block2)

        # (1) Постановка = исходная, а НЕ «продолжай» (первая реплика после сжатия).
        self.assertEqual(_section(block2, compactor.SEC_HEAD), "исходная постановка задачи")
        # (2) Ранняя поправка из прошлого блока на месте (в «Репликах»).
        self.assertIn("ранняя поправка: чини парсер", _section(block2, compactor.SEC_HUMANS))
        # (3) Файлы из обоих периодов: a/b.py (правки из обоих — сумма) и c/d.py (новый).
        files = _section(block2, compactor.SEC_FILES)
        self.assertIn("a/b.py (2 правок)", files)
        self.assertIn("c/d.py (1 правок)", files)
        # (4) Проверка обновилась: по виду dotnet test — самая свежая (ok), не старая «ошибка».
        checks = _section(block2, compactor.SEC_CHECKS)
        self.assertIn("ok", checks)
        self.assertNotIn("ошибка", checks)

    def test_двойное_сжатие_мутация_постановки(self):
        # Мутация: прошлый блок не найден (перенос постановки убран) → постановка становится
        # «продолжай» (первая реплика после сжатия), а не исходной → тест краснеет.
        orig_prev = compactor._prev_block
        compactor._prev_block = lambda msgs: None
        try:
            block1 = self._первое_сжатие()
            msgs2 = [
                _cli_summary_msg(block1),
                _user(_marker("продолжай")),
            ]
            block2 = compactor.build_preserved({"messages": msgs2})
            self.assertEqual(_section(block2, compactor.SEC_HEAD), "продолжай",
                             "мутация (перенос постановки убран) не покраснела")
        finally:
            compactor._prev_block = orig_prev

    def test_тройное_сжатие_блок_не_растёт(self):
        # Тройное сжатие: блок держится в PRESERVED_CAP, постановка всё та же.
        block1 = self._первое_сжатие()
        # второе
        msgs2 = [
            _cli_summary_msg(block1),
            _user(_marker("продолжай")),
            _assistant(_tool_use("3", "Edit", file_path="c/d.py")),
        ]
        block2 = compactor.build_preserved({"messages": msgs2})
        self.assertIsNotNone(block2)
        self.assertLessEqual(len(block2), compactor.PRESERVED_CAP)
        self.assertEqual(_section(block2, compactor.SEC_HEAD), "исходная постановка задачи")
        # третье
        msgs3 = [
            _cli_summary_msg(block2),
            _user(_marker("ещё реплика " + "Р" * 5000)),
            _assistant(_tool_use("4", "Edit", file_path="e/f.py")),
        ]
        block3 = compactor.build_preserved({"messages": msgs3})
        self.assertIsNotNone(block3)
        self.assertLessEqual(len(block3), compactor.PRESERVED_CAP,
                            "тройное сжатие: блок вырос за PRESERVED_CAP")
        self.assertEqual(_section(block3, compactor.SEC_HEAD), "исходная постановка задачи",
                         "после тройного сжатия постановка изменилась")

    def test_прошлый_блок_с_экранированными_тегами(self):
        # Прошлый блок, содержащий экранированные ‹analysis>/‹summary>, разбирается корректно:
        # экранированные теги остаются как есть (не превращаются обратно в настоящие).
        raw = "обсуждение: <analysis>x</analysis> и <summary>y</summary>"
        orig = [_user(_marker("постановка")), _user(_marker(raw))]
        block1 = compactor.build_preserved({"messages": orig})
        self.assertIn("‹analysis>x‹/analysis>", block1)
        msgs2 = [_cli_summary_msg(block1), _user(_marker("продолжай"))]
        block2 = compactor.build_preserved({"messages": msgs2})
        # экранированные теги перенеслись как есть (не <analysis>, а ‹analysis>)
        self.assertIn("‹analysis>x‹/analysis>", block2)
        self.assertNotIn("<analysis>x", block2)

    def test_мусор_вместо_блока(self):
        # Мусор вместо блока (нет закрывающего тега) → поведение как без блока, без исключения.
        junk = _user(
            "This session is being continued from a previous conversation that ran out of "
            "context. Summary of the conversation, whose transcript may be found at:\n"
            "- Primary request: ...\n\nSummary:\nсводка\n<ccs-preserved>\n## Постановка задачи "
            "(дословно)\nисходная постановка\n</ccs-preserved"  # закрывающий тег оборван
        )
        msgs = [junk, _user(_marker("продолжай"))]
        block = compactor.build_preserved({"messages": msgs})
        self.assertIsNotNone(block)
        # как без блока: постановка = первая реплика текущей истории («продолжай»)
        self.assertEqual(_section(block, compactor.SEC_HEAD), "продолжай")

    def test_сводка_без_блока_поведение_как_раньше(self):
        # Сводка CLI без нашего блока (сжатие с выключенным флагом) → переноса нет.
        summary = _user(
            "This session is being continued from a previous conversation that ran out of "
            "context. Summary of the conversation, whose transcript may be found at:\n"
            "- Primary request: ...\n\nSummary:\nсводка без блока"
        )
        msgs = [summary, _user(_marker("продолжай"))]
        block = compactor.build_preserved({"messages": msgs})
        self.assertIsNotNone(block)
        self.assertEqual(_section(block, compactor.SEC_HEAD), "продолжай")


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

    def test_корпус_блок_без_хвоста_и_постановка_один_раз(self):
        # Дефекты 1+2 на корпусе: (а) блок не содержит НОВЫХ заголовков хвоста хода
        # (те, что раньше эвристика не узнавала); (б) постановка (head) встречается в блоке
        # ровно один раз (не продублирована в «Репликах»).
        #
        # Проверяем только НОВЫЕ заголовки (с «##»/«###»): существующий
        # «Возможно релевантные заметки» (без «##») неоднозначен — он подстрокой
        # совпадает с «## Возможно релевантные заметки» внутри текста задачи (head),
        # а это часть постановки, а не хвост.
        new_headers = [h for h in compactor._TAIL_HEADERS if h.startswith("#")]
        self.assertGreaterEqual(len(new_headers), 7, "в константе меньше 7 новых заголовков")
        for fn, item in self._items():
            history = item.get("history") or []
            block = compactor.build_preserved({"messages": history})
            if block is None:
                continue
            # (а) ни один НОВЫЙ заголовок хвоста хода не в блоке
            for h in new_headers:
                self.assertNotIn(h, block, f"{fn}: новый заголовок хвоста {h!r} остался в блоке")
            # (б) постановка ровно один раз (head[:200] — в блоке только в «Постановке»)
            head = compactor.head(history)
            if head:
                n = block.count(head[:200])
                self.assertEqual(n, 1, f"{fn}: постановка встречается {n} раз (ожидалось 1)")
            print(f"  {fn}: block={len(block)}, new-tail-headers=0, head=1x")


if __name__ == "__main__":
    unittest.main()
