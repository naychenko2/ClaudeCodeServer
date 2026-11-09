# coding: utf-8
# Тесты scripts/mutation-copy.sh. Запуск: python3 -I scripts/test_mutation_copy.py
#
# Синтетические тесты гоняют скрипт на крошечном git-репозитории (секунды). Тест тёплой
# сборки берёт ЭТО дерево и требует, чтобы оно было собрано (есть obj/ у
# ClaudeHomeServer.Memory.Tests): иначе он пропускается с причиной, а не собирает решение с нуля.
import os
import re
import shutil
import subprocess
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "mutation-copy.sh")
REPO = os.path.dirname(HERE)
CACHE = os.path.expanduser("~/.cache")


def run(args, env, cwd=None, check=True):
    p = subprocess.run(["bash", SCRIPT] + args, env=env, cwd=cwd, text=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check and p.returncode != 0:
        raise AssertionError("скрипт упал (%d): %s\n%s" % (p.returncode, args, p.stderr))
    return p


def git(cwd, *args):
    return subprocess.run(["git", "-C", cwd] + list(args), check=True, text=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def worktrees(repo):
    out = git(repo, "worktree", "list", "--porcelain")
    return [l[len("worktree "):] for l in out.splitlines() if l.startswith("worktree ")]


def built_commit(project_dir):
    """Коммит, на котором собран obj проекта (из AssemblyInformationalVersion), или None."""
    d = os.path.join(project_dir, "obj", "Debug", "net10.0")
    for name in os.listdir(d) if os.path.isdir(d) else []:
        if name.endswith(".AssemblyInfo.cs"):
            m = re.search(r'AssemblyInformationalVersionAttribute\("[^"+]*\+([0-9a-f]{7,})"\)',
                          open(os.path.join(d, name)).read())
            return m.group(1) if m else None
    return None


class Base(unittest.TestCase):
    def setUp(self):
        os.makedirs(CACHE, exist_ok=True)
        self.tmp = tempfile.mkdtemp(prefix="ccs-mutation-test-", dir=CACHE)
        self.root = os.path.join(self.tmp, "root")
        self.env = dict(os.environ, CCS_MUTATION_ROOT=self.root)
        self.procs = []
        self.created = []

    def tearDown(self):
        for p in self.procs:
            p.kill()
            p.wait()
        # Упавший тест не должен оставлять запись worktree в общем репозитории.
        for dst in self.created:
            if os.path.exists(dst):
                run(["remove", dst], self.env, check=False)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def create(self, src, extra_env=None):
        env = dict(self.env, **(extra_env or {}))
        out = run(["create", "--from", src], env).stdout.strip().splitlines()
        self.created.append(out[-1])
        return out[-1]


class SyntheticTests(Base):
    OLD = 1_600_000_000  # mtime «старой» сборки

    def setUp(self):
        super().setUp()
        self.src = os.path.join(self.tmp, "src-tree")
        os.makedirs(os.path.join(self.src, "backend", "App", "obj", "Debug"))
        os.makedirs(os.path.join(self.src, "backend", "App", "bin", "Debug", "net10.0", "ref"))
        self.write("backend/App/App.cs", "class App {}\n")
        self.write("backend/App/Tracked.cs", "class T {}\n")
        self.write(".gitignore", "bin/\nobj/\nignored.log\n")
        git(self.src, "init", "-q")
        git(self.src, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A")
        git(self.src, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init")
        # Незакоммиченное: правка отслеживаемого, новый файл, удалённый файл, игнорируемый мусор.
        self.write("backend/App/App.cs", "class App { int mutated; }\n")
        self.write("backend/App/New.cs", "class N {}\n")
        os.remove(os.path.join(self.src, "backend", "App", "Tracked.cs"))
        self.write("ignored.log", "мусор\n")
        # Тёплые артефакты: obj целиком, у bin — только ref.
        self.write("backend/App/obj/Debug/App.dll", "obj-dll")
        self.write("backend/App/obj/Debug/App.csproj.FileListAbsolute.txt",
                   os.path.join(self.src, "backend/App/bin/Debug/App.dll") + "\n")
        self.write("backend/App/bin/Debug/net10.0/ref/App.dll", "ref-dll")
        self.write("backend/App/bin/Debug/net10.0/App.dll", "bin-dll")
        for rel in ("backend/App/App.cs", "backend/App/obj/Debug/App.dll",
                    "backend/App/obj/Debug/App.csproj.FileListAbsolute.txt"):
            os.utime(os.path.join(self.src, rel), (self.OLD, self.OLD))
        self.env["CCS_MUTATION_NO_PRIME"] = "1"  # dotnet в синтетике не нужен

    def write(self, rel, text):
        path = os.path.join(self.src, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as f:
            f.write(text)

    def read(self, base, rel):
        with open(os.path.join(base, rel)) as f:
            return f.read()

    def test_create_несёт_незакоммиченное_и_тёплый_obj_под_корнем(self):
        dst = self.create(self.src)
        self.assertEqual(os.path.realpath(self.root), os.path.dirname(dst))
        self.assertTrue(os.path.basename(dst).startswith("src-tree-"))
        self.assertEqual("class App { int mutated; }\n", self.read(dst, "backend/App/App.cs"))
        self.assertTrue(os.path.exists(os.path.join(dst, "backend/App/New.cs")))
        self.assertFalse(os.path.exists(os.path.join(dst, "backend/App/Tracked.cs")))
        self.assertFalse(os.path.exists(os.path.join(dst, "ignored.log")))
        # obj и bin/ref приехали с mtime источника, остальной bin — нет.
        self.assertEqual("obj-dll", self.read(dst, "backend/App/obj/Debug/App.dll"))
        self.assertEqual(self.OLD, int(os.path.getmtime(os.path.join(dst, "backend/App/obj/Debug/App.dll"))))
        self.assertEqual(self.OLD, int(os.path.getmtime(os.path.join(dst, "backend/App/App.cs"))))
        self.assertEqual("ref-dll", self.read(dst, "backend/App/bin/Debug/net10.0/ref/App.dll"))
        self.assertFalse(os.path.exists(os.path.join(dst, "backend/App/bin/Debug/net10.0/App.dll")))
        # Абсолютный путь источника в obj переписан на путь копии, mtime сохранён.
        lst = "backend/App/obj/Debug/App.csproj.FileListAbsolute.txt"
        self.assertEqual(os.path.join(dst, "backend/App/bin/Debug/App.dll") + "\n", self.read(dst, lst))
        self.assertEqual(self.OLD, int(os.path.getmtime(os.path.join(dst, lst))))
        # Копия — worktree на коммит источника, правки видны как diff, служебного мусора нет.
        self.assertIn(dst, worktrees(self.src))
        self.assertEqual(git(self.src, "rev-parse", "HEAD"), git(dst, "rev-parse", "HEAD"))
        status = sorted(git(dst, "status", "--porcelain").splitlines())
        self.assertEqual([" D backend/App/Tracked.cs", " M backend/App/App.cs", "?? backend/App/New.cs"], status)

    def stub(self, name, body):
        """Заглушка команды в PATH на время теста."""
        d = os.path.join(self.tmp, "stub-bin")
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name)
        with open(path, "w") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(path, 0o755)
        self.env["PATH"] = d + os.pathsep + self.env.get("PATH", "")

    def test_create_не_падает_молча_на_проекте_без_obj_и_свежем_кеше(self):
        # Единственный (значит, последний по порядку) .csproj — без obj; App с obj без .csproj
        # держит пересчёт хешей включённым. Заглушка dotnet кладёт кеш, которого нет в источнике:
        # `cond && cmd` последним в цикле давал код 1 в обоих местах prime_compile_cache.
        self.write("backend/Lib/Lib.csproj", "<Project />\n")
        self.env.pop("CCS_MUTATION_NO_PRIME")
        self.stub("dotnet", "mkdir -p backend/App/obj/Debug\n"
                            ": > backend/App/obj/Debug/Fresh.CoreCompileInputs.cache\n")
        p = run(["create", "--from", self.src], self.env, check=False)
        lines = p.stdout.strip().splitlines()
        if lines:
            self.created.append(lines[-1])
        self.assertEqual(0, p.returncode, p.stderr)
        dst = lines[-1]
        self.assertTrue(os.path.exists(os.path.join(dst, "backend/App/obj/Debug/Fresh.CoreCompileInputs.cache")),
                        "заглушка dotnet не вызвана — пересчёт хешей не шёл")
        self.assertIn(dst, worktrees(self.src))

    def test_create_при_сбое_в_середине_убирает_копию_и_пишет_причину(self):
        # rsync тёплых obj (второй вызов) падает — после worktree add и метаданных.
        real = shutil.which("rsync")
        self.stub("rsync", 'case "$*" in *--prune-empty-dirs*) echo "rsync: сбой-заглушка" >&2; exit 23;; esac\n'
                           'exec "%s" "$@"\n' % real)
        p = run(["create", "--from", self.src], self.env, check=False)
        self.assertNotEqual(0, p.returncode)
        self.assertEqual("", p.stdout)
        self.assertIn("create прерван", p.stderr)
        self.assertIn("rsync", p.stderr)
        self.assertEqual([".meta"], os.listdir(self.root))
        self.assertEqual([], os.listdir(os.path.join(self.root, ".meta")))
        self.assertEqual([os.path.realpath(self.src)], [os.path.realpath(w) for w in worktrees(self.src)])

    def test_remove_убирает_каталог_и_запись_worktree(self):
        dst = self.create(self.src)
        run(["remove", dst], self.env)
        self.assertFalse(os.path.exists(dst))
        self.assertNotIn(dst, worktrees(self.src))
        self.assertEqual([], os.listdir(os.path.join(self.root, ".meta")))

    def test_remove_вне_корня_отказ(self):
        outside = os.path.join(self.tmp, "outside")
        os.makedirs(outside)
        p = run(["remove", outside], self.env, check=False)
        self.assertNotEqual(0, p.returncode)
        self.assertTrue(os.path.isdir(outside))
        p = run(["remove", self.src], self.env, check=False)
        self.assertNotEqual(0, p.returncode)
        self.assertTrue(os.path.isdir(self.src))

    def test_remove_служебного_каталога_отказ(self):
        self.create(self.src)
        p = run(["remove", os.path.join(self.root, ".meta")], self.env, check=False)
        self.assertNotEqual(0, p.returncode)
        self.assertTrue(os.path.isdir(os.path.join(self.root, ".meta")))

    def age(self, dst, hours):
        meta = os.path.join(self.root, ".meta", os.path.basename(dst))
        with open(meta) as f:
            lines = [l for l in f.read().splitlines() if not l.startswith("created=")]
        lines.append("created=%d" % (time.time() - hours * 3600))
        with open(meta, "w") as f:
            f.write("\n".join(lines) + "\n")

    def test_gc_удаляет_старую_и_не_трогает_живую_и_свежую(self):
        old, live, fresh = self.create(self.src), self.create(self.src), self.create(self.src)
        self.age(old, 30)
        self.age(live, 30)
        self.procs.append(subprocess.Popen(["sleep", "600"], cwd=os.path.join(live, "backend")))
        p = run(["gc", "--older-than-hours", "24"], self.env)
        self.assertFalse(os.path.exists(old), p.stdout + p.stderr)
        self.assertNotIn(old, worktrees(self.src))
        self.assertTrue(os.path.isdir(live))
        self.assertTrue(os.path.isdir(fresh))
        self.assertIn("живой процесс", p.stderr)

    def test_create_сам_убирает_старые_копии_кроме_живой_и_свежей(self):
        old, live, fresh = self.create(self.src), self.create(self.src), self.create(self.src)
        self.age(old, 30)
        self.age(live, 30)
        self.procs.append(subprocess.Popen(["sleep", "600"], cwd=os.path.join(live, "backend")))
        p = run(["create", "--from", self.src], self.env)
        new = p.stdout.strip().splitlines()[-1]
        self.created.append(new)
        self.assertEqual([new], p.stdout.strip().splitlines(), "уборка молчит: stdout — только путь копии")
        self.assertFalse(os.path.exists(old))
        self.assertNotIn(old, worktrees(self.src))
        self.assertFalse(os.path.exists(os.path.join(self.root, ".meta", os.path.basename(old))))
        for d in (live, fresh, new):
            self.assertTrue(os.path.isdir(d), d)

    def test_create_не_падает_при_сбое_уборки(self):
        broken = self.create(self.src)
        # Битое время создания: арифметика gc падает с ошибкой разбора — уборка рушится целиком
        meta = os.path.join(self.root, ".meta", os.path.basename(broken))
        with open(meta, "w") as f:
            f.write("created=1 +\n")
        p = run(["create", "--from", self.src], self.env, check=False)
        lines = p.stdout.strip().splitlines()
        if lines:
            self.created.append(lines[-1])
        self.assertEqual(0, p.returncode, p.stderr)
        self.assertIn(lines[-1], worktrees(self.src))
        self.assertTrue(os.path.isdir(broken))
        g = run(["gc"], self.env, check=False)
        self.assertNotEqual(0, g.returncode, "сбой уборки настоящий: тот же gc напрямую падает")

    def test_gc_видит_открытый_файл_а_не_только_cwd(self):
        dst = self.create(self.src)
        self.age(dst, 30)
        held = os.path.join(dst, "backend/App/App.cs")
        self.procs.append(subprocess.Popen(["bash", "-c", "exec 3<\"$1\"; sleep 600", "_", held], cwd=self.tmp))
        time.sleep(0.3)
        run(["gc", "--older-than-hours", "24"], self.env)
        self.assertTrue(os.path.isdir(dst))


class WarmBuildTests(Base):
    PROJ = "ClaudeHomeServer.Memory.Tests"
    DEPS = ("ClaudeHomeServer.Memory", "ClaudeHomeServer.Core")

    def obj_dlls_newer(self, tree, stamp):
        got = set()
        for name in (self.PROJ,) + self.DEPS:
            d = os.path.join(tree, "backend", name, "obj", "Debug", "net10.0")
            dll = os.path.join(d, name + ".dll")
            if os.path.exists(dll) and os.path.getmtime(dll) > stamp:
                got.add(name)
        return got

    def test_правка_одного_cs_пересобирает_только_его_проект(self):
        if shutil.which("dotnet") is None:
            self.skipTest("нет dotnet")
        for name in (self.PROJ,) + self.DEPS:
            if not os.path.exists(os.path.join(REPO, "backend", name, "obj", "Debug", "net10.0", name + ".dll")):
                self.skipTest("дерево не собрано: нет obj у %s — сначала соберите решение" % name)
        built = built_commit(os.path.join(REPO, "backend", self.PROJ))
        head = git(REPO, "rev-parse", "HEAD").strip()
        if built and not head.startswith(built):
            self.skipTest("obj собран на коммите %s, HEAD %s: хеш коммита в AssemblyInformationalVersion "
                          "пересоберёт всё и без копии — сначала соберите решение" % (built[:9], head[:9]))
        dst = self.create(REPO)
        self.assertIn(dst, worktrees(REPO))
        with open(os.path.join(dst, "backend", self.PROJ, "GlobalUsings.cs"), "a") as f:
            f.write("// мутация из test_mutation_copy\n")
        stamp = time.time()
        time.sleep(1.1)
        b = subprocess.run(["dotnet", "build", os.path.join("backend", self.PROJ), "-m:4", "-nologo", "-v:q"],
                           cwd=dst, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.assertEqual(0, b.returncode, b.stdout[-3000:])
        self.assertEqual({self.PROJ}, self.obj_dlls_newer(dst, stamp),
                         "пересобрано больше одного проекта — тёплая копия не тёплая")
        self.assertEqual(set(), self.obj_dlls_newer(REPO, stamp), "сборка в копии тронула источник")
        run(["remove", dst], self.env)
        self.assertFalse(os.path.exists(dst))
        self.assertNotIn(dst, worktrees(REPO))


if __name__ == "__main__":
    unittest.main()
