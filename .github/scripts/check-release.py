import json
import os
import re
import subprocess
import xml.etree.ElementTree as ET


def version(text):
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){3}", text):
        raise ValueError(f"Use four version numbers: {text!r}")
    parts = tuple(map(int, text.split(".")))
    if any(part > 65534 for part in parts):
        raise ValueError("Version numbers must not exceed 65534.")
    return parts


def project_version(xml):
    values = ET.fromstring(xml).findall("./PropertyGroup/Version")
    if len(values) != 1 or not values[0].text:
        raise ValueError("The project must define one Version.")
    result = values[0].text.strip()
    version(result)
    return result


def validate(candidate, previous, advertised, releases, tags):
    current = version(candidate)
    if current <= version(previous):
        raise ValueError("The new version must exceed the previous project version.")
    if current <= version(advertised):
        raise ValueError("The new version must exceed the version in repo.json.")
    for release in releases:
        if release["draft"]:
            continue
        published = release["tag_name"].removeprefix("v")
        # Older releases can use fewer than four version numbers.
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", published):
            raise ValueError(f"Cannot compare published tag {published!r}.")
        parts = tuple(map(int, published.split(".")))
        parts += (0,) * (4 - len(parts))
        if current <= parts:
            raise ValueError(f"The new version must exceed published release {published}.")
    if "v" + candidate in tags or candidate in tags:
        raise ValueError("A tag already exists for this version.")


def run(*args):
    return subprocess.check_output(args, text=True, encoding="utf-8")


def api_pages(path):
    pages = json.loads(run("gh", "api", "--paginate", "--slurp", path))
    return [item for page in pages for item in page]


def main():
    before = os.environ["BEFORE_SHA"]
    if not re.fullmatch(r"[0-9a-f]{40}", before) or before == "0" * 40:
        raise ValueError("A previous main commit is required.")
    candidate = project_version(run("git", "show", "HEAD:GubalLibrary.csproj"))
    previous = project_version(run("git", "show", before + ":GubalLibrary.csproj"))
    if candidate == previous:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("changed=false\n")
        print("The project version did not change. No release.")
        return
    repository = os.environ["GITHUB_REPOSITORY"]
    index = json.loads(run("gh", "api", f"repos/{repository}/contents/repo.json?ref=main",
                          "-H", "Accept: application/vnd.github.raw+json"))
    if not isinstance(index, list) or len(index) != 1 or index[0]["InternalName"] != "GubalLibrary":
        raise ValueError("repo.json must contain one GubalLibrary entry.")
    releases = api_pages(f"repos/{repository}/releases?per_page=100")
    tags = [tag["name"] for tag in api_pages(f"repos/{repository}/tags?per_page=100")]
    validate(candidate, previous, index[0]["AssemblyVersion"], releases, tags)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"changed=true\nversion={candidate}\ntag=v{candidate}\n")
    print(f"Release v{candidate} passed the version checks.")


if __name__ == "__main__":
    main()
