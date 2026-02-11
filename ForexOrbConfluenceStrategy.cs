// NinjaTrader 8 strategy conversion of the ORB analyzer for futures.
// Built for futures instruments such as MNQ on a 5-minute chart.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class FuturesOrbConfluenceStrategy : Strategy
    {
        private enum TradeDirection
        {
            None,
            Long,
            Short
        }

        private class AnalysisResult
        {
            public TradeDirection Direction;
            public int Score;
            public string Recommendation;
            public double Entry;
            public double StopLoss;
            public double TakeProfit1;
            public double TakeProfit2;
            public double TakeProfit3;
            public int StopLossTicks;
            public int TakeProfit1Ticks;
            public int TakeProfit2Ticks;
            public int TakeProfit3Ticks;
            public int Contracts;
            public double RiskAmount;
            public double RewardTp1Target;
            public bool ShouldTrade;
            public string FactorsText;
        }

        private RSI rsi14;
        private MACD macd;
        private EMA ema20;
        private EMA ema50;

        private int sessionBarCount;
        private int tradesThisSession;
        private bool openingRangeReady;
        private double openingHigh;
        private double openingLow;

        private string logDirectory;
        private string logFilePath;
        private readonly object fileLock = new object();

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "OpeningRangeBars", GroupName = "ORB", Order = 1)]
        public int OpeningRangeBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "VolumeLookback", GroupName = "Factors", Order = 2)]
        public int VolumeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "SupportResistancePeriod", GroupName = "Factors", Order = 3)]
        public int SupportResistancePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "MomentumMultiplier", GroupName = "Factors", Order = 4)]
        public double MomentumMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "VolumeRatioThreshold", GroupName = "Factors", Order = 5)]
        public double VolumeRatioThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 8)]
        [Display(Name = "MinimumScoreToTrade", GroupName = "Risk", Order = 6)]
        public int MinimumScoreToTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, 25)]
        [Display(Name = "ContractsScore5", GroupName = "Risk", Order = 7)]
        public int ContractsScore5 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 25)]
        [Display(Name = "ContractsScore6", GroupName = "Risk", Order = 8)]
        public int ContractsScore6 { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "RiskDollarsScore5", GroupName = "Risk", Order = 9)]
        public double RiskDollarsScore5 { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "RiskDollarsScore6", GroupName = "Risk", Order = 10)]
        public double RiskDollarsScore6 { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "RewardTp1DollarsScore5", GroupName = "Risk", Order = 11)]
        public double RewardTp1DollarsScore5 { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "RewardTp1DollarsScore6", GroupName = "Risk", Order = 12)]
        public double RewardTp1DollarsScore6 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerSession", GroupName = "Execution", Order = 13)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AutoTrade", GroupName = "Execution", Order = 14)]
        public bool AutoTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VerboseOutput", GroupName = "Execution", Order = 15)]
        public bool VerboseOutput { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FuturesOrbConfluenceStrategy";
                Description = "8-factor ORB strategy for futures (MNQ-friendly) with contract sizing and tick-based SL/TP.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 60;

                OpeningRangeBars = 3;
                VolumeLookback = 20;
                SupportResistancePeriod = 10;
                MomentumMultiplier = 1.5;
                VolumeRatioThreshold = 1.2;
                MinimumScoreToTrade = 5;

                // MNQ-style defaults:
                // score >= 5 -> 1 contract, score >= 6 -> 2 contracts
                ContractsScore5 = 1;
                ContractsScore6 = 2;
                RiskDollarsScore5 = 20;
                RiskDollarsScore6 = 20;
                RewardTp1DollarsScore5 = 10;
                RewardTp1DollarsScore6 = 20;

                MaxTradesPerSession = 1;
                AutoTrade = false;
                VerboseOutput = true;
            }
            else if (State == State.Configure)
            {
                sessionBarCount = 0;
                tradesThisSession = 0;
                openingRangeReady = false;
                openingHigh = 0.0;
                openingLow = 0.0;
            }
            else if (State == State.DataLoaded)
            {
                rsi14 = RSI(14, 1);
                macd = MACD(12, 26, 9);
                ema20 = EMA(20);
                ema50 = EMA(50);

                InitializeLogging();

                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
                    Print("Warning: This strategy is designed for 5-minute bars.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            int minimumBars = Math.Max(BarsRequiredToTrade, Math.Max(VolumeLookback, SupportResistancePeriod) + 2);
            if (CurrentBar < minimumBars)
                return;

            UpdateOpeningRange();
            if (!openingRangeReady)
                return;

            TradeDirection direction = GetBreakoutDirection();
            if (direction == TradeDirection.None)
                return;

            AnalysisResult result = Analyze(direction);
            if (result == null)
                return;

            if (!result.ShouldTrade)
            {
                if (VerboseOutput)
                {
                    Print(string.Format(
                        "{0} {1} Score {2}/8 -> SKIP",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        GetDisplayInstrumentName(),
                        result.Score));
                }
                return;
            }

            LogResult(result);
            PrintRecommendation(result);

            if (AutoTrade && Position.MarketPosition == MarketPosition.Flat && tradesThisSession < MaxTradesPerSession)
                SubmitOrder(result);
        }

        private void UpdateOpeningRange()
        {
            if (Bars.IsFirstBarOfSession)
            {
                sessionBarCount = 0;
                tradesThisSession = 0;
                openingRangeReady = false;
                openingHigh = 0.0;
                openingLow = 0.0;
            }

            if (openingRangeReady)
                return;

            if (sessionBarCount == 0)
            {
                openingHigh = High[0];
                openingLow = Low[0];
            }
            else
            {
                openingHigh = Math.Max(openingHigh, High[0]);
                openingLow = Math.Min(openingLow, Low[0]);
            }

            sessionBarCount++;
            if (sessionBarCount >= OpeningRangeBars)
                openingRangeReady = true;
        }

        private TradeDirection GetBreakoutDirection()
        {
            double currentPrice = Close[0];
            if (currentPrice > openingHigh)
                return TradeDirection.Long;
            if (currentPrice < openingLow)
                return TradeDirection.Short;
            return TradeDirection.None;
        }

        private AnalysisResult Analyze(TradeDirection direction)
        {
            AnalysisResult result = new AnalysisResult();
            result.Direction = direction;
            result.Entry = Close[0];

            int score = 0;
            List<string> factors = new List<string>();

            // FACTOR 1: breakout (already true by caller)
            score += 1;
            factors.Add("1_breakout:OK " + (direction == TradeDirection.Long ? "LONG" : "SHORT"));

            // FACTOR 2: RSI
            double rsiValue = rsi14[0];
            bool rsiPass = (direction == TradeDirection.Long && rsiValue > 50.0 && rsiValue < 70.0)
                || (direction == TradeDirection.Short && rsiValue > 30.0 && rsiValue < 50.0);
            if (rsiPass)
            {
                score += 1;
                factors.Add("2_rsi:OK RSI " + rsiValue.ToString("0.0", CultureInfo.InvariantCulture));
            }
            else
            {
                factors.Add("2_rsi:NO RSI " + rsiValue.ToString("0.0", CultureInfo.InvariantCulture));
            }

            // FACTOR 3: MACD
            double macdLine = macd.Default[0];
            double signalLine = macd.Avg[0];
            double histogram = macd.Diff[0];
            bool macdPass = (direction == TradeDirection.Long && macdLine > signalLine && histogram > 0.0)
                || (direction == TradeDirection.Short && macdLine < signalLine && histogram < 0.0);
            if (macdPass)
            {
                score += 1;
                factors.Add("3_macd:OK MACD aligned");
            }
            else
            {
                factors.Add("3_macd:NO MACD no signal");
            }

            // FACTOR 4: EMA alignment
            double ema20Value = ema20[0];
            double ema50Value = ema50[0];
            bool emaPass = (direction == TradeDirection.Long && Close[0] > ema20Value && ema20Value > ema50Value)
                || (direction == TradeDirection.Short && Close[0] < ema20Value && ema20Value < ema50Value);
            if (emaPass)
            {
                score += 1;
                factors.Add("4_ema:OK EMA aligned");
            }
            else
            {
                factors.Add("4_ema:NO EMA not aligned");
            }

            // FACTOR 5: momentum
            bool momentumPass = false;
            if (CurrentBar > 0)
            {
                double currentRange = Math.Abs(Close[0] - Open[0]);
                double previousRange = Math.Abs(Close[1] - Open[1]);
                momentumPass = currentRange > previousRange * MomentumMultiplier;
            }
            if (momentumPass)
            {
                score += 1;
                factors.Add("5_momentum:OK Strong candle");
            }
            else
            {
                factors.Add("5_momentum:NO Weak candle");
            }

            // FACTOR 6: elevated volume
            double volumeRatio;
            bool elevatedVolume = CheckElevatedVolume(out volumeRatio);
            if (elevatedVolume)
            {
                score += 1;
                factors.Add("6_volume:OK Volume " + volumeRatio.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            }
            else
            {
                factors.Add("6_volume:NO Volume " + volumeRatio.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            }

            // FACTOR 7: fair value gap
            bool hasFvg = CheckFairValueGap();
            if (hasFvg)
            {
                score += 1;
                factors.Add("7_fvg:OK FVG gap");
            }
            else
            {
                factors.Add("7_fvg:NO No FVG");
            }

            // FACTOR 8: near support/resistance
            bool nearSupportResistance = CheckSupportResistance();
            if (nearSupportResistance)
            {
                score += 1;
                factors.Add("8_sr:OK Near S/R");
            }
            else
            {
                factors.Add("8_sr:NO Away S/R");
            }

            int contracts = GetContracts(score);
            double riskAmount;
            double rewardTp1Target;
            GetRiskReward(score, out riskAmount, out rewardTp1Target);

            result.Score = score;
            result.Contracts = contracts;
            result.RiskAmount = riskAmount;
            result.RewardTp1Target = rewardTp1Target;
            result.Recommendation = direction == TradeDirection.Long ? "BUY" : "SELL_SHORT";
            result.ShouldTrade = score >= MinimumScoreToTrade && contracts > 0 && riskAmount > 0.0 && rewardTp1Target > 0.0;
            result.FactorsText = string.Join(" | ", factors.ToArray());

            if (!result.ShouldTrade)
            {
                result.Recommendation = "SKIP";
                result.StopLoss = 0.0;
                result.TakeProfit1 = 0.0;
                result.TakeProfit2 = 0.0;
                result.TakeProfit3 = 0.0;
                result.StopLossTicks = 0;
                result.TakeProfit1Ticks = 0;
                result.TakeProfit2Ticks = 0;
                result.TakeProfit3Ticks = 0;
                return result;
            }

            CalculateStopTargets(
                result.Entry,
                direction,
                contracts,
                score,
                out result.StopLoss,
                out result.TakeProfit1,
                out result.TakeProfit2,
                out result.TakeProfit3,
                out result.StopLossTicks,
                out result.TakeProfit1Ticks,
                out result.TakeProfit2Ticks,
                out result.TakeProfit3Ticks);

            return result;
        }

        private bool CheckElevatedVolume(out double volumeRatio)
        {
            volumeRatio = 1.0;
            if (CurrentBar < VolumeLookback)
                return false;

            double currentVolume = Volume[0];
            double sum = 0.0;
            for (int i = 1; i < VolumeLookback; i++)
                sum += Volume[i];

            double average = sum / (VolumeLookback - 1);
            if (average <= 0.0)
                return false;

            volumeRatio = currentVolume / average;
            return volumeRatio > VolumeRatioThreshold;
        }

        private bool CheckFairValueGap()
        {
            if (CurrentBar < 2)
                return false;

            if (High[2] < Low[0])
                return true;
            if (Low[2] > High[0])
                return true;

            return false;
        }

        private bool CheckSupportResistance()
        {
            if (CurrentBar < SupportResistancePeriod)
                return false;

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;
            for (int i = 1; i < SupportResistancePeriod; i++)
            {
                recentHigh = Math.Max(recentHigh, High[i]);
                recentLow = Math.Min(recentLow, Low[i]);
            }

            double rangeSize = recentHigh - recentLow;
            if (rangeSize <= 0.0)
                return false;

            double current = Close[0];
            if (Math.Abs(current - recentHigh) < rangeSize * 0.2)
                return true;
            if (Math.Abs(current - recentLow) < rangeSize * 0.2)
                return true;

            return false;
        }

        private int GetContracts(int score)
        {
            if (score >= 6)
                return ContractsScore6;
            if (score >= 5)
                return ContractsScore5;
            return 0;
        }

        private void GetRiskReward(int score, out double risk, out double rewardTp1)
        {
            risk = 0.0;
            rewardTp1 = 0.0;

            if (score >= 6)
            {
                risk = RiskDollarsScore6;
                rewardTp1 = RewardTp1DollarsScore6;
                return;
            }

            if (score >= 5)
            {
                risk = RiskDollarsScore5;
                rewardTp1 = RewardTp1DollarsScore5;
            }
        }

        private double GetTickValue()
        {
            if (Instrument == null || Instrument.MasterInstrument == null)
                return 0.0;

            double pointValue = Instrument.MasterInstrument.PointValue;
            if (pointValue <= 0.0 || TickSize <= 0.0)
                return 0.0;

            return pointValue * TickSize;
        }

        private void CalculateStopTargets(
            double entryPrice,
            TradeDirection direction,
            int contracts,
            int score,
            out double sl,
            out double tp1,
            out double tp2,
            out double tp3,
            out int slTicks,
            out int tp1Ticks,
            out int tp2Ticks,
            out int tp3Ticks)
        {
            sl = 0.0;
            tp1 = 0.0;
            tp2 = 0.0;
            tp3 = 0.0;
            slTicks = 0;
            tp1Ticks = 0;
            tp2Ticks = 0;
            tp3Ticks = 0;

            double riskAmount;
            double rewardTp1Target;
            GetRiskReward(score, out riskAmount, out rewardTp1Target);

            double tickValue = GetTickValue();
            if (contracts <= 0 || tickValue <= 0.0)
                return;

            slTicks = Math.Max(1, (int)Math.Round(riskAmount / (tickValue * contracts), MidpointRounding.AwayFromZero));
            tp1Ticks = Math.Max(1, (int)Math.Round(rewardTp1Target / (tickValue * contracts), MidpointRounding.AwayFromZero));
            tp2Ticks = Math.Max(1, tp1Ticks * 2);
            tp3Ticks = Math.Max(1, tp1Ticks * 3);

            double slOffset = slTicks * TickSize;
            double tp1Offset = tp1Ticks * TickSize;
            double tp2Offset = tp2Ticks * TickSize;
            double tp3Offset = tp3Ticks * TickSize;

            if (direction == TradeDirection.Long)
            {
                sl = Instrument.MasterInstrument.RoundToTickSize(entryPrice - slOffset);
                tp1 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + tp1Offset);
                tp2 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + tp2Offset);
                tp3 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + tp3Offset);
            }
            else
            {
                sl = Instrument.MasterInstrument.RoundToTickSize(entryPrice + slOffset);
                tp1 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - tp1Offset);
                tp2 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - tp2Offset);
                tp3 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - tp3Offset);
            }
        }

        private void SubmitOrder(AnalysisResult result)
        {
            int quantity = Math.Max(1, result.Contracts);

            string signalName = string.Format(
                "ORB_{0}_{1}",
                result.Direction == TradeDirection.Long ? "L" : "S",
                CurrentBar);

            SetStopLoss(signalName, CalculationMode.Price, result.StopLoss, false);
            SetProfitTarget(signalName, CalculationMode.Price, result.TakeProfit1);

            if (result.Direction == TradeDirection.Long)
                EnterLong(quantity, signalName);
            else
                EnterShort(quantity, signalName);

            tradesThisSession++;
        }

        private void PrintRecommendation(AnalysisResult result)
        {
            if (!VerboseOutput)
                return;

            string instrumentName = GetDisplayInstrumentName();
            string direction = result.Direction == TradeDirection.Long ? "LONG" : "SHORT";
            string timestamp = Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            Print("==================================================");
            Print(string.Format("{0} | {1} | {2}", timestamp, instrumentName, direction));
            Print(string.Format("Score: {0}/8 -> TRADE ({1})", result.Score, result.Recommendation));
            Print(string.Format("Contracts: {0}", result.Contracts));
            Print(string.Format("Entry: {0}", result.Entry.ToString("0.00####", CultureInfo.InvariantCulture)));
            Print(string.Format("SL: {0} ({1} ticks, ${2} risk)",
                result.StopLoss.ToString("0.00####", CultureInfo.InvariantCulture),
                result.StopLossTicks.ToString(CultureInfo.InvariantCulture),
                result.RiskAmount.ToString("0.00", CultureInfo.InvariantCulture)));
            Print(string.Format("TP1: {0} ({1} ticks, ${2} target)",
                result.TakeProfit1.ToString("0.00####", CultureInfo.InvariantCulture),
                result.TakeProfit1Ticks.ToString(CultureInfo.InvariantCulture),
                result.RewardTp1Target.ToString("0.00", CultureInfo.InvariantCulture)));
            Print(string.Format("TP2: {0} ({1} ticks, ${2} target)",
                result.TakeProfit2.ToString("0.00####", CultureInfo.InvariantCulture),
                result.TakeProfit2Ticks.ToString(CultureInfo.InvariantCulture),
                (result.RewardTp1Target * 2.0).ToString("0.00", CultureInfo.InvariantCulture)));
            Print(string.Format("TP3: {0} ({1} ticks, ${2} target)",
                result.TakeProfit3.ToString("0.00####", CultureInfo.InvariantCulture),
                result.TakeProfit3Ticks.ToString(CultureInfo.InvariantCulture),
                (result.RewardTp1Target * 3.0).ToString("0.00", CultureInfo.InvariantCulture)));
            Print(string.Format("Factors: {0}", result.FactorsText));
            Print("==================================================");
        }

        private void InitializeLogging()
        {
            try
            {
                logDirectory = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "trading_logs");
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                logFilePath = Path.Combine(logDirectory, "futures_orb_trading_log.csv");
                if (!File.Exists(logFilePath))
                {
                    string header = "Date,Time,Instrument,Direction,Score,Recommendation,Contracts,Entry,SL,SL_Ticks,TP1,TP1_Ticks,TP2,TP2_Ticks,TP3,TP3_Ticks,Risk_$,Reward_TP1_$,Reward_TP2_$,RiskReward_Ratio,Factors";
                    File.WriteAllText(logFilePath, header + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Print("Logging initialization error: " + ex.Message);
            }
        }

        private void LogResult(AnalysisResult result)
        {
            if (string.IsNullOrEmpty(logFilePath))
                return;

            try
            {
                double tickValue = GetTickValue();
                if (tickValue <= 0.0)
                    return;

                double rewardTp1Dollars = Math.Round(result.TakeProfit1Ticks * tickValue * result.Contracts, 2);
                double rewardTp2Dollars = Math.Round(result.TakeProfit2Ticks * tickValue * result.Contracts, 2);
                double rrRatio = result.RiskAmount > 0.0 ? Math.Round(rewardTp1Dollars / result.RiskAmount, 2) : 0.0;

                string[] row = new string[]
                {
                    Time[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Time[0].ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    EscapeCsv(GetDisplayInstrumentName()),
                    result.Direction == TradeDirection.Long ? "LONG" : "SHORT",
                    result.Score.ToString(CultureInfo.InvariantCulture),
                    result.Recommendation,
                    result.Contracts.ToString(CultureInfo.InvariantCulture),
                    result.Entry.ToString("0.00####", CultureInfo.InvariantCulture),
                    result.StopLoss.ToString("0.00####", CultureInfo.InvariantCulture),
                    result.StopLossTicks.ToString(CultureInfo.InvariantCulture),
                    result.TakeProfit1.ToString("0.00####", CultureInfo.InvariantCulture),
                    result.TakeProfit1Ticks.ToString(CultureInfo.InvariantCulture),
                    result.TakeProfit2.ToString("0.00####", CultureInfo.InvariantCulture),
                    result.TakeProfit2Ticks.ToString(CultureInfo.InvariantCulture),
                    result.TakeProfit3.ToString("0.00####", CultureInfo.InvariantCulture),
                    result.TakeProfit3Ticks.ToString(CultureInfo.InvariantCulture),
                    result.RiskAmount.ToString("0.00", CultureInfo.InvariantCulture),
                    rewardTp1Dollars.ToString("0.00", CultureInfo.InvariantCulture),
                    rewardTp2Dollars.ToString("0.00", CultureInfo.InvariantCulture),
                    "1:" + rrRatio.ToString("0.00", CultureInfo.InvariantCulture),
                    EscapeCsv(result.FactorsText)
                };

                string line = string.Join(",", row);
                lock (fileLock)
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Print("CSV logging error: " + ex.Message);
            }
        }

        private string GetDisplayInstrumentName()
        {
            if (Instrument == null)
                return "UNKNOWN";

            string fullName = Instrument.FullName;
            if (string.IsNullOrEmpty(fullName))
                fullName = Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "UNKNOWN";

            return fullName;
        }

        private string EscapeCsv(string value)
        {
            if (value == null)
                return "\"\"";

            string escaped = value.Replace("\"", "\"\"");
            return "\"" + escaped + "\"";
        }
    }
}
