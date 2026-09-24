"""Runs the independent algorithm math without a LEAN runtime for deterministic fixture parity."""
from pathlib import Path
from types import ModuleType
import argparse
import json
import sys

stubs = ModuleType("AlgorithmImports")
for name in ("QCAlgorithm", "PythonData", "ImmediateFillModel", "FeeModel"):
    setattr(stubs, name, type(name, (), {}))
sys.modules["AlgorithmImports"] = stubs

from main import TccLeanValidationAlgorithm


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    parser.add_argument("--candles", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    algorithm = object.__new__(TccLeanValidationAlgorithm)
    algorithm._request = json.loads(Path(args.request).read_text(encoding="utf-8"), parse_float=__import__("decimal").Decimal)
    algorithm._bars = algorithm._read_bars(Path(args.candles))
    _, run = algorithm._simulate()
    Path(args.output).write_text(algorithm._json(run), encoding="utf-8")


if __name__ == "__main__":
    main()
