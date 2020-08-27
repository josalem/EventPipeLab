using System;
using System.Threading;

namespace Common
{
    // Each pattern sends N events per second, but varies the frequency
    // across that second to simulate various burst patterns.
    public enum BurstPattern : int
    {
        DRIP  = 0, // (default) to send N events per second, sleep 1000/N ms between sends
        BOLUS = 1, // each second send N events then stop
        HEAVY_DRIP = 2, // each second send M bursts of N/M events
        NONE = -1 // no burst pattern
    }

    public static class BurstPatternMethods
    {
        public static BurstPattern ToBurstPattern(this int n)
        {
            return n > 2 || n < -1 ? BurstPattern.NONE : (BurstPattern)n;
        }

        public static BurstPattern ToBurstPattern(this string str)
        {
            if (Int32.TryParse(str, out int result))
                return result.ToBurstPattern();
            else
            {
                return str.ToLowerInvariant() switch
                {
                    "drip" => BurstPattern.DRIP,
                    "bolus" => BurstPattern.BOLUS,
                    "heavy_drip" => BurstPattern.HEAVY_DRIP,
                    "none" => BurstPattern.NONE,
                    _ => BurstPattern.NONE
                };
            }
        }

        public static string ToString(this BurstPattern burstPattern) => burstPattern switch
            {
                BurstPattern.DRIP => "DRIP",
                BurstPattern.BOLUS => "BOLUS",
                BurstPattern.HEAVY_DRIP => "HEAVY_DRIP",
                BurstPattern.NONE => "NONE",
                _ => "UNKOWN"
            };

        /// <summary>
        /// Invoke <param name="method"/> <param name="rate"/> times in 1 second using the <param name="pattern"/> provided
        /// </summary>
        public static Action Burst(BurstPattern pattern, int rate, Action method)
        {
            if (rate == 0)
                throw new ArgumentException("Rate cannot be 0");

            switch (pattern)
            {
                case BurstPattern.DRIP:
                {
                    int sleepInMs = (int)Math.Floor(1000.0/rate);
                    return () => { method(); Thread.Sleep(sleepInMs); };
                }
                case BurstPattern.BOLUS:
                {
                    return () => 
                    {
                        DateTime start = DateTime.Now;
                        for (int i = 0; i < rate; i++) { method(); } 
                        TimeSpan duration = DateTime.Now - start;
                        if (duration.TotalSeconds < 1)
                            Thread.Sleep(1000 - (int)Math.Floor(duration.TotalMilliseconds));
                    };
                }
                case BurstPattern.HEAVY_DRIP:
                {
                    int nDrips = 4;
                    int nEventsPerDrip = (int)Math.Floor((double)rate / nDrips);
                    int sleepInMs = (int)Math.Floor((1000.0 / rate) / nDrips);
                    return () =>
                    {
                        for (int i = 0; i < nDrips; i++)
                        {
                            for (int j = 0; j < nEventsPerDrip; i++)
                                method();
                            Thread.Sleep(sleepInMs);
                        }
                    };
                }
                case BurstPattern.NONE:
                {
                    return method;
                }
                default:
                    throw new ArgumentException("Unkown burst pattern");
            }
        }
    }
}