// NinjaTrader 8 strategy conversion of the Python ORB analyzer.
// Run this strategy on a 5-minute chart for each instrument you want to evaluate.

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
    public class ForexOrbConfluenceStrategy : Strategy
    {
        private enum PairType
        {
            Standard,
            Jpy,
            Gold
        }

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
            public string OrderType;
            public double Entry;
            public double StopLoss;
            public double TakeProfit1;
            public double TakeProfit2;
            public double TakeProfit3;
            public double StopLossPips;
            public double TakeProfit1Pips;
            public double TakeProfit2Pips;
            public double TakeProfit3Pips;
            public double LotSize;
            public double RiskAmount;
            public double ProfitTp1Amount;
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
        [Range(1, 1000000)]
        [Display(Name = "UnitsPerLot", GroupName = "Risk", Order = 7)]
        public int UnitsPerLot { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerSession", GroupName = "Risk", Order = 8)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AutoTrade", GroupName = "Execution", Order = 9)]
        public bool AutoTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VerboseOutput", GroupName = "Execution", Order = 10)]
        public bool VerboseOutput { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ForexOrbConfluenceStrategy";
                Description = "8-factor ORB strategy with dynamic lot sizing and pip-based SL/TP.";
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
                UnitsPerLot = 100000;
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
                {
                    Print("Warning: This strategy is designed for 5-minute bars.");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
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
                        GetDisplayPairName(),
                        result.Score));
                }
                return;
            }

            LogResult(result);
            PrintRecommendation(result);

            if (AutoTrade && Position.MarketPosition == MarketPosition.Flat && tradesThisSession < MaxTradesPerSession)
            {
                SubmitOrder(result);
            }
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

            score += 1;
            factors.Add("1_breakout:OK " + (direction == TradeDirection.Long ? "LONG" : "SHORT"));

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

            PairType pairType = GetPairType();
            double lotSize = GetLotSize(pairType, score);
            double riskAmount;
            double profitTp1;
            GetRiskReward(pairType, score, out riskAmount, out profitTp1);

            result.Score = score;
            result.LotSize = lotSize;
            result.RiskAmount = riskAmount;
            result.ProfitTp1Amount = profitTp1;
            result.OrderType = direction == TradeDirection.Long ? "Buy Limit" : "Sell Limit";
            result.ShouldTrade = score >= MinimumScoreToTrade && lotSize > 0.0;
            result.FactorsText = string.Join(" | ", factors.ToArray());

            if (!result.ShouldTrade)
            {
                result.OrderType = "SKIP";
                result.StopLoss = 0.0;
                result.TakeProfit1 = 0.0;
                result.TakeProfit2 = 0.0;
                result.TakeProfit3 = 0.0;
                result.StopLossPips = 0.0;
                result.TakeProfit1Pips = 0.0;
                result.TakeProfit2Pips = 0.0;
                result.TakeProfit3Pips = 0.0;
                return result;
            }

            CalculateStopTargets(
                result.Entry,
                direction,
                pairType,
                lotSize,
                score,
                out result.StopLoss,
                out result.TakeProfit1,
                out result.TakeProfit2,
                out result.TakeProfit3,
                out result.StopLossPips,
                out result.TakeProfit1Pips,
                out result.TakeProfit2Pips,
                out result.TakeProfit3Pips);

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

        private PairType GetPairType()
        {
            string symbol = Instrument.MasterInstrument.Name.ToUpperInvariant();
            string normalized = symbol.Replace("/", string.Empty).Replace(" ", string.Empty);

            if (normalized.Contains("XAU") || normalized.StartsWith("GC"))
                return PairType.Gold;
            if (normalized.Contains("JPY"))
                return PairType.Jpy;

            return PairType.Standard;
        }

        private double GetLotSize(PairType pairType, int score)
        {
            if (score >= 6)
            {
                if (pairType == PairType.Gold)
                    return 1.25;
                return 0.25;
            }

            if (score >= 5)
            {
                if (pairType == PairType.Gold)
                    return 0.75;
                return 0.15;
            }

            return 0.0;
        }

        private void GetRiskReward(PairType pairType, int score, out double risk, out double profitTp1)
        {
            risk = 0.0;
            profitTp1 = 0.0;

            if (score < 5)
                return;

            if (pairType == PairType.Gold)
            {
                risk = 50.0;
                profitTp1 = score >= 6 ? 70.0 : 50.0;
                return;
            }

            risk = 20.0;
            profitTp1 = score >= 6 ? 20.0 : 10.0;
        }

        private double GetPipValue(PairType pairType)
        {
            if (pairType == PairType.Gold)
                return 1.0;

            return 10.0;
        }

        private double GetPipMultiplier(PairType pairType)
        {
            if (pairType == PairType.Standard)
                return 0.0001;

            return 0.01;
        }

        private void CalculateStopTargets(
            double entryPrice,
            TradeDirection direction,
            PairType pairType,
            double lotSize,
            int score,
            out double sl,
            out double tp1,
            out double tp2,
            out double tp3,
            out double slPips,
            out double tp1Pips,
            out double tp2Pips,
            out double tp3Pips)
        {
            double riskAmount;
            double profitTp1;
            GetRiskReward(pairType, score, out riskAmount, out profitTp1);

            double pipValue = GetPipValue(pairType);
            double pipMultiplier = GetPipMultiplier(pairType);

            slPips = Math.Round(riskAmount / (pipValue * lotSize), 1);
            tp1Pips = Math.Round(profitTp1 / (pipValue * lotSize), 1);
            tp2Pips = Math.Round((profitTp1 * 2.0) / (pipValue * lotSize), 1);
            tp3Pips = Math.Round((profitTp1 * 3.0) / (pipValue * lotSize), 1);

            if (direction == TradeDirection.Long)
            {
                sl = Instrument.MasterInstrument.RoundToTickSize(entryPrice - (slPips * pipMultiplier));
                tp1 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + (tp1Pips * pipMultiplier));
                tp2 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + (tp2Pips * pipMultiplier));
                tp3 = Instrument.MasterInstrument.RoundToTickSize(entryPrice + (tp3Pips * pipMultiplier));
            }
            else
            {
                sl = Instrument.MasterInstrument.RoundToTickSize(entryPrice + (slPips * pipMultiplier));
                tp1 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - (tp1Pips * pipMultiplier));
                tp2 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - (tp2Pips * pipMultiplier));
                tp3 = Instrument.MasterInstrument.RoundToTickSize(entryPrice - (tp3Pips * pipMultiplier));
            }
        }

        private void SubmitOrder(AnalysisResult result)
        {
            int quantity = (int)Math.Round(result.LotSize * UnitsPerLot, MidpointRounding.AwayFromZero);
            if (quantity <= 0)
                quantity = 1;

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

            string pair = GetDisplayPairName();
            string direction = result.Direction == TradeDirection.Long ? "LONG" : "SHORT";
            string timestamp = Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            Print("==================================================");
            Print(string.Format("{0} | {1} | {2}", timestamp, pair, direction));
            Print(string.Format("Score: {0}/8 -> TRADE ({1})", result.Score, result.OrderType));
            Print(string.Format("Lot: {0}", result.LotSize.ToString("0.00", CultureInfo.InvariantCulture)));
            Print(string.Format("Entry: {0}", result.Entry.ToString("0.0000", CultureInfo.InvariantCulture)));
            Print(string.Format("SL: {0} ({1} pips, ${2} risk)",
                result.StopLoss.ToString("0.0000", CultureInfo.InvariantCulture),
                result.StopLossPips.ToString("0.0", CultureInfo.InvariantCulture),
                result.RiskAmount.ToString("0", CultureInfo.InvariantCulture)));
            Print(string.Format("TP1: {0} ({1} pips, ${2} profit)",
                result.TakeProfit1.ToString("0.0000", CultureInfo.InvariantCulture),
                result.TakeProfit1Pips.ToString("0.0", CultureInfo.InvariantCulture),
                result.ProfitTp1Amount.ToString("0", CultureInfo.InvariantCulture)));
            Print(string.Format("TP2: {0} ({1} pips, ${2} profit)",
                result.TakeProfit2.ToString("0.0000", CultureInfo.InvariantCulture),
                result.TakeProfit2Pips.ToString("0.0", CultureInfo.InvariantCulture),
                (result.ProfitTp1Amount * 2.0).ToString("0", CultureInfo.InvariantCulture)));
            Print(string.Format("TP3: {0} ({1} pips, ${2} profit)",
                result.TakeProfit3.ToString("0.0000", CultureInfo.InvariantCulture),
                result.TakeProfit3Pips.ToString("0.0", CultureInfo.InvariantCulture),
                (result.ProfitTp1Amount * 3.0).ToString("0", CultureInfo.InvariantCulture)));
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

                logFilePath = Path.Combine(logDirectory, "orb_trading_log.csv");
                if (!File.Exists(logFilePath))
                {
                    string header = "Date,Time,Pair,Direction,Score,Recommendation,Lot,Entry,SL,SL_Pips,TP1,TP1_Pips,TP2,TP2_Pips,TP3,TP3_Pips,Risk_$,Reward_TP1_$,Reward_TP2_$,RiskReward_Ratio,Factors";
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
                PairType pairType = GetPairType();
                double pipValue = GetPipValue(pairType);

                double rewardTp1Dollars = Math.Round(result.TakeProfit1Pips * pipValue * result.LotSize, 2);
                double rewardTp2Dollars = Math.Round(result.TakeProfit2Pips * pipValue * result.LotSize, 2);
                double rrRatio = result.RiskAmount > 0.0 ? Math.Round(rewardTp1Dollars / result.RiskAmount, 2) : 0.0;

                string[] row = new string[]
                {
                    Time[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Time[0].ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    GetDisplayPairName(),
                    result.Direction == TradeDirection.Long ? "LONG" : "SHORT",
                    result.Score.ToString(CultureInfo.InvariantCulture),
                    result.OrderType,
                    result.LotSize.ToString("0.00", CultureInfo.InvariantCulture),
                    result.Entry.ToString("0.0000", CultureInfo.InvariantCulture),
                    result.StopLoss.ToString("0.0000", CultureInfo.InvariantCulture),
                    result.StopLossPips.ToString("0.0", CultureInfo.InvariantCulture),
                    result.TakeProfit1.ToString("0.0000", CultureInfo.InvariantCulture),
                    result.TakeProfit1Pips.ToString("0.0", CultureInfo.InvariantCulture),
                    result.TakeProfit2.ToString("0.0000", CultureInfo.InvariantCulture),
                    result.TakeProfit2Pips.ToString("0.0", CultureInfo.InvariantCulture),
                    result.TakeProfit3.ToString("0.0000", CultureInfo.InvariantCulture),
                    result.TakeProfit3Pips.ToString("0.0", CultureInfo.InvariantCulture),
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

        private string GetDisplayPairName()
        {
            string raw = Instrument.MasterInstrument.Name.ToUpperInvariant().Replace(" ", string.Empty);
            string normalized = raw.Replace("/", string.Empty);
            if (normalized.Length == 6)
                return normalized.Substring(0, 3) + "/" + normalized.Substring(3, 3);
            return raw;
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
