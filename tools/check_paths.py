#!/usr/bin/env python3
"""Fail when a file path is not covered by .github/allowed-paths.txt.

The allowlist keeps the repository to what the product needs (source, tests, fixtures,
installer, CI, user docs). Unlike a .gitignore it is checked against what is committed,
so a force-added or unexpected file fails CI instead of slipping through.

Usage:
    python tools/check_paths.py                      # check every tracked file (git ls-files)
    python tools/check_paths.py --list -             # check paths read from stdin, one per line
    python tools/check_paths.py --rules <file>       # use another allowlist
"""
from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULES = ROOT / ".github" / "allowed-paths.txt"


def glob_to_regex(pattern: str) -> re.Pattern[str]:
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        elif pattern[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return re.compile("".join(out) + r"\Z")


def load_rules(text: str) -> list[tuple[bool, re.Pattern[str]]]:
    rules = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        allow = not line.startswith("!")
        rules.append((allow, glob_to_regex(line.lstrip("!"))))
    return rules


def allowed(path: str, rules: list[tuple[bool, re.Pattern[str]]]) -> bool:
    verdict = False
    for allow, rx in rules:
        if rx.match(path):
            verdict = allow
    return verdict


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--rules", type=pathlib.Path, default=RULES)
    ap.add_argument("--list", help="file with paths to check, '-' for stdin (default: git ls-files)")
    a = ap.parse_args(argv)

    rules = load_rules(a.rules.read_text(encoding="utf-8"))
    if a.list == "-":
        paths = sys.stdin.read().splitlines()
    elif a.list:
        paths = pathlib.Path(a.list).read_text(encoding="utf-8").splitlines()
    else:
        paths = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True,
                               encoding="utf-8", check=True).stdout.splitlines()
    paths = [p.strip().replace("\\", "/") for p in paths if p.strip()]

    bad = sorted(p for p in set(paths) if not allowed(p, rules))
    if bad:
        print(f"{len(bad)} path(s) not covered by {a.rules.name}:")
        for p in bad:
            print(f"  {p}")
        print("If a file belongs to the product, add a pattern to .github/allowed-paths.txt.")
        return 1
    print(f"check_paths: {len(paths)} path(s) OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
