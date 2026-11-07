#!/usr/bin/env python3
"""Runs the offline tests in tests/ with Unity's Mono. The sources listed below are compiled in: they need
no game or engine.

    python3 tools/run_tests.py
"""
import glob
import os
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import compile_check  # noqa: E402

ROOT = compile_check.ROOT
MONO = f"{compile_check.UNITY}/MonoBleedingEdge/bin/mono"
# DeflateStream (the PNG encoder) needs Mono's native helper, which the game ships too.
POSIX_HELPER = f"{compile_check.UNITY}/Frameworks/MonoBleedingEdge/MonoEmbedRuntime/osx"


def main():
    csproj = compile_check.find_csproj()
    references, defines = compile_check.read_references(csproj, os.path.join(ROOT, "Assets", "ModTools"))
    sources = [os.path.join(ROOT, "Assets", "Scripts", name) for name in ("ThreadRemapper.cs", "LatestEdits.cs", "FunkCompletion.cs", "ModPageVersion.cs", "BlockSearch.cs", "BlockPlacement.cs", "Mfd/VizzyExpressionSolver.cs", "Mfd/MfdFormulaWriter.cs", "Text.cs", "PngStreamWriter.cs")]
    sources += sorted(glob.glob(os.path.join(ROOT, "tests", "*.cs")))
    with tempfile.TemporaryDirectory() as temp:
        program = os.path.join(temp, "Tests.exe")
        result = compile_check.compile_once(references, defines, sources, target="exe", out=program)
        if result.returncode != 0:
            for line in result.stdout.splitlines():
                if " error " in line:
                    print(line.replace(ROOT + "/", ""))
            return 1
        run = subprocess.run([MONO, program], capture_output=True, text=True, env=dict(os.environ, VIZZY_STUDIO_ROOT=ROOT, DYLD_LIBRARY_PATH=POSIX_HELPER))
        print(run.stdout, end="")
        if run.returncode != 0:
            print(run.stderr[-3000:], end="")
        return run.returncode


if __name__ == "__main__":
    sys.exit(main())
