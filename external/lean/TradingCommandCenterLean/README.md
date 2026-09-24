# Trading Command Center LEAN validator

This project is an independent QuantConnect LEAN implementation of `vwap-ema-trend-breakout-v1`. It reads the M29 package selected by the `tccRunId` parameter from LEAN's data directory, verifies `candles.csv`, independently calculates indicators/signals and execution, submits the resulting fills through LEAN, and writes portable `evidence.json`.

It does not reference or load any `Trading.*` assembly. The other four strategies remain unsupported until separately ported and validated.
