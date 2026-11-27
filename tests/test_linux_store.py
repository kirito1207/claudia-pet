"""Linux built-in role and migration tests. Run with python -B tests/test_linux_store.py."""

import os
import sys
import tempfile
import unittest
import urllib.parse
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "linux"))
from claudia import store


class ResourceUpdateTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        data = root / "data"
        assets = data / "characters"
        override = patch.multiple(
            store, INSTALL=root / "install", DATA=data, CHARACTERS=assets, STATE=data / "state",
            _character_dirs={}, settings={"names": {}},
        )
        override.start()
        self.addCleanup(override.stop)
        self.source = store.INSTALL / "characters" / "claude"
        self.active = assets / "claude"
        self.work = data / "builtin-resource-sync" / "claude"
        self.source.mkdir(parents=True)
        (self.source / "character.json").write_text("{}", encoding="utf-8")
        (self.source / "idle.webp").write_bytes(b"built-in")

    def make_local_role(self, contents):
        self.active.mkdir()
        (self.active / "character.json").write_text("{}", encoding="utf-8")
        (self.active / "idle.webp").write_bytes(contents)

    def assert_route(self, expected):
        self.assertIn("claude", store.manifest()["characters"])
        root, mime = store.resolve_sprite("/characters/claude/idle.webp")
        self.assertEqual(root, expected / "idle.webp")
        self.assertEqual(mime, "image/webp")

    def test_reads_builtin_without_copy_and_retires_exact_mirror(self):
        store.sync_resources()
        self.assert_route(self.source)
        self.assertFalse(self.active.exists())
        self.make_local_role(b"built-in")
        store.sync_resources()
        store.sync_resources()
        self.assertFalse(self.active.exists())
        self.assert_route(self.source)

    def test_keeps_same_size_local_edit_and_falls_back_if_incomplete(self):
        store.sync_resources()
        self.make_local_role(b"modified")
        original = (self.source / "idle.webp").stat()
        os.utime(self.active / "idle.webp", ns=(original.st_atime_ns, original.st_mtime_ns))
        store.sync_resources()
        self.assertEqual((self.active / "idle.webp").read_bytes(), b"modified")
        self.assert_route(self.active)
        (self.active / "idle.webp").unlink()
        self.assert_route(self.source)

    def test_interrupted_old_update_recovers_previous(self):
        store.sync_resources()
        self.make_local_role(b"modified")
        self.work.mkdir(parents=True)
        self.active.rename(self.work / "previous")
        store.sync_resources()
        self.assertFalse((self.work / "previous").exists())
        self.assert_route(self.active)

    def test_failed_move_leaves_mirror_in_place_and_can_retry(self):
        store.sync_resources()
        self.make_local_role(b"built-in")
        with patch.object(store.os, "rename", side_effect=OSError("injected move failure")):
            store.sync_resources()
        self.assert_route(self.active)
        store.sync_resources()
        self.assertFalse(self.active.exists())
        self.assert_route(self.source)

    def test_video_only_idle_counts_as_role(self):
        # 2026-11-27 起 Claude 角色的动画只有 .webm，没有精灵图也没有静态 idle：待机动画是视频也要认作角色
        (self.source / "idle.webp").unlink()
        (self.source / "character.json").write_text('{"animations": {"idle": {"sheets": ["待机"], "loop": true}}}', encoding="utf-8")
        self.assertNotIn("claude", store.manifest()["characters"])
        anim = self.source / store.ANIM_DIR
        anim.mkdir()
        (anim / "待机.webm").write_bytes(b"video")
        info = store.manifest()["characters"]["claude"]
        self.assertEqual(info["sheets"], {})
        self.assertEqual(list(info["videos"]), ["待机"])
        root, mime = store.resolve_sprite("/characters/claude/anim/" + urllib.parse.quote("待机.webm"))
        self.assertEqual((root, mime), (anim / "待机.webm", "video/webm"))


if __name__ == "__main__":
    unittest.main()
