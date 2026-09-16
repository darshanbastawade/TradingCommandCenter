"""Bounded vectorbt screening worker. Results are exploratory until native verification."""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
import pandas as pd
import vectorbt as vbt

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


def _indicators(frame: pd.DataFrame, parameters: dict[str, float]) -> dict[str, pd.Series]:
    fast = int(parameters["fastEmaPeriod"])
    slow = int(parameters["slowEmaPeriod"])
    atr_period = int(parameters["atrPeriod"])
    adx_period = int(parameters["adxPeriod"])
    volume_period = int(parameters["volumeAveragePeriod"])
    close, high, low = frame["close"], frame["high"], frame["low"]
    previous = close.shift(1)
    true_range = pd.concat([(high - low), (high - previous).abs(), (low - previous).abs()], axis=1).max(axis=1)
    atr = true_range.ewm(alpha=1 / atr_period, adjust=False, min_periods=atr_period).mean()
    up, down = high.diff(), -low.diff()
    plus_dm = up.where((up > down) & (up > 0), 0.0)
    minus_dm = down.where((down > up) & (down > 0), 0.0)
    plus_di = 100 * plus_dm.ewm(alpha=1 / adx_period, adjust=False, min_periods=adx_period).mean() / atr
    minus_di = 100 * minus_dm.ewm(alpha=1 / adx_period, adjust=False, min_periods=adx_period).mean() / atr
    dx = 100 * (plus_di - minus_di).abs() / (plus_di + minus_di).replace(0, np.nan)
    local_day = frame.index.tz_convert("Asia/Kolkata").date
    typical = (high + low + close) / 3
    cumulative_pv = (typical * frame["volume"]).groupby(local_day).cumsum()
    cumulative_volume = frame["volume"].groupby(local_day).cumsum().replace(0, np.nan)
    return {
        "fast": close.ewm(span=fast, adjust=False, min_periods=fast).mean(),
        "slow": close.ewm(span=slow, adjust=False, min_periods=slow).mean(),
        "atr": atr,
        "adx": dx.ewm(alpha=1 / adx_period, adjust=False, min_periods=adx_period).mean(),
        "plus_di": plus_di,
        "minus_di": minus_di,
        "volume_average": frame["volume"].rolling(volume_period, min_periods=volume_period).mean(),
        "vwap": cumulative_pv / cumulative_volume,
    }


def _signals(frame: pd.DataFrame, strategy_id: str, p: dict[str, float]) -> tuple[pd.Series, pd.Series, pd.Series]:
    indicator = _indicators(frame, p)
    close = frame["close"]
    minutes = frame.index.tz_convert("Asia/Kolkata").hour * 60 + frame.index.tz_convert("Asia/Kolkata").minute
    window = (minutes >= int(p["entryWindowStartMinuteOfDay"])) & (minutes <= int(p["entryWindowEndMinuteOfDay"]))
    volume_ok = frame["volume"] >= indicator["volume_average"] * float(p.get("volumeMultiplier", 0))
    adx_ok = indicator["adx"] >= float(p.get("minimumAdx", 0))
    trend_long, trend_short = indicator["fast"] > indicator["slow"], indicator["fast"] < indicator["slow"]

    if strategy_id == "vwap-ema-trend-breakout-v1":
        lookback = int(p["breakoutLookbackBars"])
        prior_high = frame["high"].rolling(lookback).max().shift(1)
        prior_low = frame["low"].rolling(lookback).min().shift(1)
        long_signal = trend_long & (close > indicator["vwap"]) & (close > prior_high) & adx_ok & volume_ok
        short_signal = trend_short & (close < indicator["vwap"]) & (close < prior_low) & adx_ok & volume_ok
    elif strategy_id == "opening-range-breakout-v1":
        bars = int(p["openingRangeBars"])
        days = frame.index.tz_convert("Asia/Kolkata").date
        opening_high = frame["high"].groupby(days).transform(lambda value: value.iloc[:bars].max())
        opening_low = frame["low"].groupby(days).transform(lambda value: value.iloc[:bars].min())
        long_signal = trend_long & (close > opening_high) & volume_ok
        short_signal = trend_short & (close < opening_low) & volume_ok
    elif strategy_id == "ema-pullback-continuation-v1":
        long_signal = trend_long & (close > indicator["fast"]) & (close.shift(1) <= indicator["fast"].shift(1)) & adx_ok & volume_ok
        short_signal = trend_short & (close < indicator["fast"]) & (close.shift(1) >= indicator["fast"].shift(1)) & adx_ok & volume_ok
    elif strategy_id == "vwap-reclaim-rejection-v1":
        long_signal = trend_long & (close > indicator["vwap"]) & (close.shift(1) <= indicator["vwap"].shift(1)) & adx_ok & volume_ok
        short_signal = trend_short & (close < indicator["vwap"]) & (close.shift(1) >= indicator["vwap"].shift(1)) & adx_ok & volume_ok
    elif strategy_id == "adx-trend-continuation-v1":
        long_signal = trend_long & adx_ok & (indicator["plus_di"] > indicator["minus_di"])
        short_signal = trend_short & adx_ok & (indicator["minus_di"] > indicator["plus_di"])
    else:
        raise ValueError(f"unsupported strategy: {strategy_id}")
    return (long_signal & window).fillna(False), (short_signal & window).fillna(False), indicator["atr"]


def _evaluate(frame: pd.DataFrame, request: dict, candidate: dict) -> dict:
    parameters = {key: float(value) for key, value in candidate["parameters"].items()}
    long_signal, short_signal, atr = _signals(frame, request["strategyId"], parameters)
    entries, short_entries = long_signal.shift(1, fill_value=False), short_signal.shift(1, fill_value=False)
    local = frame.index.tz_convert("Asia/Kolkata")
    exit_time = request["sessionExitTime"]
    if isinstance(exit_time, str):
        parts = [int(value) for value in exit_time.split(":")[:2]]
        exit_minute = parts[0] * 60 + parts[1]
    else:
        raise ValueError("sessionExitTime must be a string")
    session_exit = (local.hour * 60 + local.minute >= exit_minute)
    stop_distance = (atr * float(parameters["atrStopMultiple"]) / frame["open"]).clip(lower=0.000001)
    portfolio = vbt.Portfolio.from_signals(
        close=frame["close"], open=frame["open"], high=frame["high"], low=frame["low"],
        entries=entries, exits=session_exit, short_entries=short_entries, short_exits=session_exit,
        price=frame["open"], size=1.0, init_cash=float(request["initialCapital"]),
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
    frame = pd.DataFrame(request["candles"])
    frame["openTimeUtc"] = pd.to_datetime(frame["openTimeUtc"], utc=True)
    frame = frame.set_index("openTimeUtc").sort_index()
    if frame.index.has_duplicates or not frame.index.is_monotonic_increasing:
        raise ValueError("candles must be unique and chronological")
    results = [_evaluate(frame, request, candidate) for candidate in request["candidates"]]
    results.sort(key=lambda value: (-value["metrics"]["score"], value["candidateKey"]))
    return {"schemaVersion": 1, "workerId": WORKER_ID, "workerVersion": str(vbt.__version__),
            "role": "researchExploration", "requestId": request["requestId"],
            "requestSha256": request["requestSha256"],
            "candidates": results[: int(request["topCandidates"])], "evidenceSha256": ""}


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
    request = json.loads(request_path.read_text(encoding="utf-8"))
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
