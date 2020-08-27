using System;
using System.Diagnostics.Tracing;
using System.Threading;

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
        static void ThreadProc()
        {
            while(true)
            {
                if (finished)
                {
                    break;
                }
                MySource.Log.FireEvent();
            }
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: dotnet run [number of threads] [event size]");
                return;
            }

            int numThreads = args.Length > 0 ? Int32.Parse(args[0]) : 4;
            int eventSize = args.Length > 1 ? Int32.Parse(args[1]) : 100;

            Thread[] threads = new Thread[numThreads];

            for (int i = 0; i < numThreads; i++)
            {
                threads[i] = new Thread(ThreadProc);
            }

            MySource.s_Payload = new String('a', eventSize);

            Console.WriteLine($"Running {numThreads} threads with event size {eventSize * sizeof(char):N} bytes");
            Console.ReadLine();

            for (int i = 0; i < numThreads; i++)
            {
                threads[i].Start();
            }
            
            Console.WriteLine("Sleeping for 1 minutes");
            Thread.Sleep(1 * 60 * 1000);
            finished = true;
            
            Console.WriteLine("Done. Goodbye!");
        }
    }
}
