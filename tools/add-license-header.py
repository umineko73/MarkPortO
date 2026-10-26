# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 umineko73
#
# This file is part of MarkPortO.
#
# MarkPortO is free software: you can redistribute it and/or modify it under the terms of
# the GNU General Public License, version 3 only, as published by the Free Software Foundation.
#
# MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
# without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License along with MarkPortO.
# If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

"""Adds the GPL-3.0-only header to every source file that does not have it yet (.cs, .xaml, .py, .ps1).

Run:  python tools/add-license-header.py          (from anywhere; it works on the repository this script is in)
The header is written once: a file that already has an SPDX-License-Identifier line is left as it is.
The byte order mark and the line ending style of each file are kept. bin/ and obj/ are skipped.
"""
from pathlib import Path

AUTHOR = "umineko73"
YEAR = "2026"
SPDX = "SPDX-License-Identifier: GPL-3.0-only"

LINES = [
    SPDX,
    f"Copyright (C) {YEAR} {AUTHOR}",
    "",
    "This file is part of MarkPortO.",
    "",
    "MarkPortO is free software: you can redistribute it and/or modify it under the terms of",
    "the GNU General Public License, version 3 only, as published by the Free Software Foundation.",
    "",
    "MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;",
    "without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.",
    "See the GNU General Public License for more details.",
    "",
    "You should have received a copy of the GNU General Public License along with MarkPortO.",
    "If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.",
]

ROOT = Path(__file__).resolve().parent.parent
SKIP = {"bin", "obj", ".git", ".specify", ".claude", ".agents", "node_modules"}


def header(extension: str, newline: str) -> str:
    if extension == ".cs":
        body = [("// " + line).rstrip() for line in LINES]
    elif extension in (".py", ".ps1"):
        body = [("# " + line).rstrip() for line in LINES]
    else:  # .xaml: an XML comment before the root element (it must not contain a double hyphen)
        body = ["<!--"] + ["  " + line if line else "" for line in LINES] + ["-->"]
    return newline.join(body) + newline + newline


def main() -> None:
    changed = 0
    for path in sorted(ROOT.rglob("*")):
        if path.suffix not in {".cs", ".xaml", ".py", ".ps1"} or any(part in SKIP for part in path.parts):
            continue
        raw = path.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
        if any(SPDX in line and line.lstrip().startswith(("// SPDX-", "# SPDX-", "SPDX-")) for line in text[:2000].splitlines()):
            continue  # the line is in a comment at the top: the header is there
        newline = "\r\n" if "\r\n" in text else "\n"
        head = header(path.suffix, newline)
        if path.suffix == ".py" and text.startswith("#!"):
            first, _, rest = text.partition(newline)
            text = first + newline + head + rest
        else:
            text = head + text
        path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        changed += 1
    print(f"header added to {changed} files")


if __name__ == "__main__":
    main()
