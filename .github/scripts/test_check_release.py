import importlib.util
from pathlib import Path
import unittest
from unittest.mock import mock_open, patch

spec = importlib.util.spec_from_file_location("check_release", Path(__file__).with_name("check-release.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class ReleaseVersionTests(unittest.TestCase):
    def validate(self, candidate="0.5.3.10", previous="0.5.3.9", advertised="0.5.3.9", releases=None, tags=None):
        check.validate(candidate, previous, advertised, releases or [], tags or [])

    def test_numeric_order(self):
        self.validate()

    def test_equal_or_lower_versions(self):
        for candidate in ("0.5.3.9", "0.5.3.8", "0.5.2.10"):
            with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                self.validate(candidate=candidate)

    def test_advertised_version(self):
        with self.assertRaises(ValueError):
            self.validate(advertised="0.5.3.10")

    def test_published_version(self):
        for tag in ("v0.5.3.10", "v0.6.0.0", "v0.6.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                self.validate(releases=[{"tag_name": tag, "draft": False}])

    def test_existing_tag(self):
        for tag in ("v0.5.3.10", "0.5.3.10"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                self.validate(tags=[tag])

    def test_invalid_version(self):
        for text in ("0.5.3", "0.5.3.1-beta", "0.5.3.-1", "0.5.3.01", "0.5.3.65535"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                check.version(text)

    def test_legacy_release(self):
        self.validate(releases=[{"tag_name": "v0.1.2", "draft": False}])

    def test_unknown_release_fails(self):
        with self.assertRaises(ValueError):
            self.validate(releases=[{"tag_name": "unknown", "draft": False}])

    def test_project_version(self):
        self.assertEqual(check.project_version("<Project><PropertyGroup><Version>0.5.3.2</Version></PropertyGroup></Project>"), "0.5.3.2")
        with self.assertRaises(ValueError):
            check.project_version("<Project />")

    def test_unchanged_version_skips_network(self):
        xml = "<Project><PropertyGroup><Version>0.5.3.2</Version></PropertyGroup></Project>"
        with patch.dict(check.os.environ, {"BEFORE_SHA": "a" * 40, "GITHUB_OUTPUT": "unused"}), \
                patch.object(check, "run", return_value=xml) as command, \
                patch("builtins.open", mock_open()) as output:
            check.main()
        self.assertEqual(command.call_count, 2)
        output().write.assert_called_once_with("changed=false\n")


if __name__ == "__main__":
    unittest.main()
