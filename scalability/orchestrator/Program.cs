using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;

#if _WINDOWS
using System.Security.Principal;
#endif // _WINDOWS

using System.Threading;
using System.Threading.Tasks;


using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

using Common;
using System.Runtime.InteropServices;

namespace orchestrator
{
    class Program
    {
        static int NUM_CORES_MAX = 4;
        static int NUM_CORES_MIN = 1; // CHANGES THESE TO WHATEVER YOU WANT

        static int num_event_count = 0;
        static int cur_core_count;

        static int NUM_THREADS = -1;

        static int EVENT_RATE = -1;
        static TimeSpan DURATION = TimeSpan.FromMinutes(1);

        static string READER_TYPE = "EPES";

        static int EVENT_SIZE = 100;

        static bool SIMULATE_SLOW_READ = false;

        static BurstPattern burstPattern = BurstPattern.DRIP;

        static Dictionary<int, (long, long)> eventCounts;

        static Action<object> threadProc = null;

        static void Main(string[] args)
        {
            eventCounts = new Dictionary<int, (long, long)>();

#if _WINDOWS
            // Try to run in admin mode in Windows
            bool isElevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            if (!isElevated)
            {
                Console.WriteLine("Must run in root/admin mode");
                return;
            }
#endif // _WINDOWS
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: dotnet run [path-to-corescaletest.exe] [eventsize] [core min] [core max] [numThreads] [reader type (stream|EPES)] [event rate] [burst pattern] [simulate slow read] [duration in sec]");
                return;
            }
            if (!File.Exists(args[0]))
            {
                Console.WriteLine("Not a file " + args[0]);
            }

            EVENT_SIZE = args.Length > 1 ? Int32.Parse(args[1]) : 100;
            NUM_CORES_MIN = args.Length > 2 ? Int32.Parse(args[2]) : 0;
            NUM_CORES_MAX = args.Length > 3 ? Int32.Parse(args[3]) : 4;
            NUM_THREADS = args.Length > 4 ? Int32.Parse(args[4]) : -1;
            if (args.Length > 5)
            {
                READER_TYPE = args[5] switch
                {
                    "stream" => "stream",
                    "EPES" => "EPES",
                    _ => "EPES"
                };
            }

            EVENT_RATE = args.Length > 6 ? Int32.Parse(args[6]) : -1;
            burstPattern = args.Length > 7 ? args[7].ToBurstPattern() : BurstPattern.NONE;
            SIMULATE_SLOW_READ = args.Length > 8 ? bool.Parse(args[8]) : false;
            DURATION = args.Length > 9 ? 
                (int.TryParse(args[9], out int nSeconds) && nSeconds > 0 ? TimeSpan.FromSeconds(nSeconds) : TimeSpan.FromSeconds(60)) :
                TimeSpan.FromSeconds(60);

            if (READER_TYPE == "stream")
                threadProc = UseFS;
            else
                threadProc = UseEPES;

            if (EVENT_RATE == -1 && burstPattern != BurstPattern.NONE)
                throw new ArgumentException("Must have burst pattern of NONE if rate is -1");

            Console.WriteLine($"Configuration: event_size={EVENT_SIZE}, min_cores={NUM_CORES_MIN}, max_cores={NUM_CORES_MAX}, num_threads={NUM_THREADS}, reader={READER_TYPE}, event_rate={EVENT_RATE * NUM_THREADS}, burst_pattern={burstPattern.ToString()}, slow_reader={SIMULATE_SLOW_READ}, duration={DURATION}");

            Measure(args[0]);
        }
        
        /// <summary>
        /// This uses EventPipeEventSource's Stream constructor to parse the events real-time.
        /// It then returns the number of events read.
        /// </summary>
        static void UseEPES(object arg)
        {
            int pid = (int)arg;
            int eventsRead = 0;
            var sw = new Stopwatch();
            var interval = TimeSpan.FromSeconds(0.75);
            DiagnosticsClient client = new DiagnosticsClient(pid);
            while (!client.CheckTransport())
            {
                Console.WriteLine("still unable to talk");
                Thread.Sleep(50);
            }
            EventPipeSession session = client.StartEventPipeSession(new EventPipeProvider("MySource", EventLevel.Verbose));

            Console.WriteLine("session open");
            EventPipeEventSource epes = new EventPipeEventSource(session.EventStream);
            epes.Dynamic.All += (TraceEvent data) => {
                eventsRead += 1;
                if (SIMULATE_SLOW_READ)
                {
                    if (sw.Elapsed > interval)
                    {
                        Thread.Sleep(250);
                        sw.Reset();
                    }
                }
            };
            if (SIMULATE_SLOW_READ)
                sw.Start();
            epes.Process();
            Console.WriteLine("Used realtime.");
            Console.WriteLine("Read total: " + eventsRead.ToString());
            Console.WriteLine("Dropped total: " + epes.EventsLost.ToString());

            eventCounts[cur_core_count] = (eventsRead, epes.EventsLost);
        }

        /// <summary>
        /// This uses CopyToAsync to copy the trace into a filesystem first, and then uses EventPipeEventSource
        /// on the file to post-process it and return the total # of events read.
        /// </summary>
        static void UseFS(object arg)
        {
            int pid = (int)arg;
            int eventsRead = 0;
            const string fileName = "./temp.nettrace";
            DiagnosticsClient client = new DiagnosticsClient(pid);
            while (!client.CheckTransport())
            {
                Console.WriteLine("still unable to talk");
                Thread.Sleep(50);
            }
            EventPipeSession session = client.StartEventPipeSession(new EventPipeProvider("MySource", EventLevel.Verbose));

            Console.WriteLine("session open");

            using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                Task copyTask = session.EventStream.CopyToAsync(fs);
                while(!copyTask.Wait(100));
            }
            EventPipeEventSource epes = new EventPipeEventSource(fileName);
            epes.Dynamic.All += (TraceEvent data) => {
                eventsRead += 1;
            };
            epes.Process();
            Console.WriteLine("Used post processing.");
            Console.WriteLine("Read total: " + eventsRead.ToString());
            Console.WriteLine("Dropped total: " + epes.EventsLost.ToString());

            eventCounts[cur_core_count] = (eventsRead, epes.EventsLost);
        }

        static void Measure(string fileName)
        {
            for (int num_cores = NUM_CORES_MIN; num_cores <= NUM_CORES_MAX; num_cores++)
            {
                num_event_count = 0;
                cur_core_count = num_cores;

                Console.WriteLine("========================================================");
                Console.WriteLine("Starting run with proc count " + num_cores.ToString());

                Process eventWritingProc = new Process();
                eventWritingProc.StartInfo.FileName = fileName;
                eventWritingProc.StartInfo.Arguments = $"{(NUM_THREADS == -1 ? num_cores.ToString() : NUM_THREADS.ToString())} {EVENT_SIZE} {EVENT_RATE} {burstPattern} {(int)DURATION.TotalSeconds}";
                eventWritingProc.StartInfo.UseShellExecute = false;
                eventWritingProc.StartInfo.RedirectStandardInput = true;
                eventWritingProc.StartInfo.Environment["COMPlus_StressLog"] = "1";
                eventWritingProc.StartInfo.Environment["COMPlus_LogFacility"] = "2000";
                eventWritingProc.StartInfo.Environment["COMPlus_LogLevel"] = "8";
                eventWritingProc.StartInfo.Environment["COMPlus_StressLogSize"] = "0x1000000";
                eventWritingProc.Start();

                Console.WriteLine($"Executing: {eventWritingProc.StartInfo.FileName} {eventWritingProc.StartInfo.Arguments}");
                // Set affinity and priority
                long affinityMask = 0;
                for (int j = 0; j < num_cores; j++)
                {
                    affinityMask |= (1 << j);
                }
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    eventWritingProc.ProcessorAffinity = (IntPtr)((long)eventWritingProc.ProcessorAffinity & affinityMask);
                    eventWritingProc.PriorityClass = ProcessPriorityClass.RealTime; // Set the process priority to highest possible
                }

                // Start listening to the event.


                CancellationTokenSource  ct = new CancellationTokenSource();
                Thread t = new Thread(() => threadProc(eventWritingProc.Id));
                t.Start();

                Console.WriteLine("Press <enter> to start test");
                Console.ReadLine();

                // start the target process
                StreamWriter writer = eventWritingProc.StandardInput;
                writer.WriteLine("\r\n");
                eventWritingProc.WaitForExit();

                t.Join();

                t = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                Console.WriteLine("Done with proc count " + num_cores.ToString());
                Console.WriteLine("========================================================");

            }
            
            Console.WriteLine("**** Summary ****");
            for (int i = NUM_CORES_MIN; i <= NUM_CORES_MAX; i++)
            {
                Console.WriteLine($"{i} cores: {eventCounts[i].Item1:N} events collected, {eventCounts[i].Item2:N} events dropped ({100 * ((double)eventCounts[i].Item1 / (double)((long)eventCounts[i].Item1 + eventCounts[i].Item2))}%)");
                Console.WriteLine($"\t({(long)eventCounts[i].Item1/(int)DURATION.TotalSeconds:N} events/s) ({((long)eventCounts[i].Item1*EVENT_SIZE*sizeof(char))/(int)DURATION.TotalSeconds:N} bytes/s)");
            }
        }
    }
}
