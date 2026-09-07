import base64
import copy
import json
import os
from pathlib import Path
import subprocess
import textwrap
import unittest


class IndexMergeTests(unittest.TestCase):
    def test_merge_guards(self):
        workflow = Path(__file__).parents[1] / "workflows" / "release.yml"
        job = workflow.read_text(encoding="utf-8").split("  merge-repo-json:\n", 1)[1]
        script = textwrap.dedent(job.split("        run: |\n", 1)[1])
        file = {"filename": "repo.json", "status": "modified", "sha": "c" * 40}
        valid = {
            "commit": {"parents": [{"sha": "a" * 40}], "files": [file]},
            "main": "a" * 40,
            "pr": {"number": 1, "headRefOid": "b" * 40, "baseRefOid": "a" * 40,
                   "baseRefName": "main", "isCrossRepository": False},
            "files": [file],
        }
        cases = {"valid": valid}
        for name, change in {
            "extra_file": lambda f: f["commit"]["files"].append({"filename": "Plugin.cs"}),
            "changed_content": lambda f: f["commit"]["files"][0].update(sha="d" * 40),
            "deleted_index": lambda f: f["commit"]["files"][0].update(status="removed"),
            "extra_parent": lambda f: f["commit"]["parents"].append({"sha": "d" * 40}),
            "wrong_parent": lambda f: f["commit"]["parents"][0].update(sha="d" * 40),
            "main_changed": lambda f: f.update(main="d" * 40),
            "head_changed": lambda f: f["pr"].update(headRefOid="d" * 40),
            "base_changed": lambda f: f["pr"].update(baseRefOid="d" * 40),
            "fork": lambda f: f["pr"].update(isCrossRepository=True),
            "wrong_branch": lambda f: f["pr"].update(baseRefName="other"),
            "extra_pr_file": lambda f: f["files"].append({"filename": "Plugin.cs"}),
        }.items():
            fixture = copy.deepcopy(valid)
            change(fixture)
            cases[name] = fixture
        mock = r'''
$ErrorActionPreference = 'Stop'
$fixture = $env:INDEX_TEST_FIXTURE | ConvertFrom-Json
function gh {
    if ($args[0] -eq 'api') {
        if ($args[1] -match '/commits/') { return ($fixture.commit | ConvertTo-Json -Depth 10 -Compress) }
        if ($args[1] -match '/git/ref/') { return $fixture.main }
        if ($args[1] -match '/pulls/1/files$') { return ($fixture.files | ConvertTo-Json -Depth 10 -Compress -AsArray) }
    }
    if ($args[0] -eq 'pr') {
        if ($args[1] -eq 'create') { return 'https://github.com/example/repo/pull/1' }
        if ($args[1] -eq 'view') { return ($fixture.pr | ConvertTo-Json -Compress) }
        if ($args[1] -eq 'merge') {
            if ($args[-2] -ne '--match-head-commit' -or $args[-1] -ne $env:INDEX_HEAD) { throw 'Missing commit guard.' }
            Write-Output 'MERGE_ACCEPTED'
            return
        }
    }
    throw 'Unexpected gh call in test.'
}
'''
        encoded = base64.b64encode((mock + script).encode("utf-16le")).decode()
        for name, fixture in cases.items():
            with self.subTest(case=name):
                env = dict(os.environ, GH_TOKEN="test-only", GH_REPO="example/repo",
                           GITHUB_REPOSITORY="example/repo", RELEASE_VERSION="0.5.3.3",
                           INDEX_BASE="a" * 40, INDEX_HEAD="b" * 40, INDEX_BLOB="c" * 40,
                           INDEX_TEST_FIXTURE=json.dumps(fixture))
                result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                                        env=env, capture_output=True, text=True)
                if name == "valid":
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("MERGE_ACCEPTED", result.stdout)
                else:
                    self.assertNotEqual(result.returncode, 0)
                    self.assertNotIn("MERGE_ACCEPTED", result.stdout)


if __name__ == "__main__":
    unittest.main()
