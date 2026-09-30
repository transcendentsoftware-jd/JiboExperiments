#!/usr/bin/env python3
"""Read-only local Linux CPU check; does not select, pull or run an image."""
import json
import platform
from pathlib import Path

REQUIRED = frozenset({"sse4_2", "avx", "avx2", "fma", "f16c", "bmi2"})


def assess(system, machine, cpuinfo):
    processors = []
    for block in cpuinfo.strip().split("\n\n"):
        fields = dict(line.split(":", 1) for line in block.splitlines() if ":" in line)
        fields = {key.strip(): value.strip() for key, value in fields.items()}
        if "processor" in fields:
            processors.append(set(fields.get("flags", "").split()))
    common = set.intersection(*processors) if processors else set()
    missing = sorted(REQUIRED - common)
    supported = system == "Linux" and machine == "x86_64" and bool(processors) and not missing
    return {
        "purpose": "read_only_local_cpu_capability_check",
        "avx2_cpu_prerequisites_passed": supported,
        "logical_processors_checked": len(processors),
        "missing_flags": missing,
        "scope": "Local CPU flags only. Not proof of remote Docker daemon capabilities, image compatibility, or speech accuracy. No image selected or modified.",
    }


if __name__ == "__main__":
    try:
        cpuinfo = Path("/proc/cpuinfo").read_text(encoding="utf-8")
    except OSError:
        cpuinfo = ""
    result = assess(platform.system(), platform.machine(), cpuinfo)
    print(json.dumps(result))
    raise SystemExit(0 if result["avx2_cpu_prerequisites_passed"] else 1)
