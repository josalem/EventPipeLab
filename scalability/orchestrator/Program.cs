using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

using Common;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

using Process = System.Diagnostics.Process;

namespace orchestrator
{
    public enum ReaderType
    {
        Stream,
        EventPipeEventSource
    }

    class Program
    {
        static private Dictionary<int, (long, long, TimeSpan)> EventCountDict = new Dictionary<int, (long, long, TimeSpan)>();
        static private int CurrentCoreCount = -1;

        delegate Task<int> RootCommandHandler(
            IConsole console,
            CancellationToken ct,
            FileInfo corescaletestPath,
            int eventSize,
            int eventRate,
            BurstPattern burstPattern,
            ReaderType readerType,
            int slowReader,
            int duration,
            int minCore,
            int maxCore,
            int threads,
            int eventCount,
            bool rundown,
            int bufferSize,
            bool pause);

        // TODO: Add iteration mode to run the same test multiple times and get the average, median, stddev of the data

        static async Task<int> Main(string[] args)
        {

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
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
                    return -1;
                } 
            }

            return await BuildCommandLine()
                            .UseDefaults()
                            .Build()
                            .InvokeAsync(args);
        }

        static CommandLineBuilder BuildCommandLine()
        {
            var rootCommand = new RootCommand("EventPipe Stress Tester - Orchestrator")
            {
                new Argument<FileInfo>(
                    name: "corescaletest-path",
                    description: "The location of the corescaletest executable."
                ),
                CommandLineOptions.EventSizeOption,
                CommandLineOptions.EventRateOption,
                CommandLineOptions.BurstPatternOption,
                OrchestrateCommandLine.ReaderTypeOption,
                OrchestrateCommandLine.SlowReaderOption,
                CommandLineOptions.DurationOption,
                OrchestrateCommandLine.MinCoreOption,
                OrchestrateCommandLine.MaxCoreOption,
                CommandLineOptions.ThreadsOption,
                CommandLineOptions.EventCountOption,
                OrchestrateCommandLine.RundownOption,
                OrchestrateCommandLine.BufferSizeOption,
                OrchestrateCommandLine.PauseOption
            };


            rootCommand.Handler = CommandHandler.Create((RootCommandHandler)Orchestrate);
            rootCommand.AddValidator(OrchestrateCommandLine.CoreMinMaxCoherentValidator);
            return new CommandLineBuilder(rootCommand);
        }

        
        /// <summary>
        /// This uses EventPipeEventSource's Stream constructor to parse the events real-time.
        /// It then returns the number of events read.
        /// </summary>
        static Action<object> UseEPES(int bufferSize, bool rundown, int slowReader)
        {
            return (object arg) =>
            {
                int pid = (int)arg;
                int eventsRead = 0;
                var slowReadSw = new Stopwatch();
                var totalTimeSw = new Stopwatch();
                var interval = TimeSpan.FromSeconds(0.75);
                DiagnosticsClient client = new DiagnosticsClient(pid);
                while (!client.CheckTransport())
                {
                    Console.WriteLine("still unable to talk");
                    Thread.Sleep(50);
                }
                EventPipeSession session = client.StartEventPipeSession(
                    new EventPipeProvider("MySource", EventLevel.Verbose), 
                    requestRundown: rundown, 
                    circularBufferMB: bufferSize);

                Console.WriteLine("session open");
                EventPipeEventSource epes = new EventPipeEventSource(session.EventStream);
                epes.Dynamic.All += (TraceEvent data) => {
                    eventsRead += 1;
                    if (slowReader > 0)
                    {
                        if (slowReadSw.Elapsed > interval)
                        {
                            Thread.Sleep(slowReader);
                            slowReadSw.Reset();
                        }
                    }
                };
                if (slowReader > 0)
                    slowReadSw.Start();
                totalTimeSw.Start();
                epes.Process();
                totalTimeSw.Stop();
                if (slowReader > 0)
                    slowReadSw.Stop();
                Console.WriteLine("Read total: " + eventsRead.ToString());
                Console.WriteLine("Dropped total: " + epes.EventsLost.ToString());

                EventCountDict[CurrentCoreCount] = (eventsRead, epes.EventsLost, totalTimeSw.Elapsed);
            };
        }

        /// <summary>
        /// This uses CopyToAsync to copy the trace into a filesystem first, and then uses EventPipeEventSource
        /// on the file to post-process it and return the total # of events read.
        /// </summary>
        static Action<object> UseFS(int bufferSize, bool rundown)
        {
            return (object arg) =>
            {
                int pid = (int)arg;
                int eventsRead = 0;
                var totalTimeSw = new Stopwatch();
                const string fileName = "./temp.nettrace";
                DiagnosticsClient client = new DiagnosticsClient(pid);
                while (!client.CheckTransport())
                {
                    Console.WriteLine("still unable to talk");
                    Thread.Sleep(50);
                }
                EventPipeSession session = client.StartEventPipeSession(
                    new EventPipeProvider("MySource", EventLevel.Verbose),
                    requestRundown: rundown,
                    circularBufferMB: bufferSize);

                Console.WriteLine("session open");

                using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    totalTimeSw.Start();
                    session.EventStream.CopyTo(fs);
                    totalTimeSw.Stop();
                }
                EventPipeEventSource epes = new EventPipeEventSource(fileName);
                epes.Dynamic.All += (TraceEvent data) => {
                    eventsRead += 1;
                };
                epes.Process();
                Console.WriteLine("Read total: " + eventsRead.ToString());
                Console.WriteLine("Dropped total: " + epes.EventsLost.ToString());

                EventCountDict[CurrentCoreCount] = (eventsRead, epes.EventsLost, totalTimeSw.Elapsed);
            };
        }

        static async Task<int> Orchestrate(
            IConsole console,
            CancellationToken ct,
            FileInfo corescaletestPath,
            int eventSize,
            int eventRate,
            BurstPattern burstPattern,
            ReaderType readerType,
            int slowReader,
            int duration,
            int minCore,
            int maxCore,
            int threads,
            int eventCount,
            bool rundown,
            int bufferSize,
            bool pause)
        {
            if (!corescaletestPath.Exists)
            {
                Console.WriteLine($"");
                return -1;
            }

            string readerTypeString = readerType switch
            {
                ReaderType.Stream => "Stream",
                ReaderType.EventPipeEventSource => "EventPipeEventSource",
                _ => "Stream"
            };

            var durationTimeSpan = TimeSpan.FromSeconds(duration); 

            Action<object> threadProc = readerType switch
            {
                ReaderType.Stream => UseFS(bufferSize, rundown),
                ReaderType.EventPipeEventSource => UseEPES(bufferSize, rundown, slowReader),
                _ => throw new ArgumentException("Invalid reader type")
            };

            if (eventRate == -1 && burstPattern != BurstPattern.NONE)
                throw new ArgumentException("Must have burst pattern of NONE if rate is -1");

            Console.WriteLine($"Configuration: event_size={eventSize}, event_rate={eventRate}, min_cores={minCore}, max_cores={maxCore}, num_threads={threads}, reader={readerType}, event_rate={(eventRate == -1 ? -1 : eventRate * threads)}, burst_pattern={burstPattern.ToString()}, slow_reader={slowReader}, duration={duration}");

            for (int num_cores = minCore; num_cores <= maxCore; num_cores++)
            {
                CurrentCoreCount = num_cores;

                Console.WriteLine("========================================================");
                Console.WriteLine("Starting run with proc count " + num_cores.ToString());

                Process eventWritingProc = new Process();
                eventWritingProc.StartInfo.FileName = corescaletestPath.FullName;
                eventWritingProc.StartInfo.Arguments = $"--threads {(threads == -1 ? num_cores.ToString() : threads.ToString())} --event-count {eventCount} --event-size {eventSize} --event-rate {eventRate} --burst-pattern {burstPattern} --duration {(int)durationTimeSpan.TotalSeconds}";
                eventWritingProc.StartInfo.UseShellExecute = false;
                eventWritingProc.StartInfo.RedirectStandardInput = true;
                eventWritingProc.StartInfo.Environment["COMPlus_StressLog"] = "1";
                eventWritingProc.StartInfo.Environment["COMPlus_LogFacility"] = "2000";
                eventWritingProc.StartInfo.Environment["COMPlus_LogLevel"] = "8";
                eventWritingProc.StartInfo.Environment["COMPlus_StressLogSize"] = "0x1000000";
                eventWritingProc.Start();

                Console.WriteLine($"Executing: {eventWritingProc.StartInfo.FileName} {eventWritingProc.StartInfo.Arguments}");
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // Set affinity and priority
                    ulong affinityMask = 0;
                    for (int j = 0; j < num_cores; j++)
                    {
                        affinityMask |= ((ulong)1 << j);
                    }
                    eventWritingProc.ProcessorAffinity = (IntPtr)((ulong)eventWritingProc.ProcessorAffinity & affinityMask);
                    eventWritingProc.PriorityClass = ProcessPriorityClass.RealTime; // Set the process priority to highest possible
                }

                // Start listening to the event.
                var listenerTask = Task.Run(() => threadProc(eventWritingProc.Id), ct);

                if (pause)
                {
                    Console.WriteLine("Press <enter> to start test");
                    Console.ReadLine();
                }

                // start the target process
                StreamWriter writer = eventWritingProc.StandardInput;
                writer.WriteLine("\r\n");
                eventWritingProc.WaitForExit();

                await listenerTask;
                
                Console.WriteLine("Done with proc count " + num_cores.ToString());
                Console.WriteLine("========================================================");

            }
            
            Console.WriteLine("**** Summary ****");
            for (int i = minCore; i <= maxCore; i++)
            {
                Console.WriteLine($"{i} cores: {EventCountDict[i].Item1:N} events collected, {EventCountDict[i].Item2:N} events dropped in {EventCountDict[i].Item3.TotalSeconds:N} seconds - ({100 * ((double)EventCountDict[i].Item1 / (double)((long)EventCountDict[i].Item1 + EventCountDict[i].Item2))}% throughput)");
                Console.WriteLine($"\t({(double)EventCountDict[i].Item1/EventCountDict[i].Item3.TotalSeconds:N} events/s) ({((double)EventCountDict[i].Item1*eventSize*sizeof(char))/EventCountDict[i].Item3.TotalSeconds:N} bytes/s)");
            }

            return 0;
        }
    }
}
