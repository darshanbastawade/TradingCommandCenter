"""CI smoke test for the real pinned vectorbt worker runtime."""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import vectorbt as vbt

import worker


def main() -> None:
    if str(vbt.__version__) != worker.EXPECTED_VERSION:
        raise AssertionError(
            f"expected vectorbt {worker.EXPECTED_VERSION}, found {vbt.__version__}"
        )

    start = datetime(2025, 1, 6, 3, 45, tzinfo=timezone.utc)
    candles = []
    previous = 100.0
    for index in range(80):
        close = previous + 0.35
        candles.append(
            {
                "openTimeUtc": (start + timedelta(minutes=5 * index)).isoformat().replace("+00:00", "Z"),
                "open": previous,
                "high": close + 0.2,
                "low": previous - 0.2,
                "close": close,
                "volume": 300 if index == 20 else 100,
            }
        )
        previous = close

    parameters = {
        "fastEmaPeriod": 3,
        "slowEmaPeriod": 6,
        "atrPeriod": 3,
        "adxPeriod": 3,
        "volumeAveragePeriod": 3,
        "minimumAdx": 5,
        "volumeMultiplier": 1.1,
        "atrStopMultiple": 1,
        "rewardRiskMultiple": 3,
        "entryWindowStartMinuteOfDay": 555,
        "entryWindowEndMinuteOfDay": 930,
        "breakoutLookbackBars": 3,
    }
    request = {
        "schemaVersion": 1,
        "requestId": "ci-vectorbt-smoke",
        "requestSha256": "a" * 64,
        "strategyId": "vwap-ema-trend-breakout-v1",
        "exchangeTimeZoneId": "Asia/Kolkata",
        "sessionExitTime": "15:25:00",
        "timeframeMinutes": 5,
        "initialCapital": 100000,
        "slippageBasisPointsPerSide": 1,
        "topCandidates": 1,
        "candles": candles,
        "candidates": [{"candidateKey": "b" * 64, "parameters": parameters}],
    }

    result = worker.run(request)
    if result["schemaVersion"] != 2 or result["role"] != "researchExploration":
        raise AssertionError("worker returned an unexpected contract")
    if len(result["candidates"]) != 1:
        raise AssertionError("worker did not return the requested candidate")
    parity = result["parityEvidence"]
    if not parity["passed"] or len(parity["strategyPortSha256"]) != 64:
        raise AssertionError("worker did not return valid parity evidence")
    print(
        f"vectorbt smoke passed: version={vbt.__version__}, "
        f"trades={result['candidates'][0]['metrics']['tradeCount']}"
    )


if __name__ == "__main__":
    main()

