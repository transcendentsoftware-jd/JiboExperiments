#!/usr/bin/env python3
import argparse
import json
from pathlib import Path


def redact(value):
    if value is None:
        return None

    if isinstance(value, str):
        lowered = value.lower()
        if "token" in lowered or "bearer" in lowered or "session" in lowered:
            return "[redacted]"
        return value

    if isinstance(value, list):
        return [redact(item) for item in value]

    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            lowered = key.lower()
            if "token" in lowered or "authorization" in lowered:
                result[key] = "[redacted]"
            else:
                result[key] = redact(item)
        return result

    return value


def main():
    parser = argparse.ArgumentParser(description="Import and sanitize an exported websocket capture fixture.")
    parser.add_argument("source_path")
    parser.add_argument("fixture_name")
    parser.add_argument(
        "--destination-directory",
        default=str(Path(__file__).resolve().parents[2] / "src" / "Jibo.Cloud" / "node" / "fixtures" / "websocket"),
    )
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()

    source_path = Path(args.source_path).resolve()
    destination_directory = Path(args.destination_directory).resolve()
    destination_directory.mkdir(parents=True, exist_ok=True)
    destination_path = destination_directory / f"{args.fixture_name}.flow.json"

    if destination_path.exists() and not args.overwrite:
        raise SystemExit(f"Destination fixture already exists: {destination_path}. Use --overwrite to replace it.")

    with source_path.open("r", encoding="utf-8") as handle:
        fixture = json.load(handle)

    sanitized = redact(fixture)
    sanitized["name"] = args.fixture_name
    if "session" in sanitized and isinstance(sanitized["session"], dict):
        sanitized["session"]["token"] = "[redacted]"

    with destination_path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(sanitized, handle, indent=2)
        handle.write("\n")

    print("Imported sanitized websocket fixture:")
    print(f" - source: {source_path}")
    print(f" - destination: {destination_path}")


if __name__ == "__main__":
    main()
