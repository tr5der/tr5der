using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ReversalScalpingFuturesStrategy : Strategy
    {
        public enum StopMethodType
        {
            SwingExtreme,
            SignalBar,
            FixedTicks
        }

        public enum TrailMethodType
        {
            Swing,
            FixedTicks
        }

        private enum ZoneBias
        {
            Support,
            Resistance,
            Neutral
        }

        private sealed class ZoneLevel
        {
            public double Low;
            public double High;
            public int Touches;
            public ZoneBias Bias;
            public DateTime Timestamp;
            public bool IsSpecial;

            public double Center
            {
                get { return (Low + High) * 0.5; }
            }
        }

        private struct PivotPoint
        {
            public int BarsAgo;
            public double Price;
        }

        private struct PatternSignal
        {
            public double TriggerPrice;
            public double StopAnchorPrice;
            public bool IsEngulfing;
        }

        private struct TradePlan
        {
            public bool IsLong;
            public int Quantity;
            public int TimingBucket;
            public double EntryPrice;
            public double StopPrice;
            public double TargetPrice;
        }

        private const string LongSignalName = "REV_LONG";
        private const string ShortSignalName = "REV_SHORT";

        private ATR atr;
        private readonly List<ZoneLevel> zones = new List<ZoneLevel>();

        private bool sessionInitialized;
        private bool cashOpenTriggered;
        private bool hasPriorSession;
        private bool hasOvernightData;

        private DateTime sessionStartTime;
        private DateTime cashOpenCandidateTime;
        private DateTime cashOpenTime;
        private DateTime positionEntryTime;

        private double currentSessionHigh;
        private double currentSessionLow;
        private double currentSessionClose;
        private double priorSessionHigh;
        private double priorSessionLow;
        private double priorSessionClose;
        private double overnightHigh;
        private double overnightLow;
        private double openRangeHigh;
        private double openRangeLow;

        private int tradesThisSessionWindow;
        private bool usedTiming15;
        private bool usedTiming30;
        private bool usedTiming90;

        private Order workingEntryOrder;
        private int workingEntryBar;
        private int pendingTimingBucket;

        private double activeStopPrice;
        private double initialRiskPoints;
        private bool breakEvenMoved;
        private bool scaleOutDone;
        private MarketPosition priorMarketPosition;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ReversalScalpingFuturesStrategy";
                Description = "Time-window reversal scalper with zone, volatility, trend-shift, and R-multiple management.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;

                UseTiming15m = true;
                UseTiming30m = true;
                UseTiming90m = false;
                TimingWindowToleranceMinutes = 2;

                SessionOpenOffsetMinutes = 0;
                TradeWindowMinutesAfterOpen = 60;
                MaxTradesPerSession = 1;
                OneTradePerTimingWindow = false;
                EntryExpiryBars = 3;

                ZoneTimeframeMinutes = 15;
                ZoneLookbackDays = 3;
                PivotStrength = 4;
                MinTouchesForZone = 2;
                ZonePaddingTicks = 8;
                ZoneProximityTicks = 8;
                MaxZonesToTrack = 10;
                UsePriorDayHLC = true;
                UseTodayOpenLevel = true;
                UseOvernightHL = false;
                UseGaps = true;
                MinGapTicks = 12;

                UseATRFilter = true;
                ATRPeriod = 14;
                MinATR = 1.0;
                UseOpenRangeFilter = true;
                OpenRangeMinutes = 10;
                MinOpenRangeTicks = 12;
                UseChopRatioFilter = true;
                ChopLookbackBars = 20;
                MaxChopRatio = 3.2;

                RequireTrendShift = true;
                StructureLookbackBars = 30;
                UseOverextensionRule = false;
                OverextensionATRMultiplier = 1.5;

                EnableDoubleBottom = true;
                DoubleBottomToleranceTicks = 4;
                EnableHigherLowBreak = true;
                EnableHeadAndShoulders = true;
                EnableEngulfing = true;
                EnableThreeLineStrike = false;
                RequireEngulfingOrStrongClose = false;
                MinReversalCandleBodyRatio = 0.6;
                EntryOffsetTicks = 1;

                StopMethod = StopMethodType.SwingExtreme;
                StopBufferTicks = 2;
                FixedStopTicks = 20;
                MaxStopTicks = 40;
                TargetR = 2.0;
                UseZoneTargets = false;

                MoveToBreakEvenEnabled = true;
                BreakEvenTriggerR = 2.0;
                BreakEvenPlusTicks = 0;
                ScaleOutEnabled = false;
                ScaleOutPercent = 50;
                ScaleOutAtR = 2.0;
                TrailEnabled = false;
                TrailMethod = TrailMethodType.Swing;
                TrailTicks = 8;
                MaxHoldMinutes = 0;

                UseFixedDollarRisk = true;
                RiskPerTradeDollars = 300;
                MinContracts = 1;
                MaxContracts = 5;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, Math.Max(1, ZoneTimeframeMinutes));
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(ATRPeriod);
                ResetRuntimeState();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                UpdateZonesFromSecondarySeries();
                return;
            }

            if (BarsInProgress != 0)
                return;

            UpdateSessionState();

            if (CurrentBar < 5)
                return;

            if (cashOpenTriggered)
                UpdateOpenRange();

            HandlePositionStateTransition();

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageOpenPosition();
                return;
            }

            ManageWorkingEntryOrder();

            if (!cashOpenTriggered)
                return;

            if (!IsWithinTradeWindow())
                return;

            if (tradesThisSessionWindow >= MaxTradesPerSession)
                return;

            if (workingEntryOrder != null || pendingTimingBucket != 0)
                return;

            int timingBucket = GetActiveTimingBucket();
            if (timingBucket == 0)
                return;

            if (OneTradePerTimingWindow && IsTimingBucketUsed(timingBucket))
                return;

            if (!HasEnoughBarsForSetup())
                return;

            if (!VolatilityFiltersPass())
                return;

            ZoneLevel longZone;
            ZoneLevel shortZone;
            double longDistanceTicks;
            double shortDistanceTicks;

            bool longZoneOk = IsNearZone(true, out longZone, out longDistanceTicks);
            bool shortZoneOk = IsNearZone(false, out shortZone, out shortDistanceTicks);

            TradePlan longPlan;
            TradePlan shortPlan;
            bool hasLongPlan = false;
            bool hasShortPlan = false;

            if (longZoneOk && TrendContextLongOk())
                hasLongPlan = TryBuildLongPlan(timingBucket, out longPlan);
            else
                longPlan = new TradePlan();

            if (shortZoneOk && TrendContextShortOk())
                hasShortPlan = TryBuildShortPlan(timingBucket, out shortPlan);
            else
                shortPlan = new TradePlan();

            if (hasLongPlan && hasShortPlan)
            {
                if (longDistanceTicks <= shortDistanceTicks)
                    hasShortPlan = false;
                else
                    hasLongPlan = false;
            }

            if (hasLongPlan)
                SubmitTradePlan(longPlan);
            else if (hasShortPlan)
                SubmitTradePlan(shortPlan);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice,
            OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            if (order.Name != LongSignalName && order.Name != ShortSignalName)
                return;

            if (orderState == OrderState.Working || orderState == OrderState.Accepted || orderState == OrderState.PartFilled)
            {
                workingEntryOrder = order;
                return;
            }

            if (workingEntryOrder != null && order.OrderId == workingEntryOrder.OrderId)
            {
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected || orderState == OrderState.Filled)
                    workingEntryOrder = null;
            }

            if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                pendingTimingBucket = 0;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.Name != LongSignalName && execution.Order.Name != ShortSignalName)
                return;

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            tradesThisSessionWindow++;
            MarkTimingBucketUsed(pendingTimingBucket);
            pendingTimingBucket = 0;

            positionEntryTime = time;
            breakEvenMoved = false;
            scaleOutDone = false;

            if (Position.MarketPosition != MarketPosition.Flat)
                initialRiskPoints = Math.Max(TickSize, Math.Abs(Position.AveragePrice - activeStopPrice));
        }

        private void ResetRuntimeState()
        {
            sessionInitialized = false;
            cashOpenTriggered = false;
            hasPriorSession = false;
            hasOvernightData = false;

            sessionStartTime = Core.Globals.MinDate;
            cashOpenCandidateTime = Core.Globals.MinDate;
            cashOpenTime = Core.Globals.MinDate;
            positionEntryTime = Core.Globals.MinDate;

            currentSessionHigh = 0;
            currentSessionLow = 0;
            currentSessionClose = 0;
            priorSessionHigh = 0;
            priorSessionLow = 0;
            priorSessionClose = 0;
            overnightHigh = 0;
            overnightLow = 0;
            openRangeHigh = 0;
            openRangeLow = 0;

            tradesThisSessionWindow = 0;
            usedTiming15 = false;
            usedTiming30 = false;
            usedTiming90 = false;

            workingEntryOrder = null;
            workingEntryBar = -1;
            pendingTimingBucket = 0;

            activeStopPrice = double.NaN;
            initialRiskPoints = double.NaN;
            breakEvenMoved = false;
            scaleOutDone = false;
            priorMarketPosition = MarketPosition.Flat;
        }

        private void UpdateSessionState()
        {
            if (Bars.IsFirstBarOfSession)
                StartNewSession();

            if (!sessionInitialized)
                return;

            currentSessionHigh = Math.Max(currentSessionHigh, High[0]);
            currentSessionLow = Math.Min(currentSessionLow, Low[0]);
            currentSessionClose = Close[0];

            if (cashOpenTriggered)
                return;

            if (Time[0] < cashOpenCandidateTime && SessionOpenOffsetMinutes > 0)
            {
                if (!hasOvernightData)
                {
                    overnightHigh = High[0];
                    overnightLow = Low[0];
                    hasOvernightData = true;
                }
                else
                {
                    overnightHigh = Math.Max(overnightHigh, High[0]);
                    overnightLow = Math.Min(overnightLow, Low[0]);
                }
            }

            if (Time[0] >= cashOpenCandidateTime)
                StartCashOpenWindow();
        }

        private void StartNewSession()
        {
            if (sessionInitialized)
            {
                priorSessionHigh = currentSessionHigh;
                priorSessionLow = currentSessionLow;
                priorSessionClose = currentSessionClose;
                hasPriorSession = true;
            }

            sessionInitialized = true;
            sessionStartTime = Time[0];
            cashOpenCandidateTime = sessionStartTime.AddMinutes(SessionOpenOffsetMinutes);
            cashOpenTriggered = false;

            currentSessionHigh = High[0];
            currentSessionLow = Low[0];
            currentSessionClose = Close[0];

            hasOvernightData = false;
            overnightHigh = 0;
            overnightLow = 0;

            CancelWorkingEntry();
        }

        private void StartCashOpenWindow()
        {
            cashOpenTriggered = true;
            cashOpenTime = Time[0];

            tradesThisSessionWindow = 0;
            usedTiming15 = false;
            usedTiming30 = false;
            usedTiming90 = false;

            openRangeHigh = High[0];
            openRangeLow = Low[0];

            if (UseTodayOpenLevel)
                AddOrMergeZone(Open[0], ZoneBias.Neutral, Time[0], true);

            if (UsePriorDayHLC && hasPriorSession)
            {
                AddOrMergeZone(priorSessionHigh, ZoneBias.Resistance, Time[0], true);
                AddOrMergeZone(priorSessionLow, ZoneBias.Support, Time[0], true);
                AddOrMergeZone(priorSessionClose, ZoneBias.Neutral, Time[0], true);
            }

            if (UseOvernightHL && hasOvernightData)
            {
                AddOrMergeZone(overnightHigh, ZoneBias.Resistance, Time[0], true);
                AddOrMergeZone(overnightLow, ZoneBias.Support, Time[0], true);
            }

            if (UseGaps && hasPriorSession)
            {
                double gapTicks = Math.Abs(Open[0] - priorSessionClose) / TickSize;
                if (gapTicks >= MinGapTicks)
                {
                    AddOrMergeZone(priorSessionClose, ZoneBias.Neutral, Time[0], true);
                    AddOrMergeZone(Open[0], ZoneBias.Neutral, Time[0], true);
                }
            }

            PruneOldZones(Time[0]);
        }

        private void UpdateOpenRange()
        {
            if (!UseOpenRangeFilter)
                return;

            double elapsed = MinutesFromCashOpen();
            if (elapsed < 0)
                return;

            if (elapsed <= OpenRangeMinutes)
            {
                openRangeHigh = Math.Max(openRangeHigh, High[0]);
                openRangeLow = Math.Min(openRangeLow, Low[0]);
            }
        }

        private void UpdateZonesFromSecondarySeries()
        {
            if (CurrentBars[1] < (PivotStrength * 2 + 1))
                return;

            int pivotBarsAgo = PivotStrength;
            DateTime pivotTime = Times[1][pivotBarsAgo];

            if (IsPivotHighSecondary(pivotBarsAgo, PivotStrength))
                AddOrMergeZone(Highs[1][pivotBarsAgo], ZoneBias.Resistance, pivotTime, false);

            if (IsPivotLowSecondary(pivotBarsAgo, PivotStrength))
                AddOrMergeZone(Lows[1][pivotBarsAgo], ZoneBias.Support, pivotTime, false);

            PruneOldZones(Times[1][0]);
        }

        private void AddOrMergeZone(double price, ZoneBias bias, DateTime timestamp, bool isSpecial)
        {
            double padding = ZonePaddingTicks * TickSize;
            ZoneLevel match = null;

            for (int i = 0; i < zones.Count; i++)
            {
                if (Math.Abs(zones[i].Center - price) <= padding)
                {
                    match = zones[i];
                    break;
                }
            }

            if (match == null)
            {
                ZoneLevel newZone = new ZoneLevel();
                newZone.Low = price - padding;
                newZone.High = price + padding;
                newZone.Bias = bias;
                newZone.Timestamp = timestamp;
                newZone.IsSpecial = isSpecial;
                newZone.Touches = isSpecial ? Math.Max(1, MinTouchesForZone) : 1;
                zones.Add(newZone);
                TrimZoneCount();
                return;
            }

            match.Low = Math.Min(match.Low, price - padding);
            match.High = Math.Max(match.High, price + padding);
            match.Timestamp = timestamp;
            match.IsSpecial = match.IsSpecial || isSpecial;

            if (isSpecial)
                match.Touches = Math.Max(match.Touches, MinTouchesForZone);
            else
                match.Touches += 1;

            if (match.Bias != bias)
                match.Bias = ZoneBias.Neutral;

            TrimZoneCount();
        }

        private void PruneOldZones(DateTime referenceTime)
        {
            DateTime cutoff = referenceTime.AddDays(-Math.Max(1, ZoneLookbackDays));
            for (int i = zones.Count - 1; i >= 0; i--)
            {
                if (!zones[i].IsSpecial && zones[i].Timestamp < cutoff)
                    zones.RemoveAt(i);
            }

            TrimZoneCount();
        }

        private void TrimZoneCount()
        {
            if (zones.Count <= MaxZonesToTrack)
                return;

            zones.Sort(delegate (ZoneLevel a, ZoneLevel b)
            {
                int specialCmp = b.IsSpecial.CompareTo(a.IsSpecial);
                if (specialCmp != 0)
                    return specialCmp;

                int touchCmp = b.Touches.CompareTo(a.Touches);
                if (touchCmp != 0)
                    return touchCmp;

                return b.Timestamp.CompareTo(a.Timestamp);
            });

            while (zones.Count > MaxZonesToTrack)
                zones.RemoveAt(zones.Count - 1);
        }

        private bool IsNearZone(bool forLong, out ZoneLevel nearestZone, out double nearestDistanceTicks)
        {
            nearestZone = null;
            nearestDistanceTicks = double.MaxValue;
            double price = Close[0];

            for (int i = 0; i < zones.Count; i++)
            {
                ZoneLevel zone = zones[i];
                if (!IsZoneActive(zone))
                    continue;

                if (forLong && zone.Bias == ZoneBias.Resistance)
                    continue;
                if (!forLong && zone.Bias == ZoneBias.Support)
                    continue;

                double distance = DistanceToZoneTicks(price, zone);
                if (distance < nearestDistanceTicks)
                {
                    nearestDistanceTicks = distance;
                    nearestZone = zone;
                }
            }

            return nearestZone != null && nearestDistanceTicks <= ZoneProximityTicks;
        }

        private bool IsZoneActive(ZoneLevel zone)
        {
            return zone.IsSpecial || zone.Touches >= MinTouchesForZone;
        }

        private double DistanceToZoneTicks(double price, ZoneLevel zone)
        {
            if (price >= zone.Low && price <= zone.High)
                return 0;

            if (price < zone.Low)
                return (zone.Low - price) / TickSize;

            return (price - zone.High) / TickSize;
        }

        private bool HasEnoughBarsForSetup()
        {
            int minimum = StructureLookbackBars + 5;
            minimum = Math.Max(minimum, ChopLookbackBars + 5);
            minimum = Math.Max(minimum, ATRPeriod + 5);
            return CurrentBar >= minimum;
        }

        private bool VolatilityFiltersPass()
        {
            if (UseATRFilter)
            {
                if (CurrentBar < ATRPeriod + 1)
                    return false;
                if (atr[0] < MinATR)
                    return false;
            }

            if (UseOpenRangeFilter)
            {
                double elapsed = MinutesFromCashOpen();
                if (elapsed < OpenRangeMinutes)
                    return false;

                double rangeTicks = (openRangeHigh - openRangeLow) / TickSize;
                if (rangeTicks < MinOpenRangeTicks)
                    return false;
            }

            if (UseChopRatioFilter)
            {
                if (CurrentBar < ChopLookbackBars + 1)
                    return false;

                double chopRatio = CalculateChopRatio(ChopLookbackBars);
                if (chopRatio > MaxChopRatio)
                    return false;
            }

            return true;
        }

        private double CalculateChopRatio(int lookback)
        {
            double sumRanges = 0;
            double highest = double.MinValue;
            double lowest = double.MaxValue;

            for (int i = 0; i < lookback; i++)
            {
                double hi = High[i];
                double lo = Low[i];
                sumRanges += Math.Max(TickSize, hi - lo);
                highest = Math.Max(highest, hi);
                lowest = Math.Min(lowest, lo);
            }

            double netRange = highest - lowest;
            if (netRange <= TickSize * 0.5)
                return double.MaxValue;

            return sumRanges / netRange;
        }

        private bool TrendContextLongOk()
        {
            int lookback = Math.Min(StructureLookbackBars, CurrentBar - 1);
            if (lookback < 5)
                return false;

            bool downContext = Close[0] < Close[lookback];
            if (!downContext)
                return false;

            if (UseOverextensionRule)
            {
                double extension = HighestHigh(lookback) - Low[0];
                double threshold = OverextensionATRMultiplier * atr[0];
                if (extension < threshold)
                    return false;
            }

            if (RequireTrendShift && !HasBullishTrendShift())
                return false;

            return true;
        }

        private bool TrendContextShortOk()
        {
            int lookback = Math.Min(StructureLookbackBars, CurrentBar - 1);
            if (lookback < 5)
                return false;

            bool upContext = Close[0] > Close[lookback];
            if (!upContext)
                return false;

            if (UseOverextensionRule)
            {
                double extension = High[0] - LowestLow(lookback);
                double threshold = OverextensionATRMultiplier * atr[0];
                if (extension < threshold)
                    return false;
            }

            if (RequireTrendShift && !HasBearishTrendShift())
                return false;

            return true;
        }

        private bool HasBullishTrendShift()
        {
            PivotPoint recent;
            PivotPoint previous;
            if (!TryGetRecentPivotLows(2, StructureLookbackBars, out recent, out previous))
                return Close[0] > Close[1] && Close[1] > Close[2];

            if (recent.Price <= previous.Price)
                return false;

            double neckline = HighestHighBetween(previous.BarsAgo, recent.BarsAgo);
            return Close[0] > neckline;
        }

        private bool HasBearishTrendShift()
        {
            PivotPoint recent;
            PivotPoint previous;
            if (!TryGetRecentPivotHighs(2, StructureLookbackBars, out recent, out previous))
                return Close[0] < Close[1] && Close[1] < Close[2];

            if (recent.Price >= previous.Price)
                return false;

            double neckline = LowestLowBetween(previous.BarsAgo, recent.BarsAgo);
            return Close[0] < neckline;
        }

        private bool TryBuildLongPlan(int timingBucket, out TradePlan plan)
        {
            plan = new TradePlan();
            PatternSignal signal;
            if (!TryGetBullishSignal(out signal))
                return false;

            if (!PassesSignalStrengthFilter(signal, true))
                return false;

            double entry = RoundToTick(signal.TriggerPrice + EntryOffsetTicks * TickSize);
            double stop = GetLongStopPrice(entry, signal.StopAnchorPrice);
            if (double.IsNaN(stop) || stop >= entry)
                return false;

            double stopTicks = (entry - stop) / TickSize;
            if (MaxStopTicks > 0 && stopTicks > MaxStopTicks)
                return false;

            int quantity = CalculateContracts(entry, stop);
            if (quantity < 1)
                return false;

            double target = GetLongTargetPrice(entry, stop);
            if (target <= entry + TickSize)
                return false;

            plan.IsLong = true;
            plan.Quantity = quantity;
            plan.TimingBucket = timingBucket;
            plan.EntryPrice = entry;
            plan.StopPrice = stop;
            plan.TargetPrice = target;
            return true;
        }

        private bool TryBuildShortPlan(int timingBucket, out TradePlan plan)
        {
            plan = new TradePlan();
            PatternSignal signal;
            if (!TryGetBearishSignal(out signal))
                return false;

            if (!PassesSignalStrengthFilter(signal, false))
                return false;

            double entry = RoundToTick(signal.TriggerPrice - EntryOffsetTicks * TickSize);
            double stop = GetShortStopPrice(entry, signal.StopAnchorPrice);
            if (double.IsNaN(stop) || stop <= entry)
                return false;

            double stopTicks = (stop - entry) / TickSize;
            if (MaxStopTicks > 0 && stopTicks > MaxStopTicks)
                return false;

            int quantity = CalculateContracts(entry, stop);
            if (quantity < 1)
                return false;

            double target = GetShortTargetPrice(entry, stop);
            if (target >= entry - TickSize)
                return false;

            plan.IsLong = false;
            plan.Quantity = quantity;
            plan.TimingBucket = timingBucket;
            plan.EntryPrice = entry;
            plan.StopPrice = stop;
            plan.TargetPrice = target;
            return true;
        }

        private bool TryGetBullishSignal(out PatternSignal signal)
        {
            if (EnableHigherLowBreak && TryHigherLowBreakLong(out signal))
                return true;
            if (EnableDoubleBottom && TryDoubleBottomLong(out signal))
                return true;
            if (EnableEngulfing && TryBullishEngulfing(out signal))
                return true;
            if (EnableThreeLineStrike && TryBullishThreeLineStrike(out signal))
                return true;
            if (TryBullishFlushFlip(out signal))
                return true;

            signal = new PatternSignal();
            return false;
        }

        private bool TryGetBearishSignal(out PatternSignal signal)
        {
            if (EnableHeadAndShoulders && TryLowerHighBreakdownShort(out signal))
                return true;
            if (EnableEngulfing && TryBearishEngulfing(out signal))
                return true;
            if (EnableThreeLineStrike && TryBearishThreeLineStrike(out signal))
                return true;
            if (TryBearishFlushFlip(out signal))
                return true;

            signal = new PatternSignal();
            return false;
        }

        private bool PassesSignalStrengthFilter(PatternSignal signal, bool forLong)
        {
            if (!RequireEngulfingOrStrongClose)
                return true;

            if (signal.IsEngulfing)
                return true;

            if (IsStrongBody(0))
                return true;

            if (forLong)
                return Close[0] > Open[0] && Close[0] >= High[1];

            return Close[0] < Open[0] && Close[0] <= Low[1];
        }

        private bool TryDoubleBottomLong(out PatternSignal signal)
        {
            signal = new PatternSignal();

            PivotPoint recent;
            PivotPoint previous;
            if (!TryGetRecentPivotLows(2, Math.Max(StructureLookbackBars, 30), out recent, out previous))
                return false;

            double tolerance = DoubleBottomToleranceTicks * TickSize;
            if (Math.Abs(recent.Price - previous.Price) > tolerance)
                return false;

            double neckline = HighestHighBetween(previous.BarsAgo, recent.BarsAgo);
            if (Close[0] <= neckline)
                return false;

            signal.TriggerPrice = Math.Max(High[0], neckline);
            signal.StopAnchorPrice = Math.Min(recent.Price, previous.Price);
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryHigherLowBreakLong(out PatternSignal signal)
        {
            signal = new PatternSignal();

            PivotPoint recent;
            PivotPoint previous;
            if (!TryGetRecentPivotLows(2, Math.Max(StructureLookbackBars, 30), out recent, out previous))
                return false;

            if (recent.Price <= previous.Price + TickSize * 0.25)
                return false;

            double minorHigh = HighestHighBetween(previous.BarsAgo, recent.BarsAgo);
            if (Close[0] <= minorHigh)
                return false;

            signal.TriggerPrice = Math.Max(High[0], minorHigh);
            signal.StopAnchorPrice = recent.Price;
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryLowerHighBreakdownShort(out PatternSignal signal)
        {
            signal = new PatternSignal();

            PivotPoint recent;
            PivotPoint previous;
            if (!TryGetRecentPivotHighs(2, Math.Max(StructureLookbackBars, 30), out recent, out previous))
                return false;

            if (recent.Price >= previous.Price - TickSize * 0.25)
                return false;

            double neckline = LowestLowBetween(previous.BarsAgo, recent.BarsAgo);
            if (Close[0] >= neckline)
                return false;

            signal.TriggerPrice = Math.Min(Low[0], neckline);
            signal.StopAnchorPrice = Math.Max(recent.Price, previous.Price);
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryBullishEngulfing(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 2)
                return false;

            bool previousBearish = Close[1] < Open[1];
            bool currentBullish = Close[0] > Open[0];
            bool engulf = Open[0] <= Close[1] && Close[0] >= Open[1];
            if (!previousBearish || !currentBullish || !engulf)
                return false;

            if (!IsStrongBody(0))
                return false;

            signal.TriggerPrice = High[0];
            signal.StopAnchorPrice = Math.Min(Low[0], Low[1]);
            signal.IsEngulfing = true;
            return true;
        }

        private bool TryBearishEngulfing(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 2)
                return false;

            bool previousBullish = Close[1] > Open[1];
            bool currentBearish = Close[0] < Open[0];
            bool engulf = Open[0] >= Close[1] && Close[0] <= Open[1];
            if (!previousBullish || !currentBearish || !engulf)
                return false;

            if (!IsStrongBody(0))
                return false;

            signal.TriggerPrice = Low[0];
            signal.StopAnchorPrice = Math.Max(High[0], High[1]);
            signal.IsEngulfing = true;
            return true;
        }

        private bool TryBullishThreeLineStrike(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 4)
                return false;

            bool threeDown = Close[3] < Open[3] && Close[2] < Open[2] && Close[1] < Open[1]
                && Close[2] < Close[3] && Close[1] < Close[2];
            bool strike = Close[0] > Open[0] && Open[0] <= Close[1] && Close[0] >= Open[3];
            if (!threeDown || !strike)
                return false;

            if (!IsStrongBody(0))
                return false;

            signal.TriggerPrice = High[0];
            signal.StopAnchorPrice = Math.Min(Math.Min(Low[0], Low[1]), Math.Min(Low[2], Low[3]));
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryBearishThreeLineStrike(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 4)
                return false;

            bool threeUp = Close[3] > Open[3] && Close[2] > Open[2] && Close[1] > Open[1]
                && Close[2] > Close[3] && Close[1] > Close[2];
            bool strike = Close[0] < Open[0] && Open[0] >= Close[1] && Close[0] <= Open[3];
            if (!threeUp || !strike)
                return false;

            if (!IsStrongBody(0))
                return false;

            signal.TriggerPrice = Low[0];
            signal.StopAnchorPrice = Math.Max(Math.Max(High[0], High[1]), Math.Max(High[2], High[3]));
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryBullishFlushFlip(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 3)
                return false;

            bool flush = Close[2] < Open[2] && IsStrongBody(2);
            bool flip = Close[1] > Open[1] && Close[0] > Open[0] && IsStrongBody(1) && IsStrongBody(0);
            if (!flush || !flip)
                return false;

            if (Close[0] <= High[2])
                return false;

            signal.TriggerPrice = High[0];
            signal.StopAnchorPrice = Math.Min(Math.Min(Low[2], Low[1]), Low[0]);
            signal.IsEngulfing = false;
            return true;
        }

        private bool TryBearishFlushFlip(out PatternSignal signal)
        {
            signal = new PatternSignal();
            if (CurrentBar < 3)
                return false;

            bool flush = Close[2] > Open[2] && IsStrongBody(2);
            bool flip = Close[1] < Open[1] && Close[0] < Open[0] && IsStrongBody(1) && IsStrongBody(0);
            if (!flush || !flip)
                return false;

            if (Close[0] >= Low[2])
                return false;

            signal.TriggerPrice = Low[0];
            signal.StopAnchorPrice = Math.Max(Math.Max(High[2], High[1]), High[0]);
            signal.IsEngulfing = false;
            return true;
        }

        private bool IsStrongBody(int barsAgo)
        {
            if (barsAgo < 0 || CurrentBar < barsAgo)
                return false;

            double range = High[barsAgo] - Low[barsAgo];
            if (range <= 0)
                return false;

            double body = Math.Abs(Close[barsAgo] - Open[barsAgo]);
            return body / range >= MinReversalCandleBodyRatio;
        }

        private double GetLongStopPrice(double entryPrice, double patternLow)
        {
            double stopPrice;
            if (StopMethod == StopMethodType.FixedTicks)
            {
                stopPrice = entryPrice - FixedStopTicks * TickSize;
            }
            else if (StopMethod == StopMethodType.SignalBar)
            {
                stopPrice = patternLow - StopBufferTicks * TickSize;
            }
            else
            {
                int lookback = Math.Max(6, Math.Min(30, StructureLookbackBars));
                double swingLow = LowestLow(lookback);
                stopPrice = Math.Min(patternLow, swingLow) - StopBufferTicks * TickSize;
            }

            return RoundToTick(stopPrice);
        }

        private double GetShortStopPrice(double entryPrice, double patternHigh)
        {
            double stopPrice;
            if (StopMethod == StopMethodType.FixedTicks)
            {
                stopPrice = entryPrice + FixedStopTicks * TickSize;
            }
            else if (StopMethod == StopMethodType.SignalBar)
            {
                stopPrice = patternHigh + StopBufferTicks * TickSize;
            }
            else
            {
                int lookback = Math.Max(6, Math.Min(30, StructureLookbackBars));
                double swingHigh = HighestHigh(lookback);
                stopPrice = Math.Max(patternHigh, swingHigh) + StopBufferTicks * TickSize;
            }

            return RoundToTick(stopPrice);
        }

        private double GetLongTargetPrice(double entryPrice, double stopPrice)
        {
            double risk = entryPrice - stopPrice;
            double rrTarget = entryPrice + (risk * TargetR);

            if (UseZoneTargets)
            {
                double zoneTarget = FindZoneTarget(true, entryPrice);
                if (!double.IsNaN(zoneTarget))
                    rrTarget = Math.Min(rrTarget, zoneTarget);
            }

            return RoundToTick(rrTarget);
        }

        private double GetShortTargetPrice(double entryPrice, double stopPrice)
        {
            double risk = stopPrice - entryPrice;
            double rrTarget = entryPrice - (risk * TargetR);

            if (UseZoneTargets)
            {
                double zoneTarget = FindZoneTarget(false, entryPrice);
                if (!double.IsNaN(zoneTarget))
                    rrTarget = Math.Max(rrTarget, zoneTarget);
            }

            return RoundToTick(rrTarget);
        }

        private double FindZoneTarget(bool forLong, double entryPrice)
        {
            double best = double.NaN;

            for (int i = 0; i < zones.Count; i++)
            {
                ZoneLevel zone = zones[i];
                if (!IsZoneActive(zone))
                    continue;

                if (forLong)
                {
                    if (zone.Bias == ZoneBias.Support)
                        continue;

                    double level = zone.Low;
                    if (level <= entryPrice + TickSize)
                        continue;

                    if (double.IsNaN(best) || level < best)
                        best = level;
                }
                else
                {
                    if (zone.Bias == ZoneBias.Resistance)
                        continue;

                    double level = zone.High;
                    if (level >= entryPrice - TickSize)
                        continue;

                    if (double.IsNaN(best) || level > best)
                        best = level;
                }
            }

            return best;
        }

        private int CalculateContracts(double entryPrice, double stopPrice)
        {
            int maxContracts = Math.Max(1, MaxContracts);
            int minContracts = Math.Max(1, MinContracts);

            if (!UseFixedDollarRisk)
            {
                int qty = DefaultQuantity > 0 ? DefaultQuantity : 1;
                qty = Math.Max(minContracts, qty);
                return Math.Min(maxContracts, qty);
            }

            if (RiskPerTradeDollars <= 0)
                return 0;

            double pointValue = Instrument.MasterInstrument.PointValue;
            if (pointValue <= 0)
                return 0;

            double riskPerContract = Math.Abs(entryPrice - stopPrice) * pointValue;
            if (riskPerContract <= 0)
                return 0;

            int contracts = (int)Math.Floor(RiskPerTradeDollars / riskPerContract);
            contracts = Math.Min(maxContracts, contracts);
            if (contracts < minContracts)
                return 0;

            return contracts;
        }

        private void SubmitTradePlan(TradePlan plan)
        {
            pendingTimingBucket = plan.TimingBucket;
            workingEntryBar = CurrentBar;

            activeStopPrice = plan.StopPrice;
            initialRiskPoints = Math.Abs(plan.EntryPrice - plan.StopPrice);
            breakEvenMoved = false;
            scaleOutDone = false;
            positionEntryTime = Core.Globals.MinDate;

            if (plan.IsLong)
            {
                SetStopLoss(LongSignalName, CalculationMode.Price, plan.StopPrice, false);
                SetProfitTarget(LongSignalName, CalculationMode.Price, plan.TargetPrice);
                EnterLongStopMarket(plan.Quantity, plan.EntryPrice, LongSignalName);
            }
            else
            {
                SetStopLoss(ShortSignalName, CalculationMode.Price, plan.StopPrice, false);
                SetProfitTarget(ShortSignalName, CalculationMode.Price, plan.TargetPrice);
                EnterShortStopMarket(plan.Quantity, plan.EntryPrice, ShortSignalName);
            }
        }

        private void ManageWorkingEntryOrder()
        {
            if (workingEntryOrder == null)
                return;

            if (!IsWithinTradeWindow())
            {
                CancelWorkingEntry();
                return;
            }

            if (EntryExpiryBars > 0 && workingEntryBar >= 0)
            {
                if (CurrentBar - workingEntryBar >= EntryExpiryBars)
                    CancelWorkingEntry();
            }
        }

        private void CancelWorkingEntry()
        {
            if (workingEntryOrder == null)
                return;

            if (workingEntryOrder.OrderState == OrderState.Working
                || workingEntryOrder.OrderState == OrderState.Accepted
                || workingEntryOrder.OrderState == OrderState.PartFilled)
            {
                CancelOrder(workingEntryOrder);
            }
        }

        private void HandlePositionStateTransition()
        {
            if (Position.MarketPosition == priorMarketPosition)
                return;

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                activeStopPrice = double.NaN;
                initialRiskPoints = double.NaN;
                breakEvenMoved = false;
                scaleOutDone = false;
                positionEntryTime = Core.Globals.MinDate;
            }

            priorMarketPosition = Position.MarketPosition;
        }

        private void ManageOpenPosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            if (double.IsNaN(initialRiskPoints) || initialRiskPoints <= 0)
                initialRiskPoints = Math.Max(TickSize, Math.Abs(Position.AveragePrice - activeStopPrice));

            if (MaxHoldMinutes > 0 && positionEntryTime > Core.Globals.MinDate)
            {
                if (Time[0] >= positionEntryTime.AddMinutes(MaxHoldMinutes))
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("TIME_EXIT_LONG", LongSignalName);
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("TIME_EXIT_SHORT", ShortSignalName);
                    return;
                }
            }

            if (Position.MarketPosition == MarketPosition.Long)
                ManageLongPosition();
            else if (Position.MarketPosition == MarketPosition.Short)
                ManageShortPosition();
        }

        private void ManageLongPosition()
        {
            if (initialRiskPoints <= 0)
                return;

            double currentR = (Close[0] - Position.AveragePrice) / initialRiskPoints;

            if (MoveToBreakEvenEnabled && !breakEvenMoved && currentR >= BreakEvenTriggerR)
            {
                double breakEvenStop = Position.AveragePrice + BreakEvenPlusTicks * TickSize;
                UpdateLongStopIfTighter(breakEvenStop);
                breakEvenMoved = true;
            }

            if (ScaleOutEnabled && !scaleOutDone && currentR >= ScaleOutAtR && Position.Quantity > 1)
            {
                int quantity = (int)Math.Floor(Position.Quantity * (ScaleOutPercent / 100.0));
                if (quantity >= Position.Quantity)
                    quantity = Position.Quantity - 1;
                if (quantity > 0)
                {
                    ExitLong(quantity, "SCALE_OUT_LONG", LongSignalName);
                    scaleOutDone = true;
                }
            }

            if (TrailEnabled)
            {
                double trailStop = TrailMethod == TrailMethodType.FixedTicks
                    ? Close[0] - TrailTicks * TickSize
                    : LowestLow(Math.Max(5, Math.Min(20, StructureLookbackBars / 2))) - TrailTicks * TickSize;

                UpdateLongStopIfTighter(trailStop);
            }
        }

        private void ManageShortPosition()
        {
            if (initialRiskPoints <= 0)
                return;

            double currentR = (Position.AveragePrice - Close[0]) / initialRiskPoints;

            if (MoveToBreakEvenEnabled && !breakEvenMoved && currentR >= BreakEvenTriggerR)
            {
                double breakEvenStop = Position.AveragePrice - BreakEvenPlusTicks * TickSize;
                UpdateShortStopIfTighter(breakEvenStop);
                breakEvenMoved = true;
            }

            if (ScaleOutEnabled && !scaleOutDone && currentR >= ScaleOutAtR && Position.Quantity > 1)
            {
                int quantity = (int)Math.Floor(Position.Quantity * (ScaleOutPercent / 100.0));
                if (quantity >= Position.Quantity)
                    quantity = Position.Quantity - 1;
                if (quantity > 0)
                {
                    ExitShort(quantity, "SCALE_OUT_SHORT", ShortSignalName);
                    scaleOutDone = true;
                }
            }

            if (TrailEnabled)
            {
                double trailStop = TrailMethod == TrailMethodType.FixedTicks
                    ? Close[0] + TrailTicks * TickSize
                    : HighestHigh(Math.Max(5, Math.Min(20, StructureLookbackBars / 2))) + TrailTicks * TickSize;

                UpdateShortStopIfTighter(trailStop);
            }
        }

        private void UpdateLongStopIfTighter(double newStopPrice)
        {
            newStopPrice = Math.Min(newStopPrice, Close[0] - TickSize);
            newStopPrice = RoundToTick(newStopPrice);

            if (double.IsNaN(activeStopPrice) || newStopPrice > activeStopPrice + TickSize * 0.25)
            {
                activeStopPrice = newStopPrice;
                SetStopLoss(LongSignalName, CalculationMode.Price, activeStopPrice, false);
            }
        }

        private void UpdateShortStopIfTighter(double newStopPrice)
        {
            newStopPrice = Math.Max(newStopPrice, Close[0] + TickSize);
            newStopPrice = RoundToTick(newStopPrice);

            if (double.IsNaN(activeStopPrice) || newStopPrice < activeStopPrice - TickSize * 0.25)
            {
                activeStopPrice = newStopPrice;
                SetStopLoss(ShortSignalName, CalculationMode.Price, activeStopPrice, false);
            }
        }

        private bool IsWithinTradeWindow()
        {
            if (!cashOpenTriggered)
                return false;

            double elapsed = MinutesFromCashOpen();
            return elapsed >= 0 && elapsed <= TradeWindowMinutesAfterOpen;
        }

        private double MinutesFromCashOpen()
        {
            if (!cashOpenTriggered)
                return double.MinValue;
            return (Time[0] - cashOpenTime).TotalMinutes;
        }

        private int GetActiveTimingBucket()
        {
            double elapsed = MinutesFromCashOpen();
            int bucket = 0;
            double bestDelta = double.MaxValue;

            if (UseTiming15m)
            {
                double delta = Math.Abs(elapsed - 15.0);
                if (delta <= TimingWindowToleranceMinutes && delta < bestDelta)
                {
                    bestDelta = delta;
                    bucket = 15;
                }
            }

            if (UseTiming30m)
            {
                double delta = Math.Abs(elapsed - 30.0);
                if (delta <= TimingWindowToleranceMinutes && delta < bestDelta)
                {
                    bestDelta = delta;
                    bucket = 30;
                }
            }

            if (UseTiming90m)
            {
                double delta = Math.Abs(elapsed - 90.0);
                if (delta <= TimingWindowToleranceMinutes && delta < bestDelta)
                {
                    bestDelta = delta;
                    bucket = 90;
                }
            }

            return bucket;
        }

        private bool IsTimingBucketUsed(int bucket)
        {
            if (bucket == 15)
                return usedTiming15;
            if (bucket == 30)
                return usedTiming30;
            if (bucket == 90)
                return usedTiming90;
            return false;
        }

        private void MarkTimingBucketUsed(int bucket)
        {
            if (bucket == 15)
                usedTiming15 = true;
            else if (bucket == 30)
                usedTiming30 = true;
            else if (bucket == 90)
                usedTiming90 = true;
        }

        private bool TryGetRecentPivotLows(int strength, int lookback, out PivotPoint recent, out PivotPoint previous)
        {
            recent = new PivotPoint();
            previous = new PivotPoint();

            int maxBarsAgo = Math.Min(lookback, CurrentBar - strength - 1);
            if (maxBarsAgo <= strength)
                return false;

            int found = 0;
            for (int barsAgo = strength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                if (!IsPivotLowPrimary(barsAgo, strength))
                    continue;

                if (found == 0)
                    recent = new PivotPoint { BarsAgo = barsAgo, Price = Low[barsAgo] };
                else if (found == 1)
                    previous = new PivotPoint { BarsAgo = barsAgo, Price = Low[barsAgo] };

                found++;
                if (found >= 2)
                    return true;
            }

            return false;
        }

        private bool TryGetRecentPivotHighs(int strength, int lookback, out PivotPoint recent, out PivotPoint previous)
        {
            recent = new PivotPoint();
            previous = new PivotPoint();

            int maxBarsAgo = Math.Min(lookback, CurrentBar - strength - 1);
            if (maxBarsAgo <= strength)
                return false;

            int found = 0;
            for (int barsAgo = strength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                if (!IsPivotHighPrimary(barsAgo, strength))
                    continue;

                if (found == 0)
                    recent = new PivotPoint { BarsAgo = barsAgo, Price = High[barsAgo] };
                else if (found == 1)
                    previous = new PivotPoint { BarsAgo = barsAgo, Price = High[barsAgo] };

                found++;
                if (found >= 2)
                    return true;
            }

            return false;
        }

        private bool IsPivotHighPrimary(int barsAgo, int strength)
        {
            if (barsAgo - strength < 0)
                return false;

            if (barsAgo + strength > CurrentBar)
                return false;

            double candidate = High[barsAgo];
            for (int i = 1; i <= strength; i++)
            {
                if (High[barsAgo + i] >= candidate)
                    return false;
                if (High[barsAgo - i] > candidate)
                    return false;
            }

            return true;
        }

        private bool IsPivotLowPrimary(int barsAgo, int strength)
        {
            if (barsAgo - strength < 0)
                return false;

            if (barsAgo + strength > CurrentBar)
                return false;

            double candidate = Low[barsAgo];
            for (int i = 1; i <= strength; i++)
            {
                if (Low[barsAgo + i] <= candidate)
                    return false;
                if (Low[barsAgo - i] < candidate)
                    return false;
            }

            return true;
        }

        private bool IsPivotHighSecondary(int barsAgo, int strength)
        {
            if (barsAgo - strength < 0)
                return false;

            if (barsAgo + strength > CurrentBars[1])
                return false;

            double candidate = Highs[1][barsAgo];
            for (int i = 1; i <= strength; i++)
            {
                if (Highs[1][barsAgo + i] >= candidate)
                    return false;
                if (Highs[1][barsAgo - i] > candidate)
                    return false;
            }

            return true;
        }

        private bool IsPivotLowSecondary(int barsAgo, int strength)
        {
            if (barsAgo - strength < 0)
                return false;

            if (barsAgo + strength > CurrentBars[1])
                return false;

            double candidate = Lows[1][barsAgo];
            for (int i = 1; i <= strength; i++)
            {
                if (Lows[1][barsAgo + i] <= candidate)
                    return false;
                if (Lows[1][barsAgo - i] < candidate)
                    return false;
            }

            return true;
        }

        private double HighestHigh(int lookback)
        {
            int bars = Math.Min(lookback, CurrentBar);
            double result = double.MinValue;
            for (int i = 0; i <= bars; i++)
                result = Math.Max(result, High[i]);
            return result;
        }

        private double LowestLow(int lookback)
        {
            int bars = Math.Min(lookback, CurrentBar);
            double result = double.MaxValue;
            for (int i = 0; i <= bars; i++)
                result = Math.Min(result, Low[i]);
            return result;
        }

        private double HighestHighBetween(int barsAgoA, int barsAgoB)
        {
            int start = Math.Min(barsAgoA, barsAgoB);
            int end = Math.Max(barsAgoA, barsAgoB);
            double result = double.MinValue;
            for (int i = start; i <= end; i++)
                result = Math.Max(result, High[i]);
            return result;
        }

        private double LowestLowBetween(int barsAgoA, int barsAgoB)
        {
            int start = Math.Min(barsAgoA, barsAgoB);
            int end = Math.Max(barsAgoA, barsAgoB);
            double result = double.MaxValue;
            for (int i = start; i <= end; i++)
                result = Math.Min(result, Low[i]);
            return result;
        }

        private double RoundToTick(double price)
        {
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        #region Parameters - Timing
        [NinjaScriptProperty]
        [Display(Name = "UseTiming15m", GroupName = "Timing", Order = 0)]
        public bool UseTiming15m { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseTiming30m", GroupName = "Timing", Order = 1)]
        public bool UseTiming30m { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseTiming90m", GroupName = "Timing", Order = 2)]
        public bool UseTiming90m { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "TimingWindowToleranceMinutes", GroupName = "Timing", Order = 3)]
        public int TimingWindowToleranceMinutes { get; set; }
        #endregion

        #region Parameters - Session
        [NinjaScriptProperty]
        [Range(0, 1440)]
        [Display(Name = "SessionOpenOffsetMinutes", GroupName = "Session", Order = 0)]
        public int SessionOpenOffsetMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "TradeWindowMinutesAfterOpen", GroupName = "Session", Order = 1)]
        public int TradeWindowMinutesAfterOpen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerSession", GroupName = "Session", Order = 2)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OneTradePerTimingWindow", GroupName = "Session", Order = 3)]
        public bool OneTradePerTimingWindow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "EntryExpiryBars", GroupName = "Session", Order = 4)]
        public int EntryExpiryBars { get; set; }
        #endregion

        #region Parameters - Zones
        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "ZoneTimeframeMinutes", GroupName = "Zones", Order = 0)]
        public int ZoneTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "ZoneLookbackDays", GroupName = "Zones", Order = 1)]
        public int ZoneLookbackDays { get; set; }

        [NinjaScriptProperty]
        [Range(2, 20)]
        [Display(Name = "PivotStrength", GroupName = "Zones", Order = 2)]
        public int PivotStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MinTouchesForZone", GroupName = "Zones", Order = 3)]
        public int MinTouchesForZone { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ZonePaddingTicks", GroupName = "Zones", Order = 4)]
        public int ZonePaddingTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ZoneProximityTicks", GroupName = "Zones", Order = 5)]
        public int ZoneProximityTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "MaxZonesToTrack", GroupName = "Zones", Order = 6)]
        public int MaxZonesToTrack { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UsePriorDayHLC", GroupName = "Zones", Order = 7)]
        public bool UsePriorDayHLC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseTodayOpenLevel", GroupName = "Zones", Order = 8)]
        public bool UseTodayOpenLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseOvernightHL", GroupName = "Zones", Order = 9)]
        public bool UseOvernightHL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseGaps", GroupName = "Zones", Order = 10)]
        public bool UseGaps { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MinGapTicks", GroupName = "Zones", Order = 11)]
        public int MinGapTicks { get; set; }
        #endregion

        #region Parameters - Volatility/Chop
        [NinjaScriptProperty]
        [Display(Name = "UseATRFilter", GroupName = "Volatility", Order = 0)]
        public bool UseATRFilter { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "ATRPeriod", GroupName = "Volatility", Order = 1)]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "MinATR", GroupName = "Volatility", Order = 2)]
        public double MinATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseOpenRangeFilter", GroupName = "Volatility", Order = 3)]
        public bool UseOpenRangeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "OpenRangeMinutes", GroupName = "Volatility", Order = 4)]
        public int OpenRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MinOpenRangeTicks", GroupName = "Volatility", Order = 5)]
        public int MinOpenRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseChopRatioFilter", GroupName = "Volatility", Order = 6)]
        public bool UseChopRatioFilter { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "ChopLookbackBars", GroupName = "Volatility", Order = 7)]
        public int ChopLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "MaxChopRatio", GroupName = "Volatility", Order = 8)]
        public double MaxChopRatio { get; set; }
        #endregion

        #region Parameters - Trend
        [NinjaScriptProperty]
        [Display(Name = "RequireTrendShift", GroupName = "Trend", Order = 0)]
        public bool RequireTrendShift { get; set; }

        [NinjaScriptProperty]
        [Range(10, 200)]
        [Display(Name = "StructureLookbackBars", GroupName = "Trend", Order = 1)]
        public int StructureLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseOverextensionRule", GroupName = "Trend", Order = 2)]
        public bool UseOverextensionRule { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "OverextensionATRMultiplier", GroupName = "Trend", Order = 3)]
        public double OverextensionATRMultiplier { get; set; }
        #endregion

        #region Parameters - Patterns
        [NinjaScriptProperty]
        [Display(Name = "EnableDoubleBottom", GroupName = "Patterns", Order = 0)]
        public bool EnableDoubleBottom { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "DoubleBottomToleranceTicks", GroupName = "Patterns", Order = 1)]
        public int DoubleBottomToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableHigherLowBreak", GroupName = "Patterns", Order = 2)]
        public bool EnableHigherLowBreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableHeadAndShoulders", GroupName = "Patterns", Order = 3)]
        public bool EnableHeadAndShoulders { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableEngulfing", GroupName = "Patterns", Order = 4)]
        public bool EnableEngulfing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableThreeLineStrike", GroupName = "Patterns", Order = 5)]
        public bool EnableThreeLineStrike { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireEngulfingOrStrongClose", GroupName = "Patterns", Order = 6)]
        public bool RequireEngulfingOrStrongClose { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "MinReversalCandleBodyRatio", GroupName = "Patterns", Order = 7)]
        public double MinReversalCandleBodyRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "EntryOffsetTicks", GroupName = "Patterns", Order = 8)]
        public int EntryOffsetTicks { get; set; }
        #endregion

        #region Parameters - Stops/Targets
        [NinjaScriptProperty]
        [Display(Name = "StopMethod", GroupName = "StopsTargets", Order = 0)]
        public StopMethodType StopMethod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "StopBufferTicks", GroupName = "StopsTargets", Order = 1)]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FixedStopTicks", GroupName = "StopsTargets", Order = 2)]
        public int FixedStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MaxStopTicks", GroupName = "StopsTargets", Order = 3)]
        public int MaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "TargetR", GroupName = "StopsTargets", Order = 4)]
        public double TargetR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseZoneTargets", GroupName = "StopsTargets", Order = 5)]
        public bool UseZoneTargets { get; set; }
        #endregion

        #region Parameters - Management
        [NinjaScriptProperty]
        [Display(Name = "MoveToBreakEvenEnabled", GroupName = "Management", Order = 0)]
        public bool MoveToBreakEvenEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "BreakEvenTriggerR", GroupName = "Management", Order = 1)]
        public double BreakEvenTriggerR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "BreakEvenPlusTicks", GroupName = "Management", Order = 2)]
        public int BreakEvenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ScaleOutEnabled", GroupName = "Management", Order = 3)]
        public bool ScaleOutEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(1, 99)]
        [Display(Name = "ScaleOutPercent", GroupName = "Management", Order = 4)]
        public int ScaleOutPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "ScaleOutAtR", GroupName = "Management", Order = 5)]
        public double ScaleOutAtR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TrailEnabled", GroupName = "Management", Order = 6)]
        public bool TrailEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TrailMethod", GroupName = "Management", Order = 7)]
        public TrailMethodType TrailMethod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrailTicks", GroupName = "Management", Order = 8)]
        public int TrailTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "MaxHoldMinutes", GroupName = "Management", Order = 9)]
        public int MaxHoldMinutes { get; set; }
        #endregion

        #region Parameters - Risk
        [NinjaScriptProperty]
        [Display(Name = "UseFixedDollarRisk", GroupName = "Risk", Order = 0)]
        public bool UseFixedDollarRisk { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "RiskPerTradeDollars", GroupName = "Risk", Order = 1)]
        public double RiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MinContracts", GroupName = "Risk", Order = 2)]
        public int MinContracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxContracts", GroupName = "Risk", Order = 3)]
        public int MaxContracts { get; set; }
        #endregion
    }
}
