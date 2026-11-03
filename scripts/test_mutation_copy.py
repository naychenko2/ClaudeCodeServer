# coding: utf-8
# Тесты scripts/mutation-copy.sh. Запуск: python3 -I scripts/test_mutation_copy.py
#
# Синтетические тесты гоняют скрипт на крошечном git-репозитории (секунды). Тест тёплой
# сборки берёт ЭТО дерево и требует, чтобы оно было собрано (есть obj/ у
# ClaudeHomeServer.Memory.Tests): иначе он пропускается с причиной, а не собирает решение с нуля.
import os
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


class Base(unittest.TestCase):
    def setUp(self):
        os.makedirs(CACHE, exist_ok=True)
        self.tmp = tempfile.mkdtemp(prefix="ccs-mutation-test-", dir=CACHE)
        self.root = os.path.join(self.tmp, "root")
        self.env = dict(os.environ, CCS_MUTATION_ROOT=self.root)
        self.procs = []

    def tearDown(self):
        for p in self.procs:
            p.kill()
            p.wait()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def create(self, src, extra_env=None):
        env = dict(self.env, **(extra_env or {}))
        out = run(["create", "--from", src], env).stdout.strip().splitlines()
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
