"""Check the current dotnet test run, including accidental zero-test success."""

import argparse
import pathlib
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("result", type=pathlib.Path)
    args = parser.parse_args()
    results = sorted(args.result.glob("*.trx")) if args.result.is_dir() else [args.result]
    if not results:
        raise SystemExit("Test runner did not produce any TRX result files")
    for result in results:
        root = ET.parse(result).getroot()
        counters = root.find(".//{*}Counters")
        if counters is None:
            raise SystemExit("Test runner did not report result counters")
        counts = {key: int(value) for key, value in counters.attrib.items()}
        if counts.get("total", 0) == 0 or counts.get("executed", 0) == 0:
            raise SystemExit("No tests executed")
        failed = sum(counts.get(key, 0) for key in ("failed", "error", "timeout", "aborted"))
        if failed or counts.get("passed", 0) == 0:
            raise SystemExit(f"Test run did not pass: {counts}")
        print(f"Verified {result.name}: {counts}")


if __name__ == "__main__":
    main()
