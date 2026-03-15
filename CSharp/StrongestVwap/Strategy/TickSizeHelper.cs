using System;

namespace StrongestVwap.Strategy
{
    public static class TickSizeHelper
    {
        public static double GetTickSize(double price)
        {
            if (price >= 1000) return 5.0;
            if (price >= 500) return 1.0;
            if (price >= 100) return 0.5;
            if (price >= 50) return 0.1;
            if (price >= 10) return 0.05;
            return 0.01;
        }

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

        public static double CeilToTick(double price)
        {
            if (double.IsNaN(price) || price <= 0) return 0;
            decimal dp = (decimal)price;
            decimal tick = GetTickSizeDecimal(price);
            if (tick == 0m) return price;
            decimal result = Math.Ceiling(dp / tick) * tick;
            return (double)Math.Round(result, 2);
        }

        public static double FloorToTick(double price)
        {
            if (double.IsNaN(price) || price <= 0) return 0;
            decimal dp = (decimal)price;
            decimal tick = GetTickSizeDecimal(price);
            if (tick == 0m) return price;
            decimal result = Math.Floor(dp / tick) * tick;
            return (double)Math.Round(result, 2);
        }

        public static double CalculateLimitUp(double previousClose)
        {
            if (double.IsNaN(previousClose) || previousClose <= 0) return 0;
            decimal raw = (decimal)previousClose * 1.10m;
            decimal tick = GetTickSizeDecimal((double)raw);
            if (tick == 0m) return (double)raw;
            return (double)Math.Round(Math.Floor(raw / tick) * tick, 2);
        }
    }
}
