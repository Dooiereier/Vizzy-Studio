#!/usr/bin/env python3
"""Lists the game's Vizzy editor methods that look up objects in a way that skips inactive ones.

Editor Culling switches stacks of blocks off (inactive). Unity's GetComponentsInChildren(),
GetComponentInChildren() and FindObject(s)OfType() skip inactive objects unless told otherwise,
so a stock method that collects the editor's blocks that way would miss the switched-off stacks.
Each such method is listed with a verdict in tools/editor_lookups.txt: patched (the mod switches
the stacks back on before it) or harmless (and why). This finds every method in the Vizzy editor
namespaces that makes such a call, and fails when one isn't in the list, so a Juno update that adds
one is noticed. Run it after every Juno update:

    python3 tools/check_editor_lookups.py [SimpleRockets2.dll ...]

Without arguments it checks the installed game and the SimpleRockets2.dll of this project's
ModTools and of each older copy in ~/Projects/JunoModTools/<version>/ModTools. Needs dnfile
(pip install dnfile).
"""
import glob
import os
import struct
import sys

import dnfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIST = os.path.join(ROOT, "tools", "editor_lookups.txt")
GAME = os.path.expanduser(
    "~/Library/Application Support/Steam/steamapps/common/SimpleRockets2/SimpleRockets2.app/"
    "Contents/Resources/Data/Managed/SimpleRockets2.dll")
LOOKUPS = {"GetComponentsInChildren", "GetComponentInChildren", "FindObjectsOfType", "FindObjectOfType"}
NAMESPACE = "Assets.Scripts.Vizzy"
LDC_I4_1 = 0x17


def default_dlls():
    dlls = [GAME, os.path.join(ROOT, "Assets", "ModTools", "Assemblies", "SimpleRockets2.dll")]
    dlls += sorted(glob.glob(os.path.expanduser("~/Projects/JunoModTools/*/ModTools/Assemblies/SimpleRockets2.dll")))
    return [dll for dll in dlls if os.path.exists(dll)]


def method_bodies(pe):
    data = pe.__data__
    for index, method in enumerate(pe.net.mdtables.MethodDef.rows, 1):
        if not method.Rva:
            continue
        try:
            offset = pe.get_offset_from_rva(method.Rva)
            header = data[offset]
            if header & 3 == 2:
                size, start = header >> 2, offset + 1
            else:
                start = offset + (struct.unpack_from("<H", data, offset)[0] >> 12) * 4
                size = struct.unpack_from("<I", data, offset + 4)[0]
        except Exception:
            continue
        yield index, data[start:start + size]


def lookups(path):
    """Type.Method of each Vizzy editor method that makes a lookup which may skip inactive objects."""
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    owner = {}
    namespace = {}
    for row, typedef in enumerate(md.TypeDef.rows, 1):
        namespace[row] = str(typedef.TypeNamespace or "")
        for method in typedef.MethodList:
            owner[method.row_index] = (row, str(typedef.TypeName), str(method.row.Name))

    # A nested type (closures, iterators) takes its enclosing type's namespace and name.
    enclosing = {}
    if getattr(md, "NestedClass", None):
        for nested in md.NestedClass.rows:
            enclosing[nested.NestedClass.row_index] = nested.EnclosingClass.row_index

    def outer(row):
        while row in enclosing:
            row = enclosing[row]
        return row

    def name_of(token):
        table, index = token >> 24, token & 0xFFFFFF
        try:
            if table == 0x0A:
                return str(md.MemberRef.rows[index - 1].Name)
            if table == 0x06:
                return owner[index][2]
            if table == 0x2B:
                return str(md.MethodSpec.rows[index - 1].Method.row.Name)
        except Exception:
            return None
        return None

    found = set()
    for index, code in method_bodies(pe):
        if index not in owner:
            continue
        row, _, method_name = owner[index]
        top = outer(row)
        if not namespace[top].startswith(NAMESPACE):
            continue
        for i in range(1, len(code) - 4):
            if code[i] not in (0x28, 0x6F):
                continue
            name = name_of(struct.unpack_from("<I", code, i + 1)[0])
            # includeInactive: true is pushed right before the call; anything else may skip inactive objects.
            if name in LOOKUPS and code[i - 1] != LDC_I4_1:
                found.add(str(md.TypeDef.rows[top - 1].TypeName) + "." + method_name)
    return found


def reviewed():
    entries = {}
    with open(LIST, encoding="utf-8") as file:
        for line in file:
            line = line.strip()
            if line and not line.startswith("#"):
                name, _, verdict = line.partition(" ")
                entries[name] = verdict.strip()
    return entries


def main():
    dlls = sys.argv[1:] or default_dlls()
    if not dlls:
        print("No SimpleRockets2.dll found; pass its path.")
        return 2
    known = reviewed()
    failed = False
    for dll in dlls:
        found = lookups(dll)
        new = sorted(found - set(known))
        gone = sorted(set(known) - found)
        label = os.path.relpath(dll, os.path.expanduser("~"))
        if new:
            failed = True
            print("NEW (" + label + "): review each and add it to tools/editor_lookups.txt:")
            for name in new:
                print("  " + name)
        else:
            print("OK (" + label + "): " + str(len(found)) + " methods, all reviewed.")
        for name in gone:
            print("  no longer in the game: " + name)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
