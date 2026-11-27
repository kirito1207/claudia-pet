import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "linux"))

from claudia import store, update  # noqa: E402


class UpdateTests(unittest.TestCase):
    def test_newer_compares_numbers_and_ignores_prefix_and_suffix(self):
        self.assertTrue(update.newer("v0.5.2", "0.5.1"))
        self.assertTrue(update.newer("0.10.0", "0.9.9"), "numbers, not text")
        self.assertTrue(update.newer("1.0", "0.9.9"))
        self.assertFalse(update.newer("0.5.1", "0.5.1"))
        self.assertFalse(update.newer("0.5.1-beta", "0.5.1"))
        self.assertFalse(update.newer("0.4.2", "0.5.1"))
        self.assertFalse(update.newer("latest", "0.5.1"), "unreadable is never newer")
        self.assertFalse(update.newer("0.5.2", "dev"))

    def test_parse_takes_tag_and_only_github_pages(self):
        self.assertEqual(update.parse('{"tag_name": "v0.6.0", "html_url": "https://github.com/kirito1207/claudia-pet/releases/tag/v0.6.0"}'),
                         ("0.6.0", "https://github.com/kirito1207/claudia-pet/releases/tag/v0.6.0"))
        self.assertEqual(update.parse('{"tag_name": "0.6.0", "html_url": "https://evil.example/x"}'), ("0.6.0", update.RELEASES_PAGE))
        self.assertIsNone(update.parse('{"message": "Not Found"}'))
        self.assertIsNone(update.parse("not json"))
        self.assertIsNone(update.parse('{"tag_name": "nightly"}'))

    def test_for_page_reads_the_saved_result(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(store, "DATA", Path(tmp)), patch.object(store, "version", lambda: "0.5.1"):
            self.assertEqual(update.for_page(False)["latest"], None)
            update.save({"checkedAt": 100, "latest": "0.5.2", "url": "https://github.com/kirito1207/claudia-pet/releases/tag/v0.5.2"})
            page = update.for_page(True)
            self.assertEqual((page["latest"], page["newer"], page["checkedAt"], page["checking"]), ("0.5.2", True, 100, True))
            update.save({"checkedAt": 200, "latest": "0.5.1", "error": None})
            self.assertFalse(update.for_page(False)["newer"])


if __name__ == "__main__":
    unittest.main()
