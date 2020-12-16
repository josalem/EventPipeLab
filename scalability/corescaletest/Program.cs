using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Common;

namespace corescaletest
{
    class MySource : EventSource
    {
        public static MySource Log = new MySource();
        public static string s_SmallPayload = new String('a', 100);
        public static string s_BigPayload = new String('a', 10000);
        public static string s_Payload = new String('a', 100);

        public void FireSmallEvent() { WriteEvent(1, s_SmallPayload); }
        public void FireBigEvent() { WriteEvent(1, s_BigPayload); }
        public void FireEvent() => WriteEvent(1, s_Payload);
    }

    class Program
    {
        private static bool finished = false;
        private static int eventRate = -1;
        private static BurstPattern burstPattern = BurstPattern.NONE;
        private static Action threadProc = null;
        private static Func<Action> makeThreadProc = () =>
        {
            Action burst = BurstPatternMethods.Burst(burstPattern, eventRate, MySource.Log.FireEvent, BurstPatternMethods.BusySleepAction);
            return () => { while (!finished) { burst(); } };
        };

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: dotnet run [number of threads] [event size] [event rate #/sec] [burst pattern] [duration in sec]");
                return;
            }

            int numThreads = args.Length > 0 ? Int32.Parse(args[0]) : 4;
            int eventSize = args.Length > 1 ? Int32.Parse(args[1]) : 100;
            eventRate = args.Length > 2 ? Int32.Parse(args[2]) : -1;
            burstPattern = args.Length > 3 ? args[3].ToBurstPattern() : BurstPattern.NONE;
            TimeSpan duration = args.Length > 4 ? 
                (int.TryParse(args[4], out int nSeconds) && nSeconds > 0 ? TimeSpan.FromSeconds(nSeconds) : TimeSpan.FromSeconds(60)) :
                TimeSpan.FromSeconds(60);

            MySource.s_Payload = new String('a', eventSize);

            threadProc = makeThreadProc();

            Thread[] threads = new Thread[numThreads];

            for (int i = 0; i < numThreads; i++)
            {
                threads[i] = new Thread(() => threadProc());
            }

            Console.WriteLine($"Running - Threads: {numThreads}, EventSize: {eventSize * sizeof(char):N} bytes, EventRate: {eventRate * numThreads} events/sec, duration: {duration.TotalSeconds}s");
            Console.ReadLine();

            for (int i = 0; i < numThreads; i++)
            {
                threads[i].Start();
            }
            
            Console.WriteLine($"Sleeping for {duration.TotalSeconds} seconds");
            Thread.Sleep(duration);
            finished = true;
            
            Console.WriteLine("Done. Goodbye!");
        }
    }
}
