# coding: utf-8
import importlib.util
import json
import os
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "mig", os.path.join(os.path.dirname(__file__), "migrate-lora-manifests.py"))
mig = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mig)

OWNER = "a300cc8f-0074-47ab-865c-138cd39c2c18"
KEY = "a300cc8f007447ab865c138cd39c2c18"


class MigrateLoraManifestsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = os.path.join(self.tmp.name, "data")
        os.makedirs(self.data)
        self.proj = os.path.join(self.tmp.name, "proj")
        self.local = os.path.join(self.tmp.name, "localproj")
        with open(os.path.join(self.data, "users.json"), "w", encoding="utf-8") as f:
            json.dump({"users": [{"Id": OWNER, "Username": "andrey", "Role": "admin"}]}, f)
        with open(os.path.join(self.data, "projects.json"), "w", encoding="utf-8") as f:
            json.dump([
                {"Id": "1", "Name": "Серверный", "RootPath": self.proj, "DeviceId": None, "OwnerId": OWNER},
                {"Id": "2", "Name": "Локальный", "RootPath": self.local, "DeviceId": "dev", "OwnerId": OWNER},
            ], f)
        self.old = self._char(self.proj, "a", "andreyk/x.safetensors")
        self.done = self._char(self.proj, "b", f"ccs/{KEY}/y.safetensors")
        self.loc = self._char(self.local, "c", "z.safetensors")

    def tearDown(self):
        self.tmp.cleanup()

    def _char(self, root, slug, lora):
        d = os.path.join(root, "characters", slug)
        os.makedirs(d)
        p = os.path.join(d, "character.json")
        with open(p, "w", encoding="utf-8") as f:
            f.write('{\n  "slug": "%s",\n  "providers": {\n    "local": {\n      "lora": "%s",\n      "strength": 0.8\n    }\n  }\n}' % (slug, lora))
        return p

    def _read(self, p):
        with open(p, encoding="utf-8") as f:
            return f.read()

    def run_cli(self, *extra):
        return mig.main(["--data-dir", self.data, "--username", "andrey", *extra])

    def test_dry_run_ничего_не_пишет(self):
        before = self._read(self.old)
        self.run_cli()
        self.assertEqual(before, self._read(self.old))
        self.assertEqual(["character.json"], os.listdir(os.path.dirname(self.old)))

    def test_apply_пишет_и_делает_бэкап(self):
        before = self._read(self.old)
        self.run_cli("--apply")
        after = json.loads(self._read(self.old))
        self.assertEqual(f"ccs/{KEY}/andreyk/x.safetensors", after["providers"]["local"]["lora"])
        self.assertEqual(0.8, after["providers"]["local"]["strength"])
        baks = [n for n in os.listdir(os.path.dirname(self.old)) if ".bak-" in n]
        self.assertEqual(1, len(baks))
        self.assertEqual(before, self._read(os.path.join(os.path.dirname(self.old), baks[0])))
        self.assertFalse(os.path.exists(self.old + ".tmp"))

    def test_уже_мигрированное_не_трогается(self):
        before = self._read(self.done)
        self.run_cli("--apply")
        self.assertEqual(before, self._read(self.done))
        self.assertEqual(["character.json"], os.listdir(os.path.dirname(self.done)))

    def test_локальный_проект_пропущен(self):
        before = self._read(self.loc)
        self.run_cli("--apply")
        self.assertEqual(before, self._read(self.loc))

    def test_манифест_без_поля_lora_не_переписывается(self):
        path = os.path.join(self.tmp.name, "manifest.json")
        text = json.dumps({"providers": {"local": {"other": 1}}})
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        with self.assertRaises(RuntimeError) as ctx:
            mig.apply_item({"path": path, "old": "a.safetensors", "new": "ccs/k/a.safetensors"})
        self.assertIn("поле lora не найдено", str(ctx.exception))
        self.assertEqual(text, self._read(path))
        self.assertEqual([], [n for n in os.listdir(self.tmp.name) if ".bak" in n or n.endswith(".tmp")])

    def test_lora_с_другим_значением_не_переписывается(self):
        path = os.path.join(self.tmp.name, "manifest.json")
        text = json.dumps({"providers": {"local": {"lora": "other.safetensors"}}})
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        with self.assertRaises(RuntimeError):
            mig.apply_item({"path": path, "old": "a.safetensors", "new": "ccs/k/a.safetensors"})
        self.assertEqual(text, self._read(path))


if __name__ == "__main__":
    unittest.main()
