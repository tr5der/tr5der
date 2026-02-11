using System;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum EntryTriggerMode
    {
        TouchImmediate,
        TouchPlusRejection
    }

    public enum LevelSelectionMode
    {
        PremarketHighLow,
        ORHighLow
    }

    public enum StopSelectionMode
    {
        OppositeORB,
        BeyondRetestCandle,
        FixedTicks
    }

    public enum EmergencyStopSelectionMode
    {
        None,
        TrendSettingCandleExtreme
    }

    public enum RunnerSelectionMode
    {
        None,
        PremarketOppositeLevel,
        FixedR,
        TrailOrNextLevel
    }

    internal enum BreakoutDirection
    {
        None,
        Long,
        Short
    }

    public class PremarketHL_ORB_BreakRetestRejectionScalper : Strategy
    {
        private const string TimeCategory = "1. Instrument / Session";
        private const string OrbCategory = "2. ORB";
        private const string LevelsCategory = "3. Levels / Zones";
        private const string SetupCategory = "4. Setup Rules";
        private const string RiskCategory = "5. Stops / Targets";
        private const string FilterCategory = "6. Filters";
        private const string PositionCategory = "7. Risk / Limits";
        private const string DebugCategory = "8. Diagnostics";

        private bool useSecondaryConfirmSeries;
        private int confirmBarsInProgress;
        private TimeZoneInfo configuredTimeZone;
        private string resolvedTimeZoneId;

        private DateTime activeDate;

        private double premarketHigh;
        private double premarketLow;
        private double orHigh;
        private double orLow;

        private bool hasPremarketLevels;
        private bool hasORLevels;
        private bool orbLocked;

        private BreakoutDirection breakoutDirection;
        private DateTime breakoutTime;
        private double breakoutLevel;
        private bool breakoutConsumed;
        private double breakoutCandleHigh;
        private double breakoutCandleLow;

        private double highestSinceBreakout;
        private double lowestSinceBreakout;

        private bool waitingForRejectionClose;
        private int rejectionTouchBar;
        private double rejectionTouchLevel;

        private int tradesTakenToday;
        private int tradeCounter;

        private string activeEntrySignal;
        private bool entryOrderSubmitted;
        private bool entryFilled;
        private BreakoutDirection activeTradeDirection;
        private int activeEntryQuantity;
        private double activeEntryPrice;
        private double activeStopPrice;
        private double plannedStopPrice;
        private double initialRiskPrice;
        private bool breakEvenMoved;
        private bool targetsSubmitted;
        private int activeRunnerQuantity;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PremarketHL_ORB_BreakRetestRejectionScalper";
                Description = "Premarket H/L + ORB breakout, retest, rejection scalper for NQ/MNQ.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                TraceOrders = false;

                AddPlot(Brushes.LimeGreen, "PremarketHighPlot");
                AddPlot(Brushes.LimeGreen, "PremarketLowPlot");
                AddPlot(Brushes.DodgerBlue, "ORHighPlot");
                AddPlot(Brushes.DodgerBlue, "ORLowPlot");

                TimeZoneId = "America/Chicago";
                RTHOpenTime = 83000;
                PremarketStart = 30000;
                PremarketEnd = 82959;
                TradeWindowStart = 84500;
                TradeWindowEnd = 103000;

                ORBMinutes = 15;
                ConfirmTimeframeMinutes = 5;
                ConfirmOnClose = true;
                MinBreakoutCloseTicks = 2;

                LevelZoneTicks = 8;
                TouchOffsetTicks = 1;
                EntryOffsetTicks = 0;

                RequireRetest = true;
                MaxMinutesAfterBreakoutToEnter = 60;
                EntryMode = EntryTriggerMode.TouchImmediate;
                RequirePremarketConfluence = true;
                PrimaryLevel = LevelSelectionMode.PremarketHighLow;
                SecondaryLevel = LevelSelectionMode.ORHighLow;
                MaxDistanceFromLevelBeforeInvalid = 0;
                RejectionBodyRatioMin = 0.5;

                StopMode = StopSelectionMode.OppositeORB;
                EmergencyStopMode = EmergencyStopSelectionMode.None;
                StopBufferTicks = 4;
                FixedStopTicks = 40;
                MaxStopTicks = 80;
                Target1_R = 2.0;
                ScaleOutEnabled = true;
                ScaleOutPercent = 50;
                MoveStopToBreakEvenEnabled = true;
                BreakEvenAtR = 2.0;
                BreakEvenPlusTicks = 0;
                RunnerMode = RunnerSelectionMode.TrailOrNextLevel;
                RunnerTargetR = 4.0;
                TrailLookbackBars = 5;

                MinORBTicks = 12;
                MaxORBTicks = 120;
                RequireVolumeConfirmation = true;
                VolumeLookback = 20;
                MinVolumeMultiplier = 1.2;
                RequireAggressiveBreakoutCandle = true;
                BreakoutBodyRatioMin = 0.6;
                UseChopFilter = false;
                MaxChopBodyRatio = 0.25;

                MaxTradesPerDay = 1;
                UseFixedDollarRisk = true;
                RiskPerTradeDollars = 100;
                MinContracts = 1;
                MaxContracts = 10;

                EnableDebugLogging = true;
            }
            else if (State == State.Configure)
            {
                useSecondaryConfirmSeries = !(BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value == ConfirmTimeframeMinutes);
                if (useSecondaryConfirmSeries)
                {
                    AddDataSeries(BarsPeriodType.Minute, ConfirmTimeframeMinutes);
                }
            }
            else if (State == State.DataLoaded)
            {
                confirmBarsInProgress = useSecondaryConfirmSeries ? 1 : 0;
                ResolveConfiguredTimeZone();
                ResetDailyState(Core.Globals.MinDate);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 && BarsInProgress != confirmBarsInProgress)
            {
                return;
            }

            if (CurrentBars[0] < 10)
            {
                return;
            }

            if (BarsInProgress == 0)
            {
                DateTime barTime = ToConfiguredTime(Times[0][0], 0);
                EnsureDailyReset(barTime.Date);

                UpdatePremarketLevels(barTime);
                UpdateOrbLevels(barTime);
                UpdateLevelPlots();

                if (confirmBarsInProgress == 0)
                {
                    EvaluateBreakoutConfirmation(0);
                }

                InvalidateExpiredBreakout(barTime);

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    EvaluateEntry(barTime);
                }
                else
                {
                    ManageOpenPosition();
                }
            }
            else if (BarsInProgress == confirmBarsInProgress)
            {
                EvaluateBreakoutConfirmation(confirmBarsInProgress);
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (!EnableDebugLogging || order == null)
            {
                return;
            }

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} | ORDER | {1} | State={2} Qty={3} Filled={4} Avg={5:0.00} Err={6} NativeErr={7}",
                ToConfiguredTime(time, 0),
                order.Name,
                orderState,
                quantity,
                filled,
                averageFillPrice,
                error,
                nativeError));

            if (!string.IsNullOrEmpty(activeEntrySignal) && order.Name == activeEntrySignal &&
                (orderState == OrderState.Rejected || orderState == OrderState.Cancelled) &&
                !entryFilled)
            {
                entryOrderSubmitted = false;
                breakoutConsumed = false;
                if (tradesTakenToday > 0)
                {
                    tradesTakenToday--;
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
            {
                return;
            }

            if (EnableDebugLogging)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss} | FILL | {1} | Qty={2} Price={3:0.00} Pos={4}",
                    ToConfiguredTime(time, 0),
                    execution.Order.Name,
                    quantity,
                    price,
                    marketPosition));
            }

            if (!string.IsNullOrEmpty(activeEntrySignal) && execution.Order.Name == activeEntrySignal && execution.Order.OrderState == OrderState.Filled)
            {
                entryFilled = true;
                entryOrderSubmitted = false;
                activeEntryPrice = Position.AveragePrice;
                initialRiskPrice = Math.Abs(activeEntryPrice - plannedStopPrice);
                activeStopPrice = plannedStopPrice;
                breakEvenMoved = false;

                SubmitTargetsIfNeeded();
            }

            if (Position.MarketPosition == MarketPosition.Flat && marketPosition == MarketPosition.Flat)
            {
                ClearTradeState();
            }
        }

        private void EnsureDailyReset(DateTime currentDate)
        {
            if (activeDate == Core.Globals.MinDate || currentDate != activeDate)
            {
                ResetDailyState(currentDate);
            }
        }

        private void ResetDailyState(DateTime newDate)
        {
            activeDate = newDate;

            premarketHigh = double.NaN;
            premarketLow = double.NaN;
            orHigh = double.NaN;
            orLow = double.NaN;

            hasPremarketLevels = false;
            hasORLevels = false;
            orbLocked = false;

            breakoutDirection = BreakoutDirection.None;
            breakoutTime = Core.Globals.MinDate;
            breakoutLevel = double.NaN;
            breakoutConsumed = false;
            breakoutCandleHigh = double.NaN;
            breakoutCandleLow = double.NaN;
            highestSinceBreakout = double.NaN;
            lowestSinceBreakout = double.NaN;

            waitingForRejectionClose = false;
            rejectionTouchBar = -1;
            rejectionTouchLevel = double.NaN;

            tradesTakenToday = 0;
            tradeCounter = 0;

            ClearTradeState();
        }

        private void ClearTradeState()
        {
            activeEntrySignal = string.Empty;
            entryOrderSubmitted = false;
            entryFilled = false;
            activeTradeDirection = BreakoutDirection.None;
            activeEntryQuantity = 0;
            activeEntryPrice = 0;
            activeStopPrice = 0;
            plannedStopPrice = 0;
            initialRiskPrice = 0;
            breakEvenMoved = false;
            targetsSubmitted = false;
            activeRunnerQuantity = 0;

            waitingForRejectionClose = false;
            rejectionTouchBar = -1;
            rejectionTouchLevel = double.NaN;

            breakoutDirection = BreakoutDirection.None;
            breakoutTime = Core.Globals.MinDate;
            breakoutLevel = double.NaN;
            breakoutConsumed = false;
            breakoutCandleHigh = double.NaN;
            breakoutCandleLow = double.NaN;
            highestSinceBreakout = double.NaN;
            lowestSinceBreakout = double.NaN;
        }

        private void UpdatePremarketLevels(DateTime barTime)
        {
            int hhmmss = ToHhmmss(barTime);
            if (hhmmss < PremarketStart || hhmmss > PremarketEnd)
            {
                return;
            }

            if (!hasPremarketLevels)
            {
                premarketHigh = Highs[0][0];
                premarketLow = Lows[0][0];
                hasPremarketLevels = true;
            }
            else
            {
                premarketHigh = Math.Max(premarketHigh, Highs[0][0]);
                premarketLow = Math.Min(premarketLow, Lows[0][0]);
            }
        }

        private void UpdateOrbLevels(DateTime barTime)
        {
            int hhmmss = ToHhmmss(barTime);
            int orbEnd = AddMinutesToTime(RTHOpenTime, ORBMinutes, -1);

            if (hhmmss >= RTHOpenTime && hhmmss <= orbEnd)
            {
                if (!hasORLevels)
                {
                    orHigh = Highs[0][0];
                    orLow = Lows[0][0];
                    hasORLevels = true;
                }
                else
                {
                    orHigh = Math.Max(orHigh, Highs[0][0]);
                    orLow = Math.Min(orLow, Lows[0][0]);
                }
            }

            if (hasORLevels && hhmmss > orbEnd)
            {
                orbLocked = true;
            }
        }

        private void UpdateLevelPlots()
        {
            Values[0][0] = hasPremarketLevels ? premarketHigh : double.NaN;
            Values[1][0] = hasPremarketLevels ? premarketLow : double.NaN;
            Values[2][0] = hasORLevels ? orHigh : double.NaN;
            Values[3][0] = hasORLevels ? orLow : double.NaN;
        }

        private void EvaluateBreakoutConfirmation(int bip)
        {
            if (tradesTakenToday >= MaxTradesPerDay || !orbLocked || !hasORLevels || breakoutDirection != BreakoutDirection.None || breakoutConsumed)
            {
                return;
            }

            int minBarsNeeded = ConfirmOnClose ? 3 : 2;
            if (RequireVolumeConfirmation)
            {
                minBarsNeeded = Math.Max(minBarsNeeded, VolumeLookback + 5);
            }

            if (CurrentBars[bip] < minBarsNeeded)
            {
                return;
            }

            int barIndex = 0;
            if (ConfirmOnClose)
            {
                if (!IsFirstTickOfBar)
                {
                    return;
                }

                barIndex = 1;
                if (CurrentBars[bip] < 2)
                {
                    return;
                }
            }

            DateTime signalTime = ToConfiguredTime(Times[bip][barIndex], bip);
            int hhmmss = ToHhmmss(signalTime);

            if (hhmmss < TradeWindowStart || hhmmss > TradeWindowEnd)
            {
                return;
            }

            if (!PassOrbRangeFilter())
            {
                return;
            }

            double close = Closes[bip][barIndex];
            double open = Opens[bip][barIndex];
            double high = Highs[bip][barIndex];
            double low = Lows[bip][barIndex];
            double range = Math.Max(TickSize, high - low);
            double bodyRatio = Math.Abs(close - open) / range;

            if (RequireAggressiveBreakoutCandle && bodyRatio < BreakoutBodyRatioMin)
            {
                return;
            }

            if (UseChopFilter && bodyRatio <= MaxChopBodyRatio)
            {
                return;
            }

            if (!PassVolumeFilter(bip, barIndex))
            {
                return;
            }

            double breakoutBuffer = MinBreakoutCloseTicks * TickSize;
            bool longBreakout = close >= (orHigh + breakoutBuffer);
            bool shortBreakout = close <= (orLow - breakoutBuffer);

            if (!longBreakout && !shortBreakout)
            {
                return;
            }

            BreakoutDirection direction = longBreakout ? BreakoutDirection.Long : BreakoutDirection.Short;
            if (!PassPremarketConfluence(direction, close))
            {
                return;
            }

            breakoutDirection = direction;
            breakoutLevel = direction == BreakoutDirection.Long ? orHigh : orLow;
            breakoutTime = signalTime;
            breakoutConsumed = false;
            breakoutCandleHigh = high;
            breakoutCandleLow = low;
            highestSinceBreakout = Highs[0][0];
            lowestSinceBreakout = Lows[0][0];

            if (EnableDebugLogging)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss} | BREAKOUT CONFIRMED | Dir={1} ORH={2:0.00} ORL={3:0.00} Close={4:0.00}",
                    signalTime,
                    breakoutDirection,
                    orHigh,
                    orLow,
                    close));
            }
        }

        private bool PassOrbRangeFilter()
        {
            double orbTicks = Math.Abs(orHigh - orLow) / TickSize;
            if (orbTicks < MinORBTicks || orbTicks > MaxORBTicks)
            {
                return false;
            }

            return true;
        }

        private bool PassVolumeFilter(int bip, int barIndex)
        {
            if (!RequireVolumeConfirmation)
            {
                return true;
            }

            int neededBars = VolumeLookback + barIndex + 1;
            if (CurrentBars[bip] < neededBars)
            {
                return false;
            }

            double sum = 0;
            for (int i = barIndex + 1; i <= barIndex + VolumeLookback; i++)
            {
                sum += Volumes[bip][i];
            }

            double avg = sum / Math.Max(1, VolumeLookback);
            double currentVolume = Volumes[bip][barIndex];
            return currentVolume >= avg * MinVolumeMultiplier;
        }

        private bool PassPremarketConfluence(BreakoutDirection direction, double closePrice)
        {
            if (!RequirePremarketConfluence)
            {
                return true;
            }

            if (!hasPremarketLevels)
            {
                return false;
            }

            double confluenceBuffer = LevelZoneTicks * TickSize;
            if (direction == BreakoutDirection.Long)
            {
                return closePrice >= (premarketHigh - confluenceBuffer);
            }

            return closePrice <= (premarketLow + confluenceBuffer);
        }

        private void InvalidateExpiredBreakout(DateTime now)
        {
            if (breakoutDirection == BreakoutDirection.None || breakoutConsumed)
            {
                return;
            }

            if (MaxMinutesAfterBreakoutToEnter > 0 && breakoutTime != Core.Globals.MinDate)
            {
                if (now > breakoutTime.AddMinutes(MaxMinutesAfterBreakoutToEnter))
                {
                    InvalidateBreakout("Retest timeout reached.");
                    return;
                }
            }

            if (MaxDistanceFromLevelBeforeInvalid <= 0)
            {
                return;
            }

            double trackedLevel = GetConfiguredRetestLevel();
            if (double.IsNaN(trackedLevel))
            {
                return;
            }

            highestSinceBreakout = double.IsNaN(highestSinceBreakout) ? Highs[0][0] : Math.Max(highestSinceBreakout, Highs[0][0]);
            lowestSinceBreakout = double.IsNaN(lowestSinceBreakout) ? Lows[0][0] : Math.Min(lowestSinceBreakout, Lows[0][0]);

            double invalidTicks;
            if (breakoutDirection == BreakoutDirection.Long)
            {
                invalidTicks = (highestSinceBreakout - trackedLevel) / TickSize;
            }
            else
            {
                invalidTicks = (trackedLevel - lowestSinceBreakout) / TickSize;
            }

            if (invalidTicks > MaxDistanceFromLevelBeforeInvalid)
            {
                InvalidateBreakout("Moved too far away before retest.");
            }
        }

        private void EvaluateEntry(DateTime now)
        {
            if (tradesTakenToday >= MaxTradesPerDay || breakoutDirection == BreakoutDirection.None || breakoutConsumed)
            {
                return;
            }

            int hhmmss = ToHhmmss(now);
            if (hhmmss < TradeWindowStart || hhmmss > TradeWindowEnd)
            {
                return;
            }

            if (entryOrderSubmitted || entryFilled)
            {
                return;
            }

            if (!RequireRetest)
            {
                SubmitEntry(GetConfiguredRetestLevel(), "NoRetestMode");
                return;
            }

            double retestLevel = GetConfiguredRetestLevel();
            if (double.IsNaN(retestLevel))
            {
                return;
            }

            bool touched = IsLevelTouched(retestLevel, 0);
            if (EntryMode == EntryTriggerMode.TouchImmediate)
            {
                if (touched)
                {
                    SubmitEntry(retestLevel, "TouchImmediate");
                }

                return;
            }

            if (!waitingForRejectionClose && touched)
            {
                waitingForRejectionClose = true;
                rejectionTouchBar = CurrentBar;
                rejectionTouchLevel = retestLevel;
                return;
            }

            if (waitingForRejectionClose && IsFirstTickOfBar && rejectionTouchBar == CurrentBar - 1)
            {
                if (PassRejectionFilter(rejectionTouchLevel))
                {
                    SubmitEntry(rejectionTouchLevel, "TouchPlusRejection");
                }
                else
                {
                    waitingForRejectionClose = false;
                    rejectionTouchBar = -1;
                    rejectionTouchLevel = double.NaN;
                }
            }
        }

        private bool PassRejectionFilter(double level)
        {
            if (CurrentBar < 2)
            {
                return false;
            }

            double prevOpen = Opens[0][1];
            double prevClose = Closes[0][1];
            double prevHigh = Highs[0][1];
            double prevLow = Lows[0][1];
            double range = Math.Max(TickSize, prevHigh - prevLow);
            double bodyRatio = Math.Abs(prevClose - prevOpen) / range;

            if (bodyRatio < RejectionBodyRatioMin)
            {
                return false;
            }

            if (!IsLevelTouched(level, 1))
            {
                return false;
            }

            if (breakoutDirection == BreakoutDirection.Long)
            {
                return prevClose > prevOpen;
            }

            return prevClose < prevOpen;
        }

        private void SubmitEntry(double retestLevel, string reason)
        {
            if (double.IsNaN(retestLevel))
            {
                return;
            }

            double entryEstimate = Closes[0][0] + (breakoutDirection == BreakoutDirection.Long ? EntryOffsetTicks : -EntryOffsetTicks) * TickSize;
            double stopPrice = CalculateStopPrice(entryEstimate);
            if (double.IsNaN(stopPrice))
            {
                InvalidateBreakout("Invalid stop price.");
                return;
            }

            double stopTicks = Math.Abs(entryEstimate - stopPrice) / TickSize;
            if (stopTicks <= 0 || stopTicks > MaxStopTicks)
            {
                InvalidateBreakout("Stop distance outside limits.");
                return;
            }

            int quantity = CalculateOrderQuantity(stopTicks);
            if (quantity <= 0)
            {
                InvalidateBreakout("Calculated quantity <= 0.");
                return;
            }

            tradeCounter++;
            activeEntrySignal = string.Format("{0}_{1}_{2:yyyyMMdd}_{3}", breakoutDirection == BreakoutDirection.Long ? "L" : "S", Name, activeDate, tradeCounter);
            activeTradeDirection = breakoutDirection;
            activeEntryQuantity = quantity;
            plannedStopPrice = stopPrice;
            entryOrderSubmitted = true;
            entryFilled = false;
            breakoutConsumed = true;
            waitingForRejectionClose = false;
            rejectionTouchBar = -1;
            rejectionTouchLevel = double.NaN;
            tradesTakenToday++;

            SetStopLoss(activeEntrySignal, CalculationMode.Price, stopPrice, false);

            if (breakoutDirection == BreakoutDirection.Long)
            {
                EnterLong(quantity, activeEntrySignal);
            }
            else
            {
                EnterShort(quantity, activeEntrySignal);
            }

            if (EnableDebugLogging)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss} | ENTRY SUBMITTED | Reason={1} Dir={2} Qty={3} Est={4:0.00} Stop={5:0.00} Retest={6:0.00}",
                    ToConfiguredTime(Times[0][0], 0),
                    reason,
                    breakoutDirection,
                    quantity,
                    entryEstimate,
                    stopPrice,
                    retestLevel));
            }
        }

        private double CalculateStopPrice(double entryEstimate)
        {
            double buffer = StopBufferTicks * TickSize;
            double baseStopPrice;

            if (StopMode == StopSelectionMode.OppositeORB)
            {
                if (!hasORLevels)
                {
                    return double.NaN;
                }

                baseStopPrice = breakoutDirection == BreakoutDirection.Long
                    ? orLow - buffer
                    : orHigh + buffer;
            }
            else if (StopMode == StopSelectionMode.BeyondRetestCandle)
            {
                baseStopPrice = breakoutDirection == BreakoutDirection.Long
                    ? Lows[0][0] - buffer
                    : Highs[0][0] + buffer;
            }
            else
            {
                baseStopPrice = breakoutDirection == BreakoutDirection.Long
                    ? entryEstimate - FixedStopTicks * TickSize
                    : entryEstimate + FixedStopTicks * TickSize;
            }

            if (EmergencyStopMode == EmergencyStopSelectionMode.TrendSettingCandleExtreme &&
                !double.IsNaN(breakoutCandleHigh) && !double.IsNaN(breakoutCandleLow))
            {
                double emergencyStop = breakoutDirection == BreakoutDirection.Long
                    ? breakoutCandleLow - buffer
                    : breakoutCandleHigh + buffer;

                baseStopPrice = breakoutDirection == BreakoutDirection.Long
                    ? Math.Min(baseStopPrice, emergencyStop)
                    : Math.Max(baseStopPrice, emergencyStop);
            }

            return baseStopPrice;
        }

        private int CalculateOrderQuantity(double stopTicks)
        {
            int qtyByRisk = MinContracts;

            if (UseFixedDollarRisk)
            {
                double dollarsPerContract = stopTicks * TickSize * Instrument.MasterInstrument.PointValue;
                if (dollarsPerContract <= 0)
                {
                    return 0;
                }

                qtyByRisk = (int)Math.Floor(RiskPerTradeDollars / dollarsPerContract);
            }

            int result = Math.Max(MinContracts, qtyByRisk);
            result = Math.Min(result, MaxContracts);
            return Math.Max(0, result);
        }

        private void SubmitTargetsIfNeeded()
        {
            if (targetsSubmitted || !entryFilled || Position.MarketPosition == MarketPosition.Flat)
            {
                return;
            }

            if (initialRiskPrice <= 0)
            {
                return;
            }

            int targetQty = activeEntryQuantity;
            int runnerQty = 0;

            if (ScaleOutEnabled && activeEntryQuantity > 1)
            {
                targetQty = (int)Math.Round(activeEntryQuantity * (ScaleOutPercent / 100.0), MidpointRounding.AwayFromZero);
                targetQty = Math.Max(1, targetQty);
                if (targetQty >= activeEntryQuantity)
                {
                    targetQty = activeEntryQuantity - 1;
                }

                runnerQty = activeEntryQuantity - targetQty;
            }

            double target1Price = activeTradeDirection == BreakoutDirection.Long
                ? activeEntryPrice + (initialRiskPrice * Target1_R)
                : activeEntryPrice - (initialRiskPrice * Target1_R);

            string targetSignalName = "TP1_" + activeEntrySignal;
            string runnerSignalName = "Runner_" + activeEntrySignal;

            if (targetQty > 0)
            {
                if (activeTradeDirection == BreakoutDirection.Long)
                {
                    ExitLongLimit(0, true, targetQty, target1Price, targetSignalName, activeEntrySignal);
                }
                else
                {
                    ExitShortLimit(0, true, targetQty, target1Price, targetSignalName, activeEntrySignal);
                }
            }

            activeRunnerQuantity = runnerQty;
            if (runnerQty > 0)
            {
                double runnerTarget = GetRunnerTargetPrice();
                if (!double.IsNaN(runnerTarget))
                {
                    if (activeTradeDirection == BreakoutDirection.Long && runnerTarget > activeEntryPrice)
                    {
                        ExitLongLimit(0, true, runnerQty, runnerTarget, runnerSignalName, activeEntrySignal);
                    }
                    else if (activeTradeDirection == BreakoutDirection.Short && runnerTarget < activeEntryPrice)
                    {
                        ExitShortLimit(0, true, runnerQty, runnerTarget, runnerSignalName, activeEntrySignal);
                    }
                }
            }

            targetsSubmitted = true;
        }

        private double GetRunnerTargetPrice()
        {
            if (RunnerMode == RunnerSelectionMode.PremarketOppositeLevel)
            {
                if (!hasPremarketLevels)
                {
                    return double.NaN;
                }

                return activeTradeDirection == BreakoutDirection.Long ? premarketLow : premarketHigh;
            }

            if (RunnerMode == RunnerSelectionMode.FixedR)
            {
                return activeTradeDirection == BreakoutDirection.Long
                    ? activeEntryPrice + (initialRiskPrice * RunnerTargetR)
                    : activeEntryPrice - (initialRiskPrice * RunnerTargetR);
            }

            return double.NaN;
        }

        private void ManageOpenPosition()
        {
            if (!entryFilled || Position.MarketPosition == MarketPosition.Flat || initialRiskPrice <= 0)
            {
                return;
            }

            if (MoveStopToBreakEvenEnabled && !breakEvenMoved)
            {
                bool hitBreakEvenTrigger = activeTradeDirection == BreakoutDirection.Long
                    ? Highs[0][0] >= activeEntryPrice + (initialRiskPrice * BreakEvenAtR)
                    : Lows[0][0] <= activeEntryPrice - (initialRiskPrice * BreakEvenAtR);

                if (hitBreakEvenTrigger)
                {
                    double breakEvenStop = activeTradeDirection == BreakoutDirection.Long
                        ? activeEntryPrice + BreakEvenPlusTicks * TickSize
                        : activeEntryPrice - BreakEvenPlusTicks * TickSize;

                    UpdateStopIfImproved(breakEvenStop);
                    breakEvenMoved = true;

                    if (EnableDebugLogging)
                    {
                        Print(string.Format(
                            "{0:yyyy-MM-dd HH:mm:ss} | STOP TO BREAKEVEN | NewStop={1:0.00}",
                            ToConfiguredTime(Times[0][0], 0),
                            breakEvenStop));
                    }
                }
            }

            if (RunnerMode == RunnerSelectionMode.TrailOrNextLevel && activeRunnerQuantity > 0)
            {
                double trailStop = CalculateTrailStop();
                UpdateStopIfImproved(trailStop);
            }
        }

        private double CalculateTrailStop()
        {
            int lookback = Math.Max(2, TrailLookbackBars);
            if (CurrentBars[0] < lookback + 1)
            {
                return activeStopPrice;
            }

            double buffer = StopBufferTicks * TickSize;

            if (activeTradeDirection == BreakoutDirection.Long)
            {
                double lowest = double.MaxValue;
                for (int i = 1; i <= lookback; i++)
                {
                    lowest = Math.Min(lowest, Lows[0][i]);
                }

                return lowest - buffer;
            }

            double highest = double.MinValue;
            for (int i = 1; i <= lookback; i++)
            {
                highest = Math.Max(highest, Highs[0][i]);
            }

            return highest + buffer;
        }

        private void UpdateStopIfImproved(double newStop)
        {
            if (double.IsNaN(newStop) || string.IsNullOrEmpty(activeEntrySignal))
            {
                return;
            }

            bool improved = activeTradeDirection == BreakoutDirection.Long
                ? newStop > activeStopPrice
                : newStop < activeStopPrice;

            if (!improved)
            {
                return;
            }

            activeStopPrice = newStop;
            SetStopLoss(activeEntrySignal, CalculationMode.Price, newStop, false);
        }

        private void InvalidateBreakout(string reason)
        {
            if (EnableDebugLogging)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss} | BREAKOUT INVALIDATED | {1}",
                    ToConfiguredTime(Times[0][0], 0),
                    reason));
            }

            breakoutDirection = BreakoutDirection.None;
            breakoutTime = Core.Globals.MinDate;
            breakoutLevel = double.NaN;
            breakoutConsumed = false;
            highestSinceBreakout = double.NaN;
            lowestSinceBreakout = double.NaN;
            waitingForRejectionClose = false;
            rejectionTouchBar = -1;
            rejectionTouchLevel = double.NaN;
        }

        private bool IsLevelTouched(double level, int barsAgo)
        {
            if (double.IsNaN(level))
            {
                return false;
            }

            double zoneHalfWidth = LevelZoneTicks * TickSize;
            double touchOffset = TouchOffsetTicks * TickSize;

            double zoneLow = level - zoneHalfWidth - touchOffset;
            double zoneHigh = level + zoneHalfWidth + touchOffset;

            return Lows[0][barsAgo] <= zoneHigh && Highs[0][barsAgo] >= zoneLow;
        }

        private double GetConfiguredRetestLevel()
        {
            double primary = GetLevelForDirection(PrimaryLevel);
            if (!double.IsNaN(primary))
            {
                return primary;
            }

            return GetLevelForDirection(SecondaryLevel);
        }

        private double GetLevelForDirection(LevelSelectionMode levelMode)
        {
            if (breakoutDirection == BreakoutDirection.None)
            {
                return double.NaN;
            }

            if (levelMode == LevelSelectionMode.PremarketHighLow)
            {
                if (!hasPremarketLevels)
                {
                    return double.NaN;
                }

                return breakoutDirection == BreakoutDirection.Long ? premarketHigh : premarketLow;
            }

            if (!hasORLevels)
            {
                return double.NaN;
            }

            return breakoutDirection == BreakoutDirection.Long ? orHigh : orLow;
        }

        private DateTime ToConfiguredTime(DateTime barTime, int bip)
        {
            if (configuredTimeZone == null)
            {
                return barTime;
            }

            TimeZoneInfo sourceTimeZone = configuredTimeZone;
            try
            {
                if (BarsArray != null && BarsArray.Length > bip && BarsArray[bip] != null &&
                    BarsArray[bip].TradingHours != null && BarsArray[bip].TradingHours.TimeZoneInfo != null)
                {
                    sourceTimeZone = BarsArray[bip].TradingHours.TimeZoneInfo;
                }
            }
            catch
            {
                sourceTimeZone = configuredTimeZone;
            }

            try
            {
                return TimeZoneInfo.ConvertTime(barTime, sourceTimeZone, configuredTimeZone);
            }
            catch
            {
                return barTime;
            }
        }

        private void ResolveConfiguredTimeZone()
        {
            resolvedTimeZoneId = TimeZoneId;
            configuredTimeZone = null;

            string[] candidates = new[]
            {
                TimeZoneId,
                "America/Chicago",
                "Central Standard Time"
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    configuredTimeZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                    resolvedTimeZoneId = candidate;
                    break;
                }
                catch
                {
                    // try next
                }
            }

            if (EnableDebugLogging)
            {
                Print(string.Format("Resolved time zone: {0}", resolvedTimeZoneId));
            }
        }

        private static int ToHhmmss(DateTime time)
        {
            return time.Hour * 10000 + time.Minute * 100 + time.Second;
        }

        private static int AddMinutesToTime(int hhmmss, int minutesToAdd, int secondsAdjustment)
        {
            int h = hhmmss / 10000;
            int m = (hhmmss / 100) % 100;
            int s = hhmmss % 100;
            DateTime dt = new DateTime(2000, 1, 1, h, m, s).AddMinutes(minutesToAdd).AddSeconds(secondsAdjustment);
            return ToHhmmss(dt);
        }

        #region Inputs

        [NinjaScriptProperty]
        [Display(Name = "TimeZoneId", GroupName = TimeCategory, Order = 1)]
        public string TimeZoneId { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RTHOpenTime", GroupName = TimeCategory, Order = 2)]
        public int RTHOpenTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "PremarketStart", GroupName = TimeCategory, Order = 3)]
        public int PremarketStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "PremarketEnd", GroupName = TimeCategory, Order = 4)]
        public int PremarketEnd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "TradeWindowStart", GroupName = TimeCategory, Order = 5)]
        public int TradeWindowStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "TradeWindowEnd", GroupName = TimeCategory, Order = 6)]
        public int TradeWindowEnd { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "ORBMinutes", GroupName = OrbCategory, Order = 1)]
        public int ORBMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "ConfirmTimeframeMinutes", GroupName = OrbCategory, Order = 2)]
        public int ConfirmTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ConfirmOnClose", GroupName = OrbCategory, Order = 3)]
        public bool ConfirmOnClose { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "MinBreakoutCloseTicks", GroupName = OrbCategory, Order = 4)]
        public int MinBreakoutCloseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "LevelZoneTicks", GroupName = LevelsCategory, Order = 1)]
        public int LevelZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "TouchOffsetTicks", GroupName = LevelsCategory, Order = 2)]
        public int TouchOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "EntryOffsetTicks", GroupName = LevelsCategory, Order = 3)]
        public int EntryOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRetest", GroupName = SetupCategory, Order = 1)]
        public bool RequireRetest { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "MaxMinutesAfterBreakoutToEnter", GroupName = SetupCategory, Order = 2)]
        public int MaxMinutesAfterBreakoutToEnter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EntryMode", GroupName = SetupCategory, Order = 3)]
        public EntryTriggerMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequirePremarketConfluence", GroupName = SetupCategory, Order = 4)]
        public bool RequirePremarketConfluence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrimaryLevel", GroupName = SetupCategory, Order = 5)]
        public LevelSelectionMode PrimaryLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SecondaryLevel", GroupName = SetupCategory, Order = 6)]
        public LevelSelectionMode SecondaryLevel { get; set; }

        [NinjaScriptProperty]
        [Range(0, 400)]
        [Display(Name = "MaxDistanceFromLevelBeforeInvalid", GroupName = SetupCategory, Order = 7)]
        public int MaxDistanceFromLevelBeforeInvalid { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "RejectionBodyRatioMin", GroupName = SetupCategory, Order = 8)]
        public double RejectionBodyRatioMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopMode", GroupName = RiskCategory, Order = 1)]
        public StopSelectionMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EmergencyStopMode", GroupName = RiskCategory, Order = 2)]
        public EmergencyStopSelectionMode EmergencyStopMode { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "StopBufferTicks", GroupName = RiskCategory, Order = 3)]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name = "FixedStopTicks", GroupName = RiskCategory, Order = 4)]
        public int FixedStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "MaxStopTicks", GroupName = RiskCategory, Order = 5)]
        public int MaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 20.0)]
        [Display(Name = "Target1_R", GroupName = RiskCategory, Order = 6)]
        public double Target1_R { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ScaleOutEnabled", GroupName = RiskCategory, Order = 7)]
        public bool ScaleOutEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(1, 99)]
        [Display(Name = "ScaleOutPercent", GroupName = RiskCategory, Order = 8)]
        public int ScaleOutPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MoveStopToBreakEvenEnabled", GroupName = RiskCategory, Order = 9)]
        public bool MoveStopToBreakEvenEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 20.0)]
        [Display(Name = "BreakEvenAtR", GroupName = RiskCategory, Order = 10)]
        public double BreakEvenAtR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "BreakEvenPlusTicks", GroupName = RiskCategory, Order = 11)]
        public int BreakEvenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RunnerMode", GroupName = RiskCategory, Order = 12)]
        public RunnerSelectionMode RunnerMode { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "RunnerTargetR", GroupName = RiskCategory, Order = 13)]
        public double RunnerTargetR { get; set; }

        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "TrailLookbackBars", GroupName = RiskCategory, Order = 14)]
        public int TrailLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "MinORBTicks", GroupName = FilterCategory, Order = 1)]
        public int MinORBTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "MaxORBTicks", GroupName = FilterCategory, Order = 2)]
        public int MaxORBTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireVolumeConfirmation", GroupName = FilterCategory, Order = 3)]
        public bool RequireVolumeConfirmation { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "VolumeLookback", GroupName = FilterCategory, Order = 4)]
        public int VolumeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "MinVolumeMultiplier", GroupName = FilterCategory, Order = 5)]
        public double MinVolumeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireAggressiveBreakoutCandle", GroupName = FilterCategory, Order = 6)]
        public bool RequireAggressiveBreakoutCandle { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "BreakoutBodyRatioMin", GroupName = FilterCategory, Order = 7)]
        public double BreakoutBodyRatioMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseChopFilter", GroupName = FilterCategory, Order = 8)]
        public bool UseChopFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "MaxChopBodyRatio", GroupName = FilterCategory, Order = 9)]
        public double MaxChopBodyRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerDay", GroupName = PositionCategory, Order = 1)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseFixedDollarRisk", GroupName = PositionCategory, Order = 2)]
        public bool UseFixedDollarRisk { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "RiskPerTradeDollars", GroupName = PositionCategory, Order = 3)]
        public double RiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MinContracts", GroupName = PositionCategory, Order = 4)]
        public int MinContracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "MaxContracts", GroupName = PositionCategory, Order = 5)]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDebugLogging", GroupName = DebugCategory, Order = 1)]
        public bool EnableDebugLogging { get; set; }

        #endregion
    }
}
