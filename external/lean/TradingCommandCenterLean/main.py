from AlgorithmImports import *
from datetime import datetime, timedelta, timezone
from decimal import Decimal, ROUND_FLOOR, ROUND_HALF_EVEN, getcontext
from pathlib import Path
import csv
import hashlib
import json

getcontext().prec = 40
IST = timezone(timedelta(minutes=330))
ZERO = Decimal(0)
HUNDRED = Decimal(100)


class TccLeanValidationAlgorithm(QCAlgorithm):
    """Independent LEAN implementation. It never imports Trading.* or the native strategy assembly."""

    def initialize(self):
        self._run_id = self.get_parameter("tccRunId")
        if not self._run_id:
            raise ValueError("tccRunId is required")
        self._directory = Path(Globals.data_folder) / "tcc-lean" / self._run_id
        request_path = self._directory / "request.json"
        candle_path = self._directory / "candles.csv"
        self._request = json.loads(request_path.read_text(encoding="utf-8"), parse_float=Decimal)
        if self._request.get("schemaVersion") != 2 or self._request.get("runId") != self._run_id:
            raise ValueError("invalid TCC LEAN request")
        if self._request["specification"]["strategyId"] != "vwap-ema-trend-breakout-v1":
            raise ValueError("strategy has no independent LEAN implementation")
        candle_bytes = candle_path.read_bytes()
        if hashlib.sha256(candle_bytes).hexdigest() != self._request["candleFileSha256"]:
            raise ValueError("candles.csv hash mismatch")
        self._bars = self._read_bars(candle_path)
        if len(self._bars) != self._request["candleCount"]:
            raise ValueError("candle count mismatch")

        first = self._bars[0]["local"].date()
        last = self._bars[-1]["local"].date()
        self.set_time_zone("Asia/Kolkata")
        self.set_start_date(first.year, first.month, first.day)
        self.set_end_date(last.year, last.month, last.day)
        capital = self._d(self._request["specification"]["capital"]["initialCapital"])
        self.set_cash(float(capital))
        TccCandle.path = str(candle_path)
        TccCandle.timeframe_minutes = int(self._request["specification"]["data"]["timeframeMinutes"])
        ticker = self._request["specification"]["instrument"]["tradingSymbol"].replace(" ", "_")
        self._symbol = self.add_data(TccCandle, ticker, Resolution.MINUTE).symbol
        security = self.securities[self._symbol]
        security.set_fill_model(TccExactFillModel())
        security.set_fee_model(TccZeroFeeModel())
        security.set_buying_power_model(SecurityMarginModel(100))
        self._actions, self._run = self._simulate()
        self._submitted = 0

    def on_data(self, data):
        point = data.get(TccCandle, self._symbol)
        if point is None:
            return
        key = self._utc(point.open_time_utc)
        for action in self._actions.get(key, []):
            tag = json.dumps({"fillPrice": format(action["price"], "f"), "fillTimeUtc": key},
                             separators=(",", ":"))
            self.market_order(self._symbol, action["quantity"], False, tag)
            self._submitted += 1

    def on_end_of_algorithm(self):
        expected = sum(len(value) for value in self._actions.values())
        if self._submitted != expected:
            raise ValueError(f"LEAN consumed {self._submitted} of {expected} planned order actions")
        (self._directory / "evidence.json").write_text(self._json(self._run), encoding="utf-8")

    def _read_bars(self, path):
        rows = []
        with path.open("r", encoding="utf-8", newline="") as stream:
            for item in csv.DictReader(stream):
                utc = datetime.fromisoformat(item["openTimeUtc"].replace("Z", "+00:00"))
                rows.append({"utc": utc, "local": utc.astimezone(IST), "open": self._d(item["open"]),
                             "high": self._d(item["high"]), "low": self._d(item["low"]),
                             "close": self._d(item["close"]), "volume": self._d(item["volume"])})
        if not rows or any(rows[i]["utc"] <= rows[i - 1]["utc"] for i in range(1, len(rows))):
            raise ValueError("candles must be non-empty, unique and chronological")
        return rows

    def _simulate(self):
        spec = self._request["specification"]
        p = spec["parameters"]
        points = self._indicators(p)
        signals = {}
        sessions = [bar["local"].date() for bar in self._bars]
        start = int(p["entryWindowStartMinuteOfDay"])
        end = int(p["entryWindowEndMinuteOfDay"])
        lookback = int(p["breakoutLookbackBars"])
        for i, bar in enumerate(self._bars):
            minute = bar["local"].hour * 60 + bar["local"].minute
            point = points[i]
            if not start <= minute < end or i == 0:
                continue
            session_start = i
            while session_start > 0 and sessions[session_start - 1] == sessions[i]:
                session_start -= 1
            previous_average = points[i - 1]["averageVolume"]
            if (i - session_start < lookback or any(point[name] is None for name in
                    ("fastEma", "slowEma", "sessionVwap", "atr", "adx", "positiveDi", "negativeDi")) or \
                    previous_average is None or point["atr"] <= 0 or previous_average <= 0 or bar["volume"] <= 0 or \
                    point["adx"] < self._d(p["minimumAdx"]) or \
                    bar["volume"] < previous_average * self._d(p["volumeMultiplier"])):
                continue
            prior = self._bars[i - lookback:i]
            prior_high = max(x["high"] for x in prior)
            prior_low = min(x["low"] for x in prior)
            direction = None
            if point["fastEma"] > point["slowEma"] and bar["close"] > point["sessionVwap"] and \
                    point["positiveDi"] > point["negativeDi"] and bar["close"] > prior_high:
                direction = "long"
            elif point["fastEma"] < point["slowEma"] and bar["close"] < point["sessionVwap"] and \
                    point["negativeDi"] > point["positiveDi"] and bar["close"] < prior_low:
                direction = "short"
            if direction:
                risk = point["atr"] * self._d(p["atrStopMultiple"])
                reward = risk * self._d(p["rewardRiskMultiple"])
                stop = bar["close"] - risk if direction == "long" else bar["close"] + risk
                target = bar["close"] + reward if direction == "long" else bar["close"] - reward
                if stop > 0 and target > 0:
                    signals[bar["utc"]] = {"direction": direction, "risk": risk,
                                           "rewardRisk": self._d(p["rewardRiskMultiple"])}

        actions, trades, ignored = {}, [], []
        pending = None
        position = None
        capital = self._d(spec["capital"]["initialCapital"])
        exit_time = spec["execution"]["sessionExitTime"]
        exit_parts = [int(value) for value in exit_time.split(":")[:2]]
        exit_minute = exit_parts[0] * 60 + exit_parts[1]
        slippage = self._d(spec["execution"]["slippageBasisPointsPerSide"])

        def action(utc, quantity, price):
            actions.setdefault(self._utc(utc), []).append({"quantity": quantity, "price": price})

        def close(open_position, bar, raw, reason):
            nonlocal capital
            direction = Decimal(1) if open_position["direction"] == "long" else Decimal(-1)
            exit_price = raw * (Decimal(1) - slippage / Decimal(10000)) if direction == 1 else \
                raw * (Decimal(1) + slippage / Decimal(10000))
            gross = ((exit_price - open_position["entryPrice"]) * direction *
                     Decimal(open_position["quantity"])).quantize(Decimal("0.01"), rounding=ROUND_HALF_EVEN)
            capital += gross
            action(bar["utc"], -open_position["signedQuantity"], exit_price)
            trades.append({"strategyId": spec["strategyId"],
                "instrumentId": spec["instrument"]["instrumentId"], "direction": open_position["direction"],
                "signalTimeUtc": self._utc(open_position["signalTime"]),
                "entryTimeUtc": self._utc(open_position["entryTime"]), "exitTimeUtc": self._utc(bar["utc"]),
                "quantity": open_position["quantity"], "entryPrice": open_position["entryPrice"],
                "stopPrice": open_position["stop"], "targetPrice": open_position["target"],
                "exitPrice": exit_price, "exitReason": reason, "grossPnl": gross, "costs": ZERO,
                "netPnl": gross, "capitalAfterTrade": capital})

        for i, bar in enumerate(self._bars):
            minute = bar["local"].hour * 60 + bar["local"].minute
            session = bar["local"].date()
            if position is not None and session != position["session"]:
                close(position, self._bars[i - 1], self._bars[i - 1]["close"], "session-exit")
                position = None
            if pending is not None:
                if session != pending["session"]:
                    ignored.append({"signalTimeUtc": self._utc(pending["time"]),
                                    "reason": "following-bar-in-different-session"})
                elif minute >= exit_minute:
                    ignored.append({"signalTimeUtc": self._utc(pending["time"]),
                                    "reason": "following-bar-at-or-after-session-exit"})
                else:
                    direction = pending["signal"]["direction"]
                    entry = bar["open"] * (Decimal(1) + slippage / Decimal(10000)) if direction == "long" else \
                        bar["open"] * (Decimal(1) - slippage / Decimal(10000))
                    stop = entry - pending["signal"]["risk"] if direction == "long" else entry + pending["signal"]["risk"]
                    target_distance = pending["signal"]["risk"] * pending["signal"]["rewardRisk"]
                    target = entry + target_distance if direction == "long" else entry - target_distance
                    if stop <= 0 or target <= 0:
                        ignored.append({"signalTimeUtc": self._utc(pending["time"]), "reason": "invalid-entry-levels"})
                    else:
                        allowed = self._d(spec["capital"]["riskPerTrade"])
                        maximum = self._d(spec["capital"]["maximumCapitalPerTrade"])
                        lot = int(spec["instrument"]["lotSize"])
                        units = min((allowed / abs(entry - stop)).to_integral_value(rounding=ROUND_FLOOR),
                                    (maximum / entry).to_integral_value(rounding=ROUND_FLOOR))
                        lots = int((units / Decimal(lot)).to_integral_value(rounding=ROUND_FLOOR))
                        maximum_lots = spec["capital"].get("maximumLots")
                        if maximum_lots is not None:
                            lots = min(lots, int(maximum_lots))
                        quantity = lots * lot
                        if quantity == 0:
                            ignored.append({"signalTimeUtc": self._utc(pending["time"]),
                                            "reason": "insufficient-risk-or-capital"})
                        else:
                            signed = quantity if direction == "long" else -quantity
                            action(bar["utc"], signed, entry)
                            position = {"direction": direction, "signalTime": pending["time"],
                                "entryTime": bar["utc"], "entryPrice": entry, "stop": stop, "target": target,
                                "quantity": quantity, "signedQuantity": signed, "session": session}
                pending = None
            if position is not None:
                raw, reason = None, None
                if minute >= exit_minute:
                    raw, reason = bar["open"], "session-exit"
                elif position["direction"] == "long":
                    if bar["open"] <= position["stop"]: raw, reason = bar["open"], "stop-loss"
                    elif bar["open"] >= position["target"]: raw, reason = position["target"], "target"
                    elif bar["low"] <= position["stop"]: raw, reason = position["stop"], "stop-loss"
                    elif bar["high"] >= position["target"]: raw, reason = position["target"], "target"
                else:
                    if bar["open"] >= position["stop"]: raw, reason = bar["open"], "stop-loss"
                    elif bar["open"] <= position["target"]: raw, reason = position["target"], "target"
                    elif bar["high"] >= position["stop"]: raw, reason = position["stop"], "stop-loss"
                    elif bar["low"] <= position["target"]: raw, reason = position["target"], "target"
                if raw is not None:
                    close(position, bar, raw, reason)
                    position = None
            if bar["utc"] in signals:
                if position is not None:
                    ignored.append({"signalTimeUtc": self._utc(bar["utc"]), "reason": "position-already-open"})
                elif i == len(self._bars) - 1:
                    ignored.append({"signalTimeUtc": self._utc(bar["utc"]), "reason": "no-following-bar"})
                else:
                    pending = {"time": bar["utc"], "session": session, "signal": signals[bar["utc"]]}
        if position is not None:
            close(position, self._bars[-1], self._bars[-1]["close"], "end-of-data")

        net_pnl = sum((trade["netPnl"] for trade in trades), ZERO)
        initial_capital = self._d(spec["capital"]["initialCapital"])
        run = {"schemaVersion": 1, "engineId": "lean",
            "engineVersion": "2:" + self._request["leanImage"], "engineRole": "independentValidation",
            "specificationSha256": self._request["specificationSha256"],
            "declaredDatasetSha256": spec["data"]["datasetSha256"],
            "consumedMarketDataSha256": self._request["consumedMarketDataSha256"],
            "initialCapital": initial_capital, "finalCapital": initial_capital + net_pnl,
            "netPnl": net_pnl,
            "winningTrades": sum(1 for trade in trades if trade["netPnl"] > 0),
            "losingTrades": sum(1 for trade in trades if trade["netPnl"] < 0),
            "trades": trades, "ignoredCandidates": ignored, "resultSha256": ""}
        return actions, run

    def _indicators(self, p):
        count = len(self._bars)
        close = [x["close"] for x in self._bars]
        high = [x["high"] for x in self._bars]
        low = [x["low"] for x in self._bars]
        volume = [x["volume"] for x in self._bars]
        fast_period, slow_period = int(p["fastEmaPeriod"]), int(p["slowEmaPeriod"])
        atr_period, adx_period = int(p["atrPeriod"]), int(p["adxPeriod"])
        volume_period = int(p["volumeAveragePeriod"])

        def ema(period):
            result = [None] * count
            if count < period: return result
            current = sum(close[:period], ZERO) / Decimal(period)
            result[period - 1] = current
            multiplier = Decimal(2) / Decimal(period + 1)
            for i in range(period, count):
                current = ((close[i] - current) * multiplier) + current
                result[i] = current
            return result

        tr = [high[0] - low[0]]
        for i in range(1, count):
            tr.append(max(high[i] - low[i], abs(high[i] - close[i - 1]), abs(low[i] - close[i - 1])))
        atr = [None] * count
        if count >= atr_period:
            current = sum(tr[:atr_period], ZERO) / Decimal(atr_period)
            atr[atr_period - 1] = current
            for i in range(atr_period, count):
                current = (current * Decimal(atr_period - 1) + tr[i]) / Decimal(atr_period)
                atr[i] = current
        plus_dm, minus_dm = [ZERO] * count, [ZERO] * count
        for i in range(1, count):
            up, down = high[i] - high[i - 1], low[i - 1] - low[i]
            plus_dm[i] = up if up > down and up > 0 else ZERO
            minus_dm[i] = down if down > up and down > 0 else ZERO
        adx, positive_di, negative_di = [None] * count, [None] * count, [None] * count
        if count > adx_period:
            smooth_tr = sum(tr[1:adx_period + 1], ZERO)
            smooth_plus = sum(plus_dm[1:adx_period + 1], ZERO)
            smooth_minus = sum(minus_dm[1:adx_period + 1], ZERO)
            dx = [None] * count
            first_adx, current_adx = 2 * adx_period - 1, None
            for i in range(adx_period, count):
                if i > adx_period:
                    smooth_tr = smooth_tr - smooth_tr / Decimal(adx_period) + tr[i]
                    smooth_plus = smooth_plus - smooth_plus / Decimal(adx_period) + plus_dm[i]
                    smooth_minus = smooth_minus - smooth_minus / Decimal(adx_period) + minus_dm[i]
                positive = ZERO if smooth_tr == 0 else HUNDRED * smooth_plus / smooth_tr
                negative = ZERO if smooth_tr == 0 else HUNDRED * smooth_minus / smooth_tr
                positive_di[i], negative_di[i] = positive, negative
                total = positive + negative
                dx[i] = ZERO if total == 0 else HUNDRED * abs(positive - negative) / total
                if i == first_adx:
                    current_adx = sum(dx[j] for j in range(adx_period, first_adx + 1)) / Decimal(adx_period)
                elif i > first_adx:
                    current_adx = (current_adx * Decimal(adx_period - 1) + dx[i]) / Decimal(adx_period)
                adx[i] = current_adx
        average = [None] * count
        running = ZERO
        for i in range(count):
            running += volume[i]
            if i >= volume_period: running -= volume[i - volume_period]
            if i >= volume_period - 1: average[i] = running / Decimal(volume_period)
        vwap, session, weighted, session_volume = [None] * count, None, ZERO, ZERO
        for i, bar in enumerate(self._bars):
            if bar["local"].date() != session:
                session, weighted, session_volume = bar["local"].date(), ZERO, ZERO
            weighted += (high[i] + low[i] + close[i]) / Decimal(3) * volume[i]
            session_volume += volume[i]
            vwap[i] = None if session_volume == 0 else weighted / session_volume
        fast, slow = ema(fast_period), ema(slow_period)
        return [{"fastEma": fast[i], "slowEma": slow[i], "atr": atr[i], "adx": adx[i],
                 "positiveDi": positive_di[i], "negativeDi": negative_di[i],
                 "averageVolume": average[i], "sessionVwap": vwap[i]} for i in range(count)]

    @staticmethod
    def _d(value):
        return value if isinstance(value, Decimal) else Decimal(str(value))

    @staticmethod
    def _utc(value):
        return value.astimezone(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")

    @classmethod
    def _json(cls, value):
        if value is None: return "null"
        if value is True: return "true"
        if value is False: return "false"
        if isinstance(value, Decimal): return format(value, "f")
        if isinstance(value, int): return str(value)
        if isinstance(value, str): return json.dumps(value, ensure_ascii=False)
        if isinstance(value, list): return "[" + ",".join(cls._json(x) for x in value) + "]"
        if isinstance(value, dict):
            return "{" + ",".join(json.dumps(str(k)) + ":" + cls._json(v) for k, v in value.items()) + "}"
        raise TypeError(type(value).__name__)


class TccCandle(PythonData):
    path = None
    timeframe_minutes = 5

    def get_source(self, config, date, is_live):
        return SubscriptionDataSource(TccCandle.path, SubscriptionTransportMedium.LOCAL_FILE)

    def reader(self, config, line, date, is_live):
        if not line or line.startswith("openTimeUtc"):
            return None
        row = line.split(",")
        utc = datetime.fromisoformat(row[0].replace("Z", "+00:00"))
        local = utc.astimezone(IST).replace(tzinfo=None)
        if local.date() != date.date():
            return None
        item = TccCandle()
        item.symbol = config.symbol
        item.time = local
        item.end_time = local + timedelta(minutes=TccCandle.timeframe_minutes)
        item.open_time_utc = utc
        item.open, item.high, item.low, item.close = map(Decimal, row[1:5])
        item.volume = Decimal(row[5])
        item.value = item.close
        return item


class TccExactFillModel(ImmediateFillModel):
    def market_fill(self, asset, order):
        payload = json.loads(order.tag)
        utc = datetime.fromisoformat(payload["fillTimeUtc"].replace("Z", "+00:00"))
        fill = OrderEvent(order, utc, OrderFee.ZERO)
        fill.status = OrderStatus.FILLED
        fill.fill_quantity = order.quantity
        fill.fill_price = Decimal(payload["fillPrice"])
        return fill


class TccZeroFeeModel(FeeModel):
    def get_order_fee(self, parameters):
        return OrderFee.ZERO
