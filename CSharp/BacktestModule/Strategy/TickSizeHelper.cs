using System;

namespace BacktestModule.Strategy
{
    /// <summary>
    /// Taiwan Stock Exchange tick size utilities.
    /// Used by ExitManager, PositionManager, DataProcessor, and others.
    /// </summary>
    public static class TickSizeHelper
    {
        /// <summary>
        /// Returns the tick size for a given price, based on TWSE rules.
        /// </summary>
        public static double GetTickSize(double price)
        {
            if (price >= 1000) return 5.0;
            if (price >= 500) return 1.0;
            if (price >= 100) return 0.5;
            if (price >= 50) return 0.1;
            if (price >= 10) return 0.05;
            return 0.01;
        }

        /// <summary>
        /// Returns the tick size as a decimal for precise price calculations.
        /// </summary>
        public static decimal GetTickSizeDecimal(double price)
        {
            if (double.IsNaN(price)) return 0m;
            if (price >= 1000) return 5m;
            if (price >= 500) return 1m;
            if (price >= 100) return 0.5m;
            if (price >= 50) return 0.1m;
            if (price >= 10) return 0.05m;
            return 0.01m;
        }

        /// <summary>
        /// Adds (or subtracts if negative) a number of ticks to a price.
        /// </summary>
        public static double AddTicks(double price, int ticks)
        {
            return price + GetTickSize(price) * ticks;
        }

        /// <summary>
        /// Calculates the limit-up price: (previousClose * 1.10) rounded DOWN to tick.
        /// </summary>
        public static double CalculateLimitUp(double previousClose)
        {
            if (double.IsNaN(previousClose) || previousClose <= 0)
                return 0.0;

            decimal raw = (decimal)previousClose * 1.10m;
            decimal tick = GetTickSizeDecimal((double)raw);
            if (tick == 0m) return (double)raw;
            decimal limitUp = Math.Floor(raw / tick) * tick;
            return (double)Math.Round(limitUp, 2);
        }

        /// <summary>
        /// Calculates the limit-down price: (previousClose * 0.90) rounded UP to tick.
        /// Uses the same integer-division-based ceiling as the Python version.
        /// </summary>
        public static double CalculateLimitDown(double previousClose)
        {
            if (double.IsNaN(previousClose) || previousClose <= 0)
                return 0.0;

            decimal raw = (decimal)previousClose * 0.90m;
            decimal tick = GetTickSizeDecimal((double)raw);
            if (tick == 0m) return (double)raw;
            // Python: ((raw + tick - Decimal('0.01')) // tick) * tick
            decimal limitDown = ((raw + tick - 0.01m) / tick);
            limitDown = Math.Floor(limitDown) * tick;
            return (double)limitDown;
        }
    }
}
