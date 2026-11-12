"""Read an installed Electron archive and report investigation landmarks only.

Does not extract application code/assets or change the installed application.
"""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import re
import struct


def inspect(archive):
    with archive.open("rb") as stream:
        prefix = stream.read(16)
        if len(prefix) != 16:
            raise ValueError("Incomplete ASAR header")
        _, header_size, _, json_size = struct.unpack("<4I", prefix)
        if not 0 < json_size <= 32 * 1024 * 1024 or json_size + 8 > header_size:
            raise ValueError("Invalid ASAR header sizes")
        tree = json.loads(stream.read(json_size))
        entries = {}

        def walk(folder, parent=""):
            for name, entry in folder.get("files", {}).items():
                path = parent + name
                if "files" in entry:
                    walk(entry, path + "/")
                else:
                    entries[path] = entry

        walk(tree)
        data_start = 8 + header_size

        def read(name):
            entry = entries[name]
            if entry.get("unpacked") or "offset" not in entry:
                raise ValueError("Entry is outside the archive: " + name)
            start, size = data_start + int(entry["offset"]), entry["size"]
            if start < data_start or size < 0 or start + size > archive.stat().st_size:
                raise ValueError("Entry outside archive bounds: " + name)
            stream.seek(start)
            data = stream.read(size)
            if len(data) != size:
                raise ValueError("Truncated entry: " + name)
            return data

        package = json.loads(read("package.json"))
        entrypoints = {}
        for name in ["editor", "editor-frame", "welcome", "new", "dialog-map-templates", "dialog-theme-editor"]:
            path = "renderer/" + name + ".html"
            if path in entries:
                entrypoints[path] = re.findall(r'<script[^>]+src=["\']([^"\']+)', read(path).decode("utf-8"))
        landmarks = {}
        preset_tables = {}
        for name, terms in {
            "renderer/1857.js": ["function sC(", "countOfFirstHalfChildrenTopicsForUnbalancedMap",
                                  "boundaryAllocationStackForChildTopicById", "summaryAllocationStackForChildTopicById",
                                  "startViewportGesture", "changeViewportGesture", "compositionstart", "compositionend",
                                  "wB=", "YB=", "eB=", "function iB(", "gk=", "yk=", "function _k(",
                                  "function fa(", "function IC(", "function fG(", "function zD(", "function WD(",
                                  "function Rg(", "function zm(", "function _$(", "function y$(", "undoableContent",
                                  "94075:", "15626:", "generateSmartColorThemes()"],
            "renderer/editor.js": ["scheduleProcessing()", "requestAnimationFrame", "timeSliceMs"],
        }.items():
            if name not in entries:
                continue
            data = read(name)
            source = data.decode("utf-8")
            landmarks[name] = {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                               "symbols": {term: {"occurrences": source.count(term), "firstCharacterOffset": source.find(term)} for term in terms}}
            if name == "renderer/1857.js":
                for module_id in [94075, 15626]:
                    start = source.find(str(module_id) + ":")
                    if start < 0:
                        continue
                    following = re.search(r"},\d+:\(", source[start + 20:])
                    module = source[start:start + 20 + following.start()] if following else source[start:]
                    tables = []
                    for literal in re.finditer(r"JSON\.parse\(('(?:\\.|[^'\\])*')\)", module):
                        # Parse only a string literal, never execute application JavaScript.
                        table = json.loads(ast.literal_eval(literal.group(1)))
                        if not isinstance(table, list) or not table or not isinstance(table[0], dict) or "theme" not in table[0]:
                            continue
                        roles = {}
                        for preset in table:
                            for role, fields in preset.get("theme", {}).items():
                                if isinstance(fields, dict):
                                    roles.setdefault(role, set()).update(fields)
                        tables.append({"entries": len(table), "fieldsPerRole": {role: sorted(fields) for role, fields in roles.items()}})
                    preset_tables[str(module_id)] = tables
        return {"archive": str(archive), "archiveBytes": archive.stat().st_size, "fileCount": len(entries),
                "package": {key: package.get(key) for key in ["name", "version", "main"]},
                "applicationSourceMaps": [name for name in entries if name.endswith(".map") and not name.startswith("node_modules/")],
                "entrypoints": entrypoints, "landmarks": landmarks, "presetSchemas": preset_tables,
                "note": "Static inspection landmarks, not runtime performance measurements. Contains no extracted application code or assets."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = json.dumps(inspect(args.archive.resolve()), ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result + "\n", encoding="utf-8")
        print("Inspection report: " + str(args.output))
    else:
        print(result)
