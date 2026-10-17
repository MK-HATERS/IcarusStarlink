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
    for line in log:
        sha, when, subject = line.split("\t", 2)
        tables = {}
        for entry in git(repo, "ls-tree", "-r", sha).decode("utf-8", "replace").splitlines():
            meta, path = entry.split("\t", 1)
            _, kind, blob = meta.split()
            if kind != "blob" or not TABLE.match(path):
                continue
            file = current_file(path)
            if file is None:
                continue
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

    # Two repos can hold the same update: the same table contents on nearby dates add nothing.
    versions, seen, last_tables = [], {}, None
    for date, name, tables in snapshots:
        if tables == last_tables:
            continue
        last_tables = tables
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
