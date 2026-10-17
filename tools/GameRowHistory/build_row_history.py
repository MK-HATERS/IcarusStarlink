"""
Builds ISL's game row history: every data-table row name the game has ever had, with the first
and last game version it appeared in, from public git histories of the extracted game data:

  https://github.com/GODOFMINECRAFT4/IcarusData   (Week 9, Feb 2022 -> Week 251, one commit per update)
  https://github.com/IcarusWiki/Data              (weekly and automatic since March 2026)

Only row names and version labels are kept, no game content. The result ships inside the app
(src/IcarusStarlink.PakIO/Resources/game_row_history.json.gz); each user's own Refresh game data
adds to it after that, so it stays current between releases.

Usage: python build_row_history.py <output.json.gz> <clone> [<clone> ...]
       (full clones, not shallow: every commit's tables are read)
"""
import gzip
import json
import re
import subprocess
import sys
from datetime import datetime, timezone

# Folders the repos keep the game's tables under ("data/Items/D_ItemsStatic.json", "InGameFiles/...").
PREFIXES = ("data/", "InGameFiles/")
TABLE = re.compile(r"^(?:.+/)?D_[^/]+\.json$")


def git(repo, *args, data=None):
    return subprocess.run(["git", "-C", repo, *args], input=data, capture_output=True, check=True).stdout


def current_file(path):
    """'data/Items/D_ItemsStatic.json' -> 'Items-D_ItemsStatic.json' (the name ISL's mods use)."""
    for prefix in PREFIXES:
        if path.startswith(prefix):
            path = path[len(prefix):]
            break
    return path.replace("/", "-") if "/" in path else None


def label(subject, date):
    week = re.search(r"Week\s+(\d+)", subject, re.IGNORECASE)
    return f"Week {week.group(1)}" if week else date


class BlobReader:
    """git cat-file --batch over one process: thousands of table versions, read once each."""

    def __init__(self, repo):
        self.proc = subprocess.Popen(["git", "-C", repo, "cat-file", "--batch"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)

    def read(self, sha):
        self.proc.stdin.write(sha.encode() + b"\n")
        self.proc.stdin.flush()
        header = self.proc.stdout.readline().split()
        size = int(header[2])
        body = self.proc.stdout.read(size)
        self.proc.stdout.read(1)
        return body


def row_names(raw):
    try:
        table = json.loads(raw.decode("utf-8-sig"))
    except (ValueError, UnicodeDecodeError):
        return None
    rows = table.get("Rows") if isinstance(table, dict) else None
    if not isinstance(rows, list):
        return None
    return {r["Name"] for r in rows if isinstance(r, dict) and isinstance(r.get("Name"), str) and r["Name"]}


def versions_of(repo):
    """(date, label, {current_file: set(rows)}) per commit that touched tables, oldest first."""
    log = git(repo, "log", "--reverse", "--format=%H%x09%aI%x09%s").decode("utf-8", "replace").splitlines()
    reader = BlobReader(repo)
    names_by_blob = {}

    # A repo can hold one table at two paths: GODOFMINECRAFT4/IcarusData moved from data/ to the
    # root in late 2025 and left the data/ copies behind, frozen. At each commit, a table is read
    # from whichever of its paths was changed most recently, never from a leftover.
    last_changed = {}
    for index, line in enumerate(log):
        sha, when, subject = line.split("\t", 2)
        changed = git(repo, "diff-tree", "--root", "--no-commit-id", "--name-only", "-r", sha).decode("utf-8", "replace").splitlines()
        for path in changed:
            last_changed[path] = index

        candidates = {}
        for entry in git(repo, "ls-tree", "-r", sha).decode("utf-8", "replace").splitlines():
            meta, path = entry.split("\t", 1)
            _, kind, blob = meta.split()
            if kind != "blob" or not TABLE.match(path):
                continue
            file = current_file(path)
            if file is None:
                continue
            freshness = last_changed.get(path, -1)
            if file not in candidates or freshness > candidates[file][0]:
                candidates[file] = (freshness, blob)

        tables = {}
        for file, (_, blob) in candidates.items():
            if blob not in names_by_blob:
                names_by_blob[blob] = row_names(reader.read(blob))
            if names_by_blob[blob] is not None:
                tables[file] = names_by_blob[blob]
        if tables:
            date = when[:10]
            yield date, label(subject, date), tables


def main():
    out, repos = sys.argv[1], sys.argv[2:]
    snapshots = []
    for repo in repos:
        snapshots.extend(versions_of(repo))
    snapshots.sort(key=lambda s: s[0])

    # Two repos can hold the same update (GODOFMINECRAFT4's "Week 251" is IcarusWiki's 25 Sep):
    # identical tables add no version, but a "Week N" label is what players know, so it's kept.
    versions, seen, by_content = [], {}, {}
    for date, name, tables in snapshots:
        fingerprint = hash(tuple(sorted((file, hash(frozenset(rows))) for file, rows in tables.items())))
        if fingerprint in by_content:
            same = versions[by_content[fingerprint]]
            if name.startswith("Week") and not same["label"].startswith("Week"):
                same["label"] = name
            continue
        by_content[fingerprint] = len(versions)
        index = len(versions)
        versions.append({"label": name, "date": date})
        for file, rows in tables.items():
            per_file = seen.setdefault(file, {})
            for row in rows:
                first_last = per_file.get(row)
                per_file[row] = [first_last[0] if first_last else index, index]

    result = {
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%d"),
        "sources": ["github.com/GODOFMINECRAFT4/IcarusData", "github.com/IcarusWiki/Data"],
        "versions": versions,
        "tables": {file: dict(sorted(rows.items())) for file, rows in sorted(seen.items())},
    }
    with gzip.open(out, "wt", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))
    total = sum(len(r) for r in seen.values())
    latest = len(versions) - 1
    removed = sum(1 for r in seen.values() for v in r.values() if v[1] < latest)
    print(f"{len(versions)} versions ({versions[0]['label']} .. {versions[-1]['label']}), {len(seen)} tables, {total} rows ever, {removed} not in the latest version")


if __name__ == "__main__":
    main()
