#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LiquidityStructureContinuationStrategy : Strategy
    {
        public enum EntryTriggerMode
        {
            Minute1_BOS,
            Minute1_iFVG,
            RejectionCandle
        }

        public enum EntryOrderMode
        {
            Market,
            Limit
        }

        public enum StopPlacementMode
        {
            AbovePullbackSwing,
            AboveSweepExtreme,
            FixedTicks
        }

        private enum SetupStage
        {
            Idle,
            Swept,
            Confirmed,
            ConfluenceTouched
        }

        private enum TradeDirection
        {
            None,
            Long,
            Short
        }

        private int bosBip;
        private int hourBip;
        private int fourHourBip;
        private int smtBip;

        private SetupStage setupStage;
        private TradeDirection setupDirection;
        private TradeDirection higherTimeframeBias;

        private DateTime currentTradeDate;
        private int tradesToday;
        private bool wasInPosition;

        private DateTime sweepTime;
        private double sweepLevel;
        private double sweepExtreme;
        private double sweepImpulseHigh;
        private double sweepImpulseLow;

        private DateTime confirmationTime;
        private int confirmationBarOnBosSeries;
        private double impulseHigh;
        private double impulseLow;
        private double equilibriumPrice;

        private double confluenceLower;
        private double confluenceUpper;
        private DateTime confluenceTouchTime;
        private double pullbackExtreme;

        private double previousDayHigh;
        private double previousDayLow;
        private double currentDayHigh;
        private double currentDayLow;

        private double asiaHigh;
        private double asiaLow;
        private double londonHigh;
        private double londonLow;

        private readonly List<double> hourlySwingHighs = new List<double>();
        private readonly List<double> hourlySwingLows = new List<double>();

        private double lastSwingHigh5m;
        private double lastSwingLow5m;
        private double lastSwingHigh1m;
        private double lastSwingLow1m;

        private bool printedBearishFvgThisBosBar;
        private bool printedBullishFvgThisBosBar;
        private bool hasBearishFvgAfterConfirm;
        private bool hasBullishFvgAfterConfirm;
        private double bearishFvgLower;
        private double bearishFvgUpper;
        private double bullishFvgLower;
        private double bullishFvgUpper;

        private bool breakEvenMoved;
        private bool partialTaken;
        private DateTime positionEntryTime;
        private double activeStopPrice;
        private double initialRiskTicks;

        private readonly string[] longSignals = { "L1", "L2", "L3" };
        private readonly string[] shortSignals = { "S1", "S2", "S3" };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LiquidityStructureContinuationStrategy";
                Description = "Liquidity sweep -> BOS/iFVG -> EQ/FVG retrace -> 1m trigger continuation strategy.";

                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 3;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 50;

                UsePDH_PDL = true;
                UseAsiaHighLow = true;
                AsiaStartTime = 190000;
                AsiaEndTime = 010000;
                UseLondonHighLow = true;
                LondonStartTime = 020000;
                LondonEndTime = 070000;
                UseHourlySwings = true;
                SwingLookbackBars_H1 = 3;

                SweepBufferTicks = 2;
                SweepMaxAgeMinutes = 120;
                RequireCloseBackInside = true;
                MaxTradesPerDay = 2;

                EnableSMT = false;
                SMTInstrument = "ES 06-26";
                SmtLookbackMinutes = 30;

                BOSTimeframeMinutes = 5;
                PivotStrength_5m = 3;
                RequireBOS = true;
                AllowiFVGInsteadOfBOS = true;

                UseEquilibrium = true;
                EqBandTicks = 4;
                UseFVG = true;
                FVGMinSizeTicks = 2;
                MaxConfluenceWaitBars = 12;

                EntryTimeframeMinutes = 1;
                EntryTriggerType = EntryTriggerMode.Minute1_BOS;
                EntryPivotStrength_1m = 2;
                RejectionBodyRatioMin = 0.60;
                EntryOffsetTicks = 1;
                LimitOrMarket = EntryOrderMode.Market;

                StopType = StopPlacementMode.AbovePullbackSwing;
                StopBufferTicks = 2;
                FixedStopTicks = 20;
                MaxStopTicks = 60;
                RiskPerTradeDollars = 250;
                PercentAccountRisk = 0;
                RMultipleTargets = 2.0;
                MoveStopToBEAtR = 2.0;
                PartialAtR = 2.0;
                MaxHoldMinutes = 120;

                TradeOnlyRTH = true;
                RTHOpenTime = 083000;
                RTHCloseTime = 150000;
                StartTradingMinutesAfterOpen = 5;
                StopTradingMinutesBeforeClose = 15;

                UseHigherTimeframeBias = true;
            }
            else if (State == State.Configure)
            {
                if (EntryTimeframeMinutes != 1)
                    Print("This strategy is designed to run on a 1-minute primary chart. EntryTimeframeMinutes is kept as an input for transparency.");

                bosBip = 1;
                hourBip = 2;
                fourHourBip = 3;
                smtBip = -1;

                AddDataSeries(BarsPeriodType.Minute, BOSTimeframeMinutes);
                AddDataSeries(BarsPeriodType.Minute, 60);
                AddDataSeries(BarsPeriodType.Minute, 240);

                if (EnableSMT && !string.IsNullOrWhiteSpace(SMTInstrument))
                {
                    AddDataSeries(SMTInstrument, BarsPeriodType.Minute, 1);
                    smtBip = 4;
                }
            }
            else if (State == State.DataLoaded)
            {
                currentTradeDate = default(DateTime);
                tradesToday = 0;
                currentDayHigh = double.NaN;
                currentDayLow = double.NaN;
                asiaHigh = double.NaN;
                asiaLow = double.NaN;
                londonHigh = double.NaN;
                londonLow = double.NaN;

                ResetSetup();
                ResetTradeManagement();

                previousDayHigh = double.NaN;
                previousDayLow = double.NaN;
                lastSwingHigh5m = double.NaN;
                lastSwingLow5m = double.NaN;
                lastSwingHigh1m = double.NaN;
                lastSwingLow1m = double.NaN;
            }
        }

        protected override void OnBarUpdate()
        {
            if (!HasRequiredBars())
                return;

            if (BarsInProgress == 0)
            {
                UpdateDailyAndSessionLevels();
                UpdateEntrySwingsOnPrimary();
                UpdateSetupExpiryOnPrimary();
                ManageOpenPosition();

                if (Position.MarketPosition != MarketPosition.Flat)
                    return;

                if (!IsWithinTradingWindow(Times[0][0]))
                    return;

                if (tradesToday >= MaxTradesPerDay)
                    return;

                if (setupStage == SetupStage.Idle)
                    DetectSweepCandidate();
                else if (setupStage == SetupStage.ConfluenceTouched)
                {
                    UpdatePullbackExtreme();
                    TryEnterOnPrimaryTrigger();
                }
            }
            else if (BarsInProgress == bosBip)
            {
                UpdateBosSwings();
                UpdateBosFvg();

                if (setupStage == SetupStage.Swept)
                    TryConfirmFilledOrders();
                else if (setupStage == SetupStage.Confirmed)
                    TryConfirmContinuationConfluence();
            }
            else if (BarsInProgress == hourBip)
            {
                UpdateHourlySwings();
                UpdateHigherTimeframeBias();
            }
            else if (BarsInProgress == fourHourBip)
            {
                UpdateHigherTimeframeBias();
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name;
            if (IsEntrySignal(orderName))
            {
                positionEntryTime = time;
                if (activeStopPrice > 0 && Position.MarketPosition != MarketPosition.Flat)
                    initialRiskTicks = Math.Abs(activeStopPrice - Position.AveragePrice) / TickSize;
            }
        }

        private bool HasRequiredBars()
        {
            if (CurrentBars[0] < Math.Max(50, EntryPivotStrength_1m * 2 + 5))
                return false;
            if (CurrentBars[bosBip] < Math.Max(30, PivotStrength_5m * 2 + 5))
                return false;
            if (CurrentBars[hourBip] < SwingLookbackBars_H1 * 2 + 5)
                return false;
            if (CurrentBars[fourHourBip] < 5)
                return false;
            if (EnableSMT && smtBip >= 0 && CurrentBars[smtBip] < Math.Max(10, SmtLookbackMinutes + 5))
                return false;
            return true;
        }

        private void ResetForNewDay(DateTime newDate)
        {
            currentTradeDate = newDate;
            tradesToday = 0;

            currentDayHigh = Highs[0][0];
            currentDayLow = Lows[0][0];

            asiaHigh = double.NaN;
            asiaLow = double.NaN;
            londonHigh = double.NaN;
            londonLow = double.NaN;
        }

        private void UpdateDailyAndSessionLevels()
        {
            DateTime barDate = Times[0][0].Date;
            if (currentTradeDate == default(DateTime))
            {
                ResetForNewDay(barDate);
            }
            else if (currentTradeDate != barDate)
            {
                if (currentTradeDate != default(DateTime))
                {
                    previousDayHigh = currentDayHigh;
                    previousDayLow = currentDayLow;
                }

                ResetForNewDay(barDate);
                ResetSetup();
            }

            currentDayHigh = Math.Max(currentDayHigh, Highs[0][0]);
            currentDayLow = Math.Min(currentDayLow, Lows[0][0]);

            int timeValue = ToTime(Times[0][0]);
            if (UseAsiaHighLow && IsTimeInRange(timeValue, AsiaStartTime, AsiaEndTime))
            {
                asiaHigh = double.IsNaN(asiaHigh) ? Highs[0][0] : Math.Max(asiaHigh, Highs[0][0]);
                asiaLow = double.IsNaN(asiaLow) ? Lows[0][0] : Math.Min(asiaLow, Lows[0][0]);
            }

            if (UseLondonHighLow && IsTimeInRange(timeValue, LondonStartTime, LondonEndTime))
            {
                londonHigh = double.IsNaN(londonHigh) ? Highs[0][0] : Math.Max(londonHigh, Highs[0][0]);
                londonLow = double.IsNaN(londonLow) ? Lows[0][0] : Math.Min(londonLow, Lows[0][0]);
            }
        }

        private bool IsTimeInRange(int currentTime, int startTime, int endTime)
        {
            if (startTime <= endTime)
                return currentTime >= startTime && currentTime <= endTime;
            return currentTime >= startTime || currentTime <= endTime;
        }

        private void UpdateHigherTimeframeBias()
        {
            bool up1h = Closes[hourBip][0] > Closes[hourBip][1];
            bool down1h = Closes[hourBip][0] < Closes[hourBip][1];
            bool up4h = Closes[fourHourBip][0] > Closes[fourHourBip][1];
            bool down4h = Closes[fourHourBip][0] < Closes[fourHourBip][1];

            if (up1h && up4h)
                higherTimeframeBias = TradeDirection.Long;
            else if (down1h && down4h)
                higherTimeframeBias = TradeDirection.Short;
            else
                higherTimeframeBias = TradeDirection.None;
        }

        private void UpdateEntrySwingsOnPrimary()
        {
            int strength = EntryPivotStrength_1m;
            if (CurrentBars[0] < strength * 2 + 1)
                return;

            double centerHigh = Highs[0][strength];
            bool isPivotHigh = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Highs[0][i] >= centerHigh)
                {
                    isPivotHigh = false;
                    break;
                }
            }
            if (isPivotHigh)
                lastSwingHigh1m = centerHigh;

            double centerLow = Lows[0][strength];
            bool isPivotLow = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Lows[0][i] <= centerLow)
                {
                    isPivotLow = false;
                    break;
                }
            }
            if (isPivotLow)
                lastSwingLow1m = centerLow;
        }

        private void UpdateBosSwings()
        {
            int strength = PivotStrength_5m;
            if (CurrentBars[bosBip] < strength * 2 + 1)
                return;

            double centerHigh = Highs[bosBip][strength];
            bool isPivotHigh = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Highs[bosBip][i] >= centerHigh)
                {
                    isPivotHigh = false;
                    break;
                }
            }
            if (isPivotHigh)
                lastSwingHigh5m = centerHigh;

            double centerLow = Lows[bosBip][strength];
            bool isPivotLow = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Lows[bosBip][i] <= centerLow)
                {
                    isPivotLow = false;
                    break;
                }
            }
            if (isPivotLow)
                lastSwingLow5m = centerLow;
        }

        private void UpdateHourlySwings()
        {
            if (!UseHourlySwings)
                return;

            int strength = SwingLookbackBars_H1;
            if (CurrentBars[hourBip] < strength * 2 + 1)
                return;

            double centerHigh = Highs[hourBip][strength];
            bool isPivotHigh = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Highs[hourBip][i] >= centerHigh)
                {
                    isPivotHigh = false;
                    break;
                }
            }

            if (isPivotHigh && !ContainsApprox(hourlySwingHighs, centerHigh))
            {
                hourlySwingHighs.Insert(0, centerHigh);
                if (hourlySwingHighs.Count > 50)
                    hourlySwingHighs.RemoveAt(hourlySwingHighs.Count - 1);
            }

            double centerLow = Lows[hourBip][strength];
            bool isPivotLow = true;
            for (int i = 0; i <= strength * 2; i++)
            {
                if (i == strength)
                    continue;
                if (Lows[hourBip][i] <= centerLow)
                {
                    isPivotLow = false;
                    break;
                }
            }

            if (isPivotLow && !ContainsApprox(hourlySwingLows, centerLow))
            {
                hourlySwingLows.Insert(0, centerLow);
                if (hourlySwingLows.Count > 50)
                    hourlySwingLows.RemoveAt(hourlySwingLows.Count - 1);
            }
        }

        private bool ContainsApprox(List<double> values, double price)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (Math.Abs(values[i] - price) <= TickSize * 2)
                    return true;
            }
            return false;
        }

        private void UpdateBosFvg()
        {
            printedBearishFvgThisBosBar = false;
            printedBullishFvgThisBosBar = false;

            if (CurrentBars[bosBip] < 3)
                return;

            // Bearish FVG: Candle(2).Low > Candle(0).High
            if (Lows[bosBip][2] > Highs[bosBip][0])
            {
                double lower = Highs[bosBip][0];
                double upper = Lows[bosBip][2];
                double sizeTicks = (upper - lower) / TickSize;
                if (sizeTicks >= FVGMinSizeTicks)
                {
                    printedBearishFvgThisBosBar = true;
                    if (setupStage == SetupStage.Confirmed && setupDirection == TradeDirection.Short)
                    {
                        hasBearishFvgAfterConfirm = true;
                        bearishFvgLower = lower;
                        bearishFvgUpper = upper;
                    }
                }
            }

            // Bullish FVG: Candle(2).High < Candle(0).Low
            if (Highs[bosBip][2] < Lows[bosBip][0])
            {
                double lower = Highs[bosBip][2];
                double upper = Lows[bosBip][0];
                double sizeTicks = (upper - lower) / TickSize;
                if (sizeTicks >= FVGMinSizeTicks)
                {
                    printedBullishFvgThisBosBar = true;
                    if (setupStage == SetupStage.Confirmed && setupDirection == TradeDirection.Long)
                    {
                        hasBullishFvgAfterConfirm = true;
                        bullishFvgLower = lower;
                        bullishFvgUpper = upper;
                    }
                }
            }
        }

        private void DetectSweepCandidate()
        {
            TradeDirection direction = TradeDirection.None;
            double level = double.NaN;

            if (TryDetectHighSideSweep(out level))
                direction = TradeDirection.Short;
            else if (TryDetectLowSideSweep(out level))
                direction = TradeDirection.Long;

            if (direction == TradeDirection.None)
                return;

            if (UseHigherTimeframeBias && higherTimeframeBias != TradeDirection.None && direction != higherTimeframeBias)
                return;

            if (!PassesSmtFilter(direction))
                return;

            setupDirection = direction;
            setupStage = SetupStage.Swept;
            sweepTime = Times[0][0];
            sweepLevel = level;
            sweepExtreme = direction == TradeDirection.Short ? Highs[0][0] : Lows[0][0];
            sweepImpulseHigh = Highs[0][0];
            sweepImpulseLow = Lows[0][0];
            hasBearishFvgAfterConfirm = false;
            hasBullishFvgAfterConfirm = false;
        }

        private bool TryDetectHighSideSweep(out double level)
        {
            level = double.NaN;
            double buffer = SweepBufferTicks * TickSize;

            if (UsePDH_PDL && !double.IsNaN(previousDayHigh) && Highs[0][0] >= previousDayHigh + buffer && (!RequireCloseBackInside || Closes[0][0] < previousDayHigh))
            {
                level = previousDayHigh;
                return true;
            }

            if (UseAsiaHighLow && !double.IsNaN(asiaHigh) && Highs[0][0] >= asiaHigh + buffer && (!RequireCloseBackInside || Closes[0][0] < asiaHigh))
            {
                level = asiaHigh;
                return true;
            }

            if (UseLondonHighLow && !double.IsNaN(londonHigh) && Highs[0][0] >= londonHigh + buffer && (!RequireCloseBackInside || Closes[0][0] < londonHigh))
            {
                level = londonHigh;
                return true;
            }

            if (UseHourlySwings)
            {
                for (int i = 0; i < hourlySwingHighs.Count; i++)
                {
                    double swingHigh = hourlySwingHighs[i];
                    if (Highs[0][0] >= swingHigh + buffer && (!RequireCloseBackInside || Closes[0][0] < swingHigh))
                    {
                        level = swingHigh;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryDetectLowSideSweep(out double level)
        {
            level = double.NaN;
            double buffer = SweepBufferTicks * TickSize;

            if (UsePDH_PDL && !double.IsNaN(previousDayLow) && Lows[0][0] <= previousDayLow - buffer && (!RequireCloseBackInside || Closes[0][0] > previousDayLow))
            {
                level = previousDayLow;
                return true;
            }

            if (UseAsiaHighLow && !double.IsNaN(asiaLow) && Lows[0][0] <= asiaLow - buffer && (!RequireCloseBackInside || Closes[0][0] > asiaLow))
            {
                level = asiaLow;
                return true;
            }

            if (UseLondonHighLow && !double.IsNaN(londonLow) && Lows[0][0] <= londonLow - buffer && (!RequireCloseBackInside || Closes[0][0] > londonLow))
            {
                level = londonLow;
                return true;
            }

            if (UseHourlySwings)
            {
                for (int i = 0; i < hourlySwingLows.Count; i++)
                {
                    double swingLow = hourlySwingLows[i];
                    if (Lows[0][0] <= swingLow - buffer && (!RequireCloseBackInside || Closes[0][0] > swingLow))
                    {
                        level = swingLow;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool PassesSmtFilter(TradeDirection direction)
        {
            if (!EnableSMT || smtBip < 0)
                return true;

            int lookback = Math.Max(5, SmtLookbackMinutes);
            if (CurrentBars[0] < lookback + 2 || CurrentBars[smtBip] < lookback + 2)
                return false;

            if (direction == TradeDirection.Short)
            {
                double primaryPreviousHigh = Highs[0][1];
                double secondaryPreviousHigh = Highs[smtBip][1];
                for (int i = 2; i <= lookback + 1; i++)
                {
                    primaryPreviousHigh = Math.Max(primaryPreviousHigh, Highs[0][i]);
                    secondaryPreviousHigh = Math.Max(secondaryPreviousHigh, Highs[smtBip][i]);
                }

                bool primaryMadeNewHigh = Highs[0][0] > primaryPreviousHigh;
                bool secondaryFailedToConfirm = Highs[smtBip][0] <= secondaryPreviousHigh;
                return primaryMadeNewHigh && secondaryFailedToConfirm;
            }

            if (direction == TradeDirection.Long)
            {
                double primaryPreviousLow = Lows[0][1];
                double secondaryPreviousLow = Lows[smtBip][1];
                for (int i = 2; i <= lookback + 1; i++)
                {
                    primaryPreviousLow = Math.Min(primaryPreviousLow, Lows[0][i]);
                    secondaryPreviousLow = Math.Min(secondaryPreviousLow, Lows[smtBip][i]);
                }

                bool primaryMadeNewLow = Lows[0][0] < primaryPreviousLow;
                bool secondaryFailedToConfirm = Lows[smtBip][0] >= secondaryPreviousLow;
                return primaryMadeNewLow && secondaryFailedToConfirm;
            }

            return false;
        }

        private void TryConfirmFilledOrders()
        {
            if ((Times[bosBip][0] - sweepTime).TotalMinutes > SweepMaxAgeMinutes)
            {
                ResetSetup();
                return;
            }

            sweepImpulseHigh = Math.Max(sweepImpulseHigh, Highs[bosBip][0]);
            sweepImpulseLow = Math.Min(sweepImpulseLow, Lows[bosBip][0]);

            bool bosConfirmed = false;
            if (setupDirection == TradeDirection.Short && !double.IsNaN(lastSwingLow5m))
                bosConfirmed = Closes[bosBip][0] < lastSwingLow5m;
            else if (setupDirection == TradeDirection.Long && !double.IsNaN(lastSwingHigh5m))
                bosConfirmed = Closes[bosBip][0] > lastSwingHigh5m;

            bool ifvgConfirmed = false;
            if (setupDirection == TradeDirection.Short)
                ifvgConfirmed = printedBearishFvgThisBosBar;
            else if (setupDirection == TradeDirection.Long)
                ifvgConfirmed = printedBullishFvgThisBosBar;

            bool confirmed = false;
            if (RequireBOS && !AllowiFVGInsteadOfBOS)
                confirmed = bosConfirmed;
            else if (RequireBOS && AllowiFVGInsteadOfBOS)
                confirmed = bosConfirmed || ifvgConfirmed;
            else if (!RequireBOS && AllowiFVGInsteadOfBOS)
                confirmed = ifvgConfirmed;
            else
                confirmed = bosConfirmed;

            if (!confirmed)
                return;

            setupStage = SetupStage.Confirmed;
            confirmationTime = Times[bosBip][0];
            confirmationBarOnBosSeries = CurrentBars[bosBip];

            impulseHigh = sweepImpulseHigh;
            impulseLow = sweepImpulseLow;
            equilibriumPrice = (impulseHigh + impulseLow) * 0.5;
        }

        private void TryConfirmContinuationConfluence()
        {
            int barsSinceConfirmation = CurrentBars[bosBip] - confirmationBarOnBosSeries;
            if (barsSinceConfirmation > MaxConfluenceWaitBars)
            {
                ResetSetup();
                return;
            }

            bool touchedEq = false;
            bool touchedFvg = false;

            double eqLower = equilibriumPrice - EqBandTicks * TickSize;
            double eqUpper = equilibriumPrice + EqBandTicks * TickSize;

            if (UseEquilibrium)
                touchedEq = Highs[bosBip][0] >= eqLower && Lows[bosBip][0] <= eqUpper;

            if (UseFVG)
            {
                if (setupDirection == TradeDirection.Short && hasBearishFvgAfterConfirm)
                    touchedFvg = Highs[bosBip][0] >= bearishFvgLower && Lows[bosBip][0] <= bearishFvgUpper;
                else if (setupDirection == TradeDirection.Long && hasBullishFvgAfterConfirm)
                    touchedFvg = Highs[bosBip][0] >= bullishFvgLower && Lows[bosBip][0] <= bullishFvgUpper;
            }

            if (!touchedEq && !touchedFvg)
                return;

            setupStage = SetupStage.ConfluenceTouched;
            confluenceTouchTime = Times[bosBip][0];

            if (touchedEq && touchedFvg)
            {
                double fvgLower = setupDirection == TradeDirection.Short ? bearishFvgLower : bullishFvgLower;
                double fvgUpper = setupDirection == TradeDirection.Short ? bearishFvgUpper : bullishFvgUpper;
                confluenceLower = Math.Min(eqLower, fvgLower);
                confluenceUpper = Math.Max(eqUpper, fvgUpper);
            }
            else if (touchedEq)
            {
                confluenceLower = eqLower;
                confluenceUpper = eqUpper;
            }
            else
            {
                confluenceLower = setupDirection == TradeDirection.Short ? bearishFvgLower : bullishFvgLower;
                confluenceUpper = setupDirection == TradeDirection.Short ? bearishFvgUpper : bullishFvgUpper;
            }

            pullbackExtreme = setupDirection == TradeDirection.Short ? Highs[bosBip][0] : Lows[bosBip][0];
        }

        private void UpdatePullbackExtreme()
        {
            if (setupDirection == TradeDirection.Short)
                pullbackExtreme = Math.Max(pullbackExtreme, Highs[0][0]);
            else if (setupDirection == TradeDirection.Long)
                pullbackExtreme = Math.Min(pullbackExtreme, Lows[0][0]);
        }

        private void UpdateSetupExpiryOnPrimary()
        {
            if (setupStage == SetupStage.Swept && (Times[0][0] - sweepTime).TotalMinutes > SweepMaxAgeMinutes)
            {
                ResetSetup();
                return;
            }

            if (setupStage == SetupStage.ConfluenceTouched)
            {
                double allowedMinutes = MaxConfluenceWaitBars * BOSTimeframeMinutes;
                if ((Times[0][0] - confluenceTouchTime).TotalMinutes > allowedMinutes)
                    ResetSetup();
            }
        }

        private void TryEnterOnPrimaryTrigger()
        {
            bool triggered = false;

            if (EntryTriggerType == EntryTriggerMode.Minute1_BOS)
                triggered = EntryTriggerBos1m();
            else if (EntryTriggerType == EntryTriggerMode.Minute1_iFVG)
                triggered = EntryTriggerIfvg1m();
            else if (EntryTriggerType == EntryTriggerMode.RejectionCandle)
                triggered = EntryTriggerRejectionCandle();

            if (!triggered)
                return;

            SubmitEntryOrders();
        }

        private bool EntryTriggerBos1m()
        {
            if (setupDirection == TradeDirection.Short && !double.IsNaN(lastSwingLow1m))
                return Closes[0][0] < lastSwingLow1m;
            if (setupDirection == TradeDirection.Long && !double.IsNaN(lastSwingHigh1m))
                return Closes[0][0] > lastSwingHigh1m;
            return false;
        }

        private bool EntryTriggerIfvg1m()
        {
            if (CurrentBars[0] < 3)
                return false;

            if (setupDirection == TradeDirection.Short)
                return Lows[0][2] > Highs[0][0];
            if (setupDirection == TradeDirection.Long)
                return Highs[0][2] < Lows[0][0];
            return false;
        }

        private bool EntryTriggerRejectionCandle()
        {
            double range = Highs[0][0] - Lows[0][0];
            if (range <= TickSize)
                return false;

            double body = Math.Abs(Closes[0][0] - Opens[0][0]);
            double bodyRatio = body / range;
            if (bodyRatio < RejectionBodyRatioMin)
                return false;

            bool touchedConfluence = Highs[0][0] >= confluenceLower && Lows[0][0] <= confluenceUpper;
            if (!touchedConfluence)
                return false;

            if (setupDirection == TradeDirection.Short)
                return Closes[0][0] < Opens[0][0];
            if (setupDirection == TradeDirection.Long)
                return Closes[0][0] > Opens[0][0];

            return false;
        }

        private void SubmitEntryOrders()
        {
            double rawEntryPrice = Closes[0][0];
            double desiredEntryPrice = rawEntryPrice;

            if (LimitOrMarket == EntryOrderMode.Limit)
            {
                if (setupDirection == TradeDirection.Short)
                    desiredEntryPrice = rawEntryPrice + EntryOffsetTicks * TickSize;
                else
                    desiredEntryPrice = rawEntryPrice - EntryOffsetTicks * TickSize;
            }

            double stopPrice;
            double stopTicks;
            if (!TryComputeStop(desiredEntryPrice, out stopPrice, out stopTicks))
            {
                ResetSetup();
                return;
            }

            int totalQuantity = ComputeQuantity(stopTicks);
            if (totalQuantity < 1)
            {
                ResetSetup();
                return;
            }

            List<double> targets = BuildTargets(desiredEntryPrice, stopTicks);
            int[] legQuantities = BuildLegQuantities(totalQuantity);
            string[] signals = setupDirection == TradeDirection.Short ? shortSignals : longSignals;

            activeStopPrice = stopPrice;
            initialRiskTicks = stopTicks;
            breakEvenMoved = false;
            partialTaken = false;
            positionEntryTime = Times[0][0];

            for (int i = 0; i < legQuantities.Length; i++)
            {
                if (legQuantities[i] <= 0)
                    continue;

                string signal = signals[i];
                SetStopLoss(signal, CalculationMode.Price, stopPrice, false);
                if (i < targets.Count)
                    SetProfitTarget(signal, CalculationMode.Price, targets[i]);

                if (setupDirection == TradeDirection.Short)
                {
                    if (LimitOrMarket == EntryOrderMode.Market)
                        EnterShort(legQuantities[i], signal);
                    else
                        EnterShortLimit(legQuantities[i], desiredEntryPrice, signal);
                }
                else
                {
                    if (LimitOrMarket == EntryOrderMode.Market)
                        EnterLong(legQuantities[i], signal);
                    else
                        EnterLongLimit(legQuantities[i], desiredEntryPrice, signal);
                }
            }

            tradesToday++;
            ResetSetup();
        }

        private bool TryComputeStop(double entryPrice, out double stopPrice, out double stopTicks)
        {
            stopPrice = 0;
            stopTicks = 0;
            double buffer = StopBufferTicks * TickSize;

            if (setupDirection == TradeDirection.Short)
            {
                if (StopType == StopPlacementMode.AbovePullbackSwing)
                    stopPrice = pullbackExtreme + buffer;
                else if (StopType == StopPlacementMode.AboveSweepExtreme)
                    stopPrice = sweepExtreme + buffer;
                else
                    stopPrice = entryPrice + FixedStopTicks * TickSize;

                if (stopPrice <= entryPrice)
                    stopPrice = entryPrice + TickSize;
            }
            else
            {
                if (StopType == StopPlacementMode.AbovePullbackSwing)
                    stopPrice = pullbackExtreme - buffer;
                else if (StopType == StopPlacementMode.AboveSweepExtreme)
                    stopPrice = sweepExtreme - buffer;
                else
                    stopPrice = entryPrice - FixedStopTicks * TickSize;

                if (stopPrice >= entryPrice)
                    stopPrice = entryPrice - TickSize;
            }

            stopPrice = Instrument.MasterInstrument.RoundToTickSize(stopPrice);
            stopTicks = Math.Abs(stopPrice - entryPrice) / TickSize;
            if (stopTicks < 1 || stopTicks > MaxStopTicks)
                return false;

            return true;
        }

        private int ComputeQuantity(double stopTicks)
        {
            double riskDollars = RiskPerTradeDollars;
            if (PercentAccountRisk > 0)
            {
                double accountSize = 0;
                try
                {
                    if (Account != null)
                        accountSize = Account.Get(AccountItem.CashValue, Currency.UsDollar);
                }
                catch
                {
                    accountSize = 0;
                }

                if (accountSize <= 0)
                    accountSize = 50000;

                riskDollars = accountSize * (PercentAccountRisk / 100.0);
            }

            if (riskDollars <= 0)
                return Math.Max(1, DefaultQuantity);

            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            double riskPerContract = stopTicks * tickValue;
            if (riskPerContract <= 0)
                return Math.Max(1, DefaultQuantity);

            int qty = (int)Math.Floor(riskDollars / riskPerContract);
            if (qty < 1)
                return 0;

            return qty;
        }

        private List<double> BuildTargets(double entryPrice, double stopTicks)
        {
            List<double> targets = new List<double>();
            List<double> liquidityTargets = GetDirectionalLiquidityTargets(entryPrice);

            for (int i = 0; i < liquidityTargets.Count && targets.Count < 3; i++)
                targets.Add(Instrument.MasterInstrument.RoundToTickSize(liquidityTargets[i]));

            while (targets.Count < 3)
            {
                double rMultiple = RMultipleTargets + targets.Count;
                double offset = stopTicks * TickSize * rMultiple;
                double target = setupDirection == TradeDirection.Short ? entryPrice - offset : entryPrice + offset;
                targets.Add(Instrument.MasterInstrument.RoundToTickSize(target));
            }

            return targets;
        }

        private List<double> GetDirectionalLiquidityTargets(double entryPrice)
        {
            List<double> levels = new List<double>();

            AddLiquidityLevelIfValid(levels, previousDayHigh);
            AddLiquidityLevelIfValid(levels, previousDayLow);
            AddLiquidityLevelIfValid(levels, asiaHigh);
            AddLiquidityLevelIfValid(levels, asiaLow);
            AddLiquidityLevelIfValid(levels, londonHigh);
            AddLiquidityLevelIfValid(levels, londonLow);

            if (UseHourlySwings)
            {
                for (int i = 0; i < hourlySwingHighs.Count; i++)
                    AddLiquidityLevelIfValid(levels, hourlySwingHighs[i]);
                for (int i = 0; i < hourlySwingLows.Count; i++)
                    AddLiquidityLevelIfValid(levels, hourlySwingLows[i]);
            }

            List<double> filtered = new List<double>();
            for (int i = 0; i < levels.Count; i++)
            {
                if (setupDirection == TradeDirection.Short && levels[i] < entryPrice - TickSize)
                    filtered.Add(levels[i]);
                else if (setupDirection == TradeDirection.Long && levels[i] > entryPrice + TickSize)
                    filtered.Add(levels[i]);
            }

            if (setupDirection == TradeDirection.Short)
                filtered.Sort((a, b) => b.CompareTo(a));
            else
                filtered.Sort((a, b) => a.CompareTo(b));

            List<double> deduped = new List<double>();
            for (int i = 0; i < filtered.Count; i++)
            {
                if (deduped.Count == 0 || Math.Abs(filtered[i] - deduped[deduped.Count - 1]) > TickSize)
                    deduped.Add(filtered[i]);
            }

            return deduped;
        }

        private void AddLiquidityLevelIfValid(List<double> levels, double price)
        {
            if (!double.IsNaN(price) && price > 0)
                levels.Add(price);
        }

        private int[] BuildLegQuantities(int totalQty)
        {
            int[] legs = new int[3];
            if (totalQty <= 0)
                return legs;

            if (totalQty == 1)
            {
                legs[0] = 1;
                return legs;
            }

            if (totalQty == 2)
            {
                legs[0] = 1;
                legs[1] = 1;
                return legs;
            }

            legs[0] = totalQty / 2;
            legs[1] = (totalQty - legs[0]) / 2;
            legs[2] = totalQty - legs[0] - legs[1];

            if (legs[0] == 0) legs[0] = 1;
            if (legs[1] == 0 && totalQty >= 2) legs[1] = 1;
            legs[2] = Math.Max(0, totalQty - legs[0] - legs[1]);

            return legs;
        }

        private void ManageOpenPosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (wasInPosition)
                    ResetTradeManagement();
                wasInPosition = false;
                return;
            }

            wasInPosition = true;

            if (initialRiskTicks <= 0 || Position.AveragePrice <= 0)
                return;

            double riskDistance = initialRiskTicks * TickSize;
            if (riskDistance <= 0)
                return;

            double progressR = 0;
            if (Position.MarketPosition == MarketPosition.Long)
                progressR = (Closes[0][0] - Position.AveragePrice) / riskDistance;
            else if (Position.MarketPosition == MarketPosition.Short)
                progressR = (Position.AveragePrice - Closes[0][0]) / riskDistance;

            if (!breakEvenMoved && MoveStopToBEAtR > 0 && progressR >= MoveStopToBEAtR)
            {
                MoveStopsToBreakEven();
                breakEvenMoved = true;
            }

            if (!partialTaken && PartialAtR > 0 && progressR >= PartialAtR)
            {
                TakePartialProfit();
                partialTaken = true;
            }

            if (MaxHoldMinutes > 0 && (Times[0][0] - positionEntryTime).TotalMinutes >= MaxHoldMinutes)
                ExitForTimeStop();
        }

        private void MoveStopsToBreakEven()
        {
            double bePrice = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice);
            string[] signals = Position.MarketPosition == MarketPosition.Long ? longSignals : shortSignals;
            for (int i = 0; i < signals.Length; i++)
                SetStopLoss(signals[i], CalculationMode.Price, bePrice, false);
        }

        private void TakePartialProfit()
        {
            int qty = Position.Quantity / 2;
            if (qty < 1)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(qty, "PartialAtR", string.Empty);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(qty, "PartialAtR", string.Empty);
        }

        private void ExitForTimeStop()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("MaxHoldExit", string.Empty);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("MaxHoldExit", string.Empty);
        }

        private bool IsWithinTradingWindow(DateTime barTime)
        {
            if (!TradeOnlyRTH)
                return true;

            int current = ToTime(barTime);
            int start = AddMinutesToTime(RTHOpenTime, StartTradingMinutesAfterOpen);
            int end = AddMinutesToTime(RTHCloseTime, -StopTradingMinutesBeforeClose);

            return current >= start && current <= end;
        }

        private int AddMinutesToTime(int hhmmss, int minutesOffset)
        {
            int hh = hhmmss / 10000;
            int mm = (hhmmss % 10000) / 100;
            int ss = hhmmss % 100;

            DateTime dt = new DateTime(2000, 1, 1, hh, mm, ss).AddMinutes(minutesOffset);
            return dt.Hour * 10000 + dt.Minute * 100 + dt.Second;
        }

        private bool IsEntrySignal(string orderName)
        {
            for (int i = 0; i < longSignals.Length; i++)
            {
                if (orderName == longSignals[i] || orderName == shortSignals[i])
                    return true;
            }
            return false;
        }

        private void ResetSetup()
        {
            setupStage = SetupStage.Idle;
            setupDirection = TradeDirection.None;
            sweepTime = default(DateTime);
            sweepLevel = double.NaN;
            sweepExtreme = double.NaN;
            sweepImpulseHigh = double.NaN;
            sweepImpulseLow = double.NaN;
            confirmationTime = default(DateTime);
            confirmationBarOnBosSeries = 0;
            impulseHigh = double.NaN;
            impulseLow = double.NaN;
            equilibriumPrice = double.NaN;
            confluenceLower = double.NaN;
            confluenceUpper = double.NaN;
            confluenceTouchTime = default(DateTime);
            pullbackExtreme = double.NaN;
            hasBearishFvgAfterConfirm = false;
            hasBullishFvgAfterConfirm = false;
        }

        private void ResetTradeManagement()
        {
            breakEvenMoved = false;
            partialTaken = false;
            positionEntryTime = default(DateTime);
            activeStopPrice = 0;
            initialRiskTicks = 0;
        }

        #region Parameters
        [NinjaScriptProperty]
        [Display(Name = "UsePDH_PDL", GroupName = "Liquidity Levels", Order = 1)]
        public bool UsePDH_PDL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseAsiaHighLow", GroupName = "Liquidity Levels", Order = 2)]
        public bool UseAsiaHighLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AsiaStartTime", GroupName = "Liquidity Levels", Order = 3)]
        public int AsiaStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AsiaEndTime", GroupName = "Liquidity Levels", Order = 4)]
        public int AsiaEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseLondonHighLow", GroupName = "Liquidity Levels", Order = 5)]
        public bool UseLondonHighLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LondonStartTime", GroupName = "Liquidity Levels", Order = 6)]
        public int LondonStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LondonEndTime", GroupName = "Liquidity Levels", Order = 7)]
        public int LondonEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseHourlySwings", GroupName = "Liquidity Levels", Order = 8)]
        public bool UseHourlySwings { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "SwingLookbackBars_H1", GroupName = "Liquidity Levels", Order = 9)]
        public int SwingLookbackBars_H1 { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "SweepBufferTicks", GroupName = "Sweep", Order = 10)]
        public int SweepBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 480)]
        [Display(Name = "SweepMaxAgeMinutes", GroupName = "Sweep", Order = 11)]
        public int SweepMaxAgeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireCloseBackInside", GroupName = "Sweep", Order = 12)]
        public bool RequireCloseBackInside { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerDay", GroupName = "Sweep", Order = 13)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableSMT", GroupName = "SMT", Order = 14)]
        public bool EnableSMT { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SMTInstrument", GroupName = "SMT", Order = 15)]
        public string SMTInstrument { get; set; }

        [NinjaScriptProperty]
        [Range(5, 240)]
        [Display(Name = "SmtLookbackMinutes", GroupName = "SMT", Order = 16)]
        public int SmtLookbackMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "BOSTimeframeMinutes", GroupName = "Structure", Order = 17)]
        public int BOSTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "PivotStrength_5m", GroupName = "Structure", Order = 18)]
        public int PivotStrength_5m { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireBOS", GroupName = "Structure", Order = 19)]
        public bool RequireBOS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowiFVGInsteadOfBOS", GroupName = "Structure", Order = 20)]
        public bool AllowiFVGInsteadOfBOS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseEquilibrium", GroupName = "Confluence", Order = 21)]
        public bool UseEquilibrium { get; set; }

        [NinjaScriptProperty]
        [Range(0, 40)]
        [Display(Name = "EqBandTicks", GroupName = "Confluence", Order = 22)]
        public int EqBandTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseFVG", GroupName = "Confluence", Order = 23)]
        public bool UseFVG { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "FVGMinSizeTicks", GroupName = "Confluence", Order = 24)]
        public int FVGMinSizeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MaxConfluenceWaitBars", GroupName = "Confluence", Order = 25)]
        public int MaxConfluenceWaitBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EntryTimeframeMinutes", GroupName = "Entry", Order = 26)]
        public int EntryTimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EntryTriggerType", GroupName = "Entry", Order = 27)]
        public EntryTriggerMode EntryTriggerType { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "EntryPivotStrength_1m", GroupName = "Entry", Order = 28)]
        public int EntryPivotStrength_1m { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "RejectionBodyRatioMin", GroupName = "Entry", Order = 29)]
        public double RejectionBodyRatioMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "EntryOffsetTicks", GroupName = "Entry", Order = 30)]
        public int EntryOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LimitOrMarket", GroupName = "Entry", Order = 31)]
        public EntryOrderMode LimitOrMarket { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopType", GroupName = "Risk", Order = 32)]
        public StopPlacementMode StopType { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "StopBufferTicks", GroupName = "Risk", Order = 33)]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "FixedStopTicks", GroupName = "Risk", Order = 34)]
        public int FixedStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "MaxStopTicks", GroupName = "Risk", Order = 35)]
        public int MaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "RiskPerTradeDollars", GroupName = "Risk", Order = 36)]
        public double RiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "PercentAccountRisk", GroupName = "Risk", Order = 37)]
        public double PercentAccountRisk { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10)]
        [Display(Name = "RMultipleTargets", GroupName = "Risk", Order = 38)]
        public double RMultipleTargets { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "MoveStopToBEAtR", GroupName = "Risk", Order = 39)]
        public double MoveStopToBEAtR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "PartialAtR", GroupName = "Risk", Order = 40)]
        public double PartialAtR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1440)]
        [Display(Name = "MaxHoldMinutes", GroupName = "Risk", Order = 41)]
        public int MaxHoldMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TradeOnlyRTH", GroupName = "Time Filters", Order = 42)]
        public bool TradeOnlyRTH { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTHOpenTime", GroupName = "Time Filters", Order = 43)]
        public int RTHOpenTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTHCloseTime", GroupName = "Time Filters", Order = 44)]
        public int RTHCloseTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "StartTradingMinutesAfterOpen", GroupName = "Time Filters", Order = 45)]
        public int StartTradingMinutesAfterOpen { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "StopTradingMinutesBeforeClose", GroupName = "Time Filters", Order = 46)]
        public int StopTradingMinutesBeforeClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseHigherTimeframeBias", GroupName = "Time Filters", Order = 47)]
        public bool UseHigherTimeframeBias { get; set; }
        #endregion
    }
}
