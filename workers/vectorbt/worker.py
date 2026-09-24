"""Bounded vectorbt screening worker. Results are exploratory until native verification."""
from __future__ import annotations

import argparse
import json
import math
import sys
from decimal import Decimal
from pathlib import Path

import numpy as np
import pandas as pd
import vectorbt as vbt
from parity import SUPPORTED_STRATEGIES, parity_evidence, signals as native_signals

SCHEMA_VERSION = 1
WORKER_ID = "vectorbt"
EXPECTED_VERSION = "1.1.0"
MAX_CANDIDATES = 10_000
MAX_CANDLES = 1_000_000


def _series_value(value: object) -> float:
    if hasattr(value, "iloc"):
        value = value.iloc[0]
    result = float(value)
    return result if math.isfinite(result) else 0.0


def _signals(frame: pd.DataFrame, strategy_id: str, p: dict[str, float], zone_id: str) -> tuple[pd.Series, pd.Series, pd.Series]:
    candles = [{"openTimeUtc": timestamp.isoformat().replace("+00:00", "Z"),
                "open": row.open, "high": row.high, "low": row.low,
                "close": row.close, "volume": int(row.volume)}
               for timestamp, row in frame.iterrows()]
    exact, indicator = native_signals(candles, p, strategy_id, zone_id)
    long_times = pd.to_datetime([x["timestampUtc"] for x in exact if x["direction"] == "long"], utc=True)
    short_times = pd.to_datetime([x["timestampUtc"] for x in exact if x["direction"] == "short"], utc=True)
    long_signal = pd.Series(frame.index.isin(long_times), index=frame.index)
    short_signal = pd.Series(frame.index.isin(short_times), index=frame.index)
    atr = pd.Series([float(x["atr"]) if x["atr"] is not None else np.nan for x in indicator], index=frame.index)
    return long_signal, short_signal, atr


def _evaluate(frame: pd.DataFrame, request: dict, candidate: dict) -> dict:
    parameters = candidate["parameters"]
    long_signal, short_signal, atr = _signals(frame, request["strategyId"], parameters,
                                               request["exchangeTimeZoneId"])
    numeric = frame.astype({name: float for name in ("open", "high", "low", "close", "volume")})
    entries, short_entries = long_signal.shift(1, fill_value=False), short_signal.shift(1, fill_value=False)
    local = numeric.index.tz_convert("Asia/Kolkata")
    exit_time = request["sessionExitTime"]
    if isinstance(exit_time, str):
        parts = [int(value) for value in exit_time.split(":")[:2]]
        exit_minute = parts[0] * 60 + parts[1]
    else:
        raise ValueError("sessionExitTime must be a string")
    session_exit = (local.hour * 60 + local.minute >= exit_minute)
    stop_distance = (atr * float(parameters["atrStopMultiple"]) / numeric["open"]).clip(lower=0.000001)
    portfolio = vbt.Portfolio.from_signals(
        close=numeric["close"], open=numeric["open"], high=numeric["high"], low=numeric["low"],
        entries=entries, exits=session_exit, short_entries=short_entries, short_exits=session_exit,
        price=numeric["open"], size=1.0, init_cash=float(request["initialCapital"]),
        slippage=float(request["slippageBasisPointsPerSide"]) / 10000.0,
        sl_stop=stop_distance, tp_stop=stop_distance * float(parameters["rewardRiskMultiple"]),
        upon_opposite_entry="ignore", freq=f"{int(request['timeframeMinutes'])}min")
    total_return = _series_value(portfolio.total_return()) * 100
    drawdown = abs(_series_value(portfolio.max_drawdown())) * 100
    trade_count = int(_series_value(portfolio.trades.count()))
    win_rate = (_series_value(portfolio.trades.win_rate()) * 100) if trade_count else 0.0
    sharpe = _series_value(portfolio.sharpe_ratio())
    score = total_return - 0.5 * drawdown + 0.1 * win_rate + 0.001 * min(trade_count, 100)
    return {"candidateKey": candidate["candidateKey"], "metrics": {
        "score": round(score, 8), "totalReturnPercent": round(total_return, 8),
        "maximumDrawdownPercent": round(drawdown, 8), "tradeCount": trade_count,
        "winRatePercent": round(win_rate, 8), "sharpeRatio": round(sharpe, 8)}}


def run(request: dict) -> dict:
    if request.get("schemaVersion") != SCHEMA_VERSION or len(request.get("candidates", [])) not in range(1, MAX_CANDIDATES + 1):
        raise ValueError("invalid request schema or candidate count")
    if len(request.get("candles", [])) not in range(2, MAX_CANDLES + 1):
        raise ValueError("invalid candle count")
    if request.get("strategyId") not in SUPPORTED_STRATEGIES:
        raise ValueError("strategy has no passing native parity port")
    evidence = parity_evidence(request["strategyId"])
    frame = pd.DataFrame(request["candles"])
    frame["openTimeUtc"] = pd.to_datetime(frame["openTimeUtc"], utc=True)
    frame = frame.set_index("openTimeUtc").sort_index()
    if frame.index.has_duplicates or not frame.index.is_monotonic_increasing:
        raise ValueError("candles must be unique and chronological")
    results = [_evaluate(frame, request, candidate) for candidate in request["candidates"]]
    results.sort(key=lambda value: (-value["metrics"]["score"], value["candidateKey"]))
    return {"schemaVersion": 2, "workerId": WORKER_ID, "workerVersion": str(vbt.__version__),
            "role": "researchExploration", "requestId": request["requestId"],
            "requestSha256": request["requestSha256"],
            "candidates": results[: int(request["topCandidates"])],
            "parityEvidence": evidence, "evidenceSha256": ""}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if str(vbt.__version__) != EXPECTED_VERSION:
        raise RuntimeError(f"vectorbt {EXPECTED_VERSION} is required; found {vbt.__version__}")
    request_path, output_path = Path(args.request), Path(args.output)
    if request_path.stat().st_size > 128 * 1024 * 1024 or output_path.exists():
        raise ValueError("request is too large or output already exists")
    request = json.loads(request_path.read_text(encoding="utf-8"), parse_float=Decimal)
    result = run(request)
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False), encoding="utf-8")
    temporary.replace(output_path)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exception:
        print(f"{type(exception).__name__}: {exception}", file=sys.stderr)
        sys.exit(2)
