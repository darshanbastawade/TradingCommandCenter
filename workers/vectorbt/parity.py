"""Exact native-C# indicator and signal port used by vectorbt screening and parity tests."""
from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timedelta, timezone
from decimal import Decimal, getcontext
from pathlib import Path

getcontext().prec = 40
STRATEGY_PORT_VERSION = "native-signals-v1"
SUPPORTED_STRATEGIES = (
    "vwap-ema-trend-breakout-v1", "opening-range-breakout-v1",
    "ema-pullback-continuation-v1", "vwap-reclaim-rejection-v1",
    "adx-trend-continuation-v1",
)
ZERO = Decimal(0)
HUNDRED = Decimal(100)


def port_sha256() -> str:
    source = Path(__file__).read_text(encoding="utf-8").replace("\r\n", "\n")
    return hashlib.sha256(source.encode("utf-8")).hexdigest()


def parity_evidence(strategy_id: str) -> dict:
    manifest_path = Path(__file__).with_name("parity-manifest.json")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected_hash = port_sha256()
    strategies = manifest.get("strategies", {})
    if (manifest.get("schemaVersion") != 1 or
            manifest.get("strategyPortVersion") != STRATEGY_PORT_VERSION or
            manifest.get("strategyPortSha256") != expected_hash or
            strategies.get(strategy_id) is not True):
        raise ValueError(f"strategy parity is absent or stale: {strategy_id}")
    return {"strategyId": strategy_id, "passed": True,
            "strategyPortVersion": STRATEGY_PORT_VERSION,
            "strategyPortSha256": expected_hash}


def _d(value) -> Decimal:
    return value if isinstance(value, Decimal) else Decimal(str(value))


def _period(parameters: dict, name: str) -> int:
    value = _d(parameters[name])
    if value != value.to_integral_value():
        raise ValueError(f"{name} must be integral")
    return int(value)


def _local(value: str, zone_id: str) -> datetime:
    utc = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if zone_id not in ("India Standard Time", "Asia/Kolkata"):
        raise ValueError(f"unsupported parity time zone: {zone_id}")
    return utc.astimezone(timezone(timedelta(minutes=330)))


def indicators(candles: list[dict], parameters: dict, zone_id: str) -> list[dict]:
    count = len(candles)
    close = [_d(x["close"]) for x in candles]
    high = [_d(x["high"]) for x in candles]
    low = [_d(x["low"]) for x in candles]
    volume = [_d(x["volume"]) for x in candles]
    fast_period, slow_period = _period(parameters, "fastEmaPeriod"), _period(parameters, "slowEmaPeriod")
    atr_period, adx_period = _period(parameters, "atrPeriod"), _period(parameters, "adxPeriod")
    volume_period = _period(parameters, "volumeAveragePeriod")

    def ema(period: int) -> list[Decimal | None]:
        values: list[Decimal | None] = [None] * count
        if count < period:
            return values
        current = sum(close[:period], ZERO) / Decimal(period)
        values[period - 1] = current
        multiplier = Decimal(2) / Decimal(period + 1)
        for i in range(period, count):
            current = ((close[i] - current) * multiplier) + current
            values[i] = current
        return values

    tr = [high[0] - low[0]] if count else []
    for i in range(1, count):
        tr.append(max(high[i] - low[i], abs(high[i] - close[i - 1]), abs(low[i] - close[i - 1])))
    atr: list[Decimal | None] = [None] * count
    if count >= atr_period:
        current_atr = sum(tr[:atr_period], ZERO) / Decimal(atr_period)
        atr[atr_period - 1] = current_atr
        for i in range(atr_period, count):
            current_atr = ((current_atr * Decimal(atr_period - 1)) + tr[i]) / Decimal(atr_period)
            atr[i] = current_atr

    plus_dm, minus_dm = [ZERO] * count, [ZERO] * count
    for i in range(1, count):
        up, down = high[i] - high[i - 1], low[i - 1] - low[i]
        plus_dm[i] = up if up > down and up > 0 else ZERO
        minus_dm[i] = down if down > up and down > 0 else ZERO
    adx, plus_di, minus_di = [None] * count, [None] * count, [None] * count
    if count > adx_period:
        smooth_tr = sum(tr[1:adx_period + 1], ZERO)
        smooth_plus = sum(plus_dm[1:adx_period + 1], ZERO)
        smooth_minus = sum(minus_dm[1:adx_period + 1], ZERO)
        dx: list[Decimal | None] = [None] * count
        first_adx = 2 * adx_period - 1
        current_adx = None
        for i in range(adx_period, count):
            if i > adx_period:
                smooth_tr = smooth_tr - (smooth_tr / Decimal(adx_period)) + tr[i]
                smooth_plus = smooth_plus - (smooth_plus / Decimal(adx_period)) + plus_dm[i]
                smooth_minus = smooth_minus - (smooth_minus / Decimal(adx_period)) + minus_dm[i]
            positive = ZERO if smooth_tr == 0 else HUNDRED * smooth_plus / smooth_tr
            negative = ZERO if smooth_tr == 0 else HUNDRED * smooth_minus / smooth_tr
            plus_di[i], minus_di[i] = positive, negative
            total = positive + negative
            dx[i] = ZERO if total == 0 else HUNDRED * abs(positive - negative) / total
            if i == first_adx:
                current_adx = sum((dx[j] for j in range(adx_period, first_adx + 1)), ZERO) / Decimal(adx_period)
            elif i > first_adx:
                current_adx = ((current_adx * Decimal(adx_period - 1)) + dx[i]) / Decimal(adx_period)
            adx[i] = current_adx

    average_volume: list[Decimal | None] = [None] * count
    running = ZERO
    for i in range(count):
        running += volume[i]
        if i >= volume_period:
            running -= volume[i - volume_period]
        if i >= volume_period - 1:
            average_volume[i] = running / Decimal(volume_period)

    vwap: list[Decimal | None] = [None] * count
    session, weighted, session_volume = None, ZERO, ZERO
    for i, candle in enumerate(candles):
        date = _local(candle["openTimeUtc"], zone_id).date()
        if date != session:
            session, weighted, session_volume = date, ZERO, ZERO
        typical = (high[i] + low[i] + close[i]) / Decimal(3)
        weighted += typical * volume[i]
        session_volume += volume[i]
        vwap[i] = None if session_volume == 0 else weighted / session_volume

    fast_values, slow_values = ema(fast_period), ema(slow_period)
    return [{"fastEma": fast_values[i], "slowEma": slow_values[i], "atr": atr[i], "adx": adx[i],
             "positiveDi": plus_di[i], "negativeDi": minus_di[i],
             "averageVolume": average_volume[i], "sessionVwap": vwap[i]} for i in range(count)]


def signals(candles: list[dict], parameters: dict, strategy_id: str, zone_id: str) -> tuple[list[dict], list[dict]]:
    if strategy_id not in SUPPORTED_STRATEGIES:
        raise ValueError(f"unsupported strategy: {strategy_id}")
    points = indicators(candles, parameters, zone_id)
    start, end = _period(parameters, "entryWindowStartMinuteOfDay"), _period(parameters, "entryWindowEndMinuteOfDay")
    minimum_adx = _d(parameters.get("minimumAdx", 0))
    volume_multiplier = _d(parameters.get("volumeMultiplier", 0))
    sessions = [_local(x["openTimeUtc"], zone_id).date() for x in candles]
    minutes = [x.hour * 60 + x.minute for x in (_local(c["openTimeUtc"], zone_id) for c in candles)]
    output: list[dict] = []
    for i, candle in enumerate(candles):
        if not start <= minutes[i] < end:
            continue
        point = points[i]
        direction = None
        previous_average = points[i - 1]["averageVolume"] if i > 0 else None
        common = (point["fastEma"] is not None and point["slowEma"] is not None and
                  point["sessionVwap"] is not None and point["atr"] is not None and point["atr"] > 0 and
                  point["adx"] is not None and point["positiveDi"] is not None and
                  point["negativeDi"] is not None and previous_average is not None and previous_average > 0)
        if not common:
            continue
        close, high, low, volume = _d(candle["close"]), _d(candle["high"]), _d(candle["low"]), _d(candle["volume"])
        if strategy_id == "vwap-ema-trend-breakout-v1":
            lookback = _period(parameters, "breakoutLookbackBars")
            session_start = next(j for j in range(i, -1, -1) if j == 0 or sessions[j - 1] != sessions[i])
            if i - session_start < lookback or i == 0 or point["adx"] < minimum_adx or volume <= 0 or volume < previous_average * volume_multiplier:
                continue
            prior = candles[i - lookback:i]
            prior_high, prior_low = max(_d(x["high"]) for x in prior), min(_d(x["low"]) for x in prior)
            if point["fastEma"] > point["slowEma"] and close > point["sessionVwap"] and point["positiveDi"] > point["negativeDi"] and close > prior_high:
                direction = "long"
            elif point["fastEma"] < point["slowEma"] and close < point["sessionVwap"] and point["negativeDi"] > point["positiveDi"] and close < prior_low:
                direction = "short"
        elif strategy_id == "opening-range-breakout-v1":
            bars = _period(parameters, "openingRangeBars")
            indices = [j for j in range(len(candles)) if sessions[j] == sessions[i]]
            position = indices.index(i)
            if position < bars or i == 0 or volume < previous_average * volume_multiplier:
                continue
            opening = [candles[j] for j in indices[:bars]]
            opening_high, opening_low = max(_d(x["high"]) for x in opening), min(_d(x["low"]) for x in opening)
            direction = "long" if close > opening_high else "short" if close < opening_low else None
        elif strategy_id == "ema-pullback-continuation-v1":
            if i == 0 or sessions[i] != sessions[i - 1] or points[i - 1]["fastEma"] is None or point["adx"] < minimum_adx or volume < previous_average * volume_multiplier:
                continue
            previous, prior_point = candles[i - 1], points[i - 1]
            if point["fastEma"] > point["slowEma"] and close > point["sessionVwap"] and point["positiveDi"] > point["negativeDi"] and _d(previous["low"]) <= prior_point["fastEma"] and _d(previous["close"]) <= prior_point["fastEma"] and close > point["fastEma"] and high > _d(previous["high"]):
                direction = "long"
            elif point["fastEma"] < point["slowEma"] and close < point["sessionVwap"] and point["negativeDi"] > point["positiveDi"] and _d(previous["high"]) >= prior_point["fastEma"] and _d(previous["close"]) >= prior_point["fastEma"] and close < point["fastEma"] and low < _d(previous["low"]):
                direction = "short"
        elif strategy_id == "vwap-reclaim-rejection-v1":
            if i == 0 or sessions[i] != sessions[i - 1] or points[i - 1]["sessionVwap"] is None or point["adx"] < minimum_adx or volume < previous_average * volume_multiplier:
                continue
            previous, prior_point = candles[i - 1], points[i - 1]
            if point["fastEma"] > point["slowEma"] and _d(previous["close"]) <= prior_point["sessionVwap"] and close > point["sessionVwap"]:
                direction = "long"
            elif point["fastEma"] < point["slowEma"] and _d(previous["close"]) >= prior_point["sessionVwap"] and close < point["sessionVwap"]:
                direction = "short"
        else:
            if i == 0 or sessions[i] != sessions[i - 1] or points[i - 1]["adx"] is None or point["adx"] < minimum_adx or point["adx"] <= points[i - 1]["adx"]:
                continue
            previous = candles[i - 1]
            if point["fastEma"] > point["slowEma"] and point["positiveDi"] > point["negativeDi"] and close > point["fastEma"] and close > _d(previous["high"]):
                direction = "long"
            elif point["fastEma"] < point["slowEma"] and point["negativeDi"] > point["positiveDi"] and close < point["fastEma"] and close < _d(previous["low"]):
                direction = "short"
        if direction:
            risk = point["atr"] * _d(parameters["atrStopMultiple"])
            reward = risk * _d(parameters["rewardRiskMultiple"])
            stop = close - risk if direction == "long" else close + risk
            target = close + reward if direction == "long" else close - reward
            if stop > 0 and target > 0:
                output.append({"timestampUtc": candle["openTimeUtc"], "direction": direction})
    return output, points


def _json_value(value):
    if isinstance(value, Decimal):
        return format(value, "f")
    if isinstance(value, list):
        return [_json_value(x) for x in value]
    if isinstance(value, dict):
        return {k: _json_value(v) for k, v in value.items()}
    return value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    fixture = json.loads(Path(args.fixture).read_text(encoding="utf-8"), parse_float=Decimal)
    evidence = parity_evidence(fixture["strategyId"])
    actual, points = signals(fixture["candles"], fixture["parameters"], fixture["strategyId"], fixture["exchangeTimeZoneId"])
    result = {"strategyPortVersion": evidence["strategyPortVersion"],
              "strategyPortSha256": evidence["strategyPortSha256"],
              "signals": actual, "indicators": points}
    Path(args.output).write_text(json.dumps(_json_value(result), separators=(",", ":")), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
