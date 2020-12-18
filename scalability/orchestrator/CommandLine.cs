using Common;
using System;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace orchestrator
{
    public static class OrchestrateCommandLine
    {
        static public Option<ReaderType> ReaderTypeOption = 
            new Option<ReaderType>(
                alias: "--reader-type",
                getDefaultValue: () => ReaderType.Stream,
                description: "The method to read the stream of events.");

        static public Option<bool> PauseOption = 
            new Option<bool>(
                alias: "--pause",
                getDefaultValue: () => false,
                description: "Should the orchestrator pause before starting each test phase for a debugger to attach?");

        static public Option<bool> RundownOption = 
            new Option<bool>(
                alias: "--rundown",
                getDefaultValue: () => true,
                description: "Should the EventPipe session request rundown events?");

        static private Option<int> _bufferSizeOption = null;
        static public Option<int> BufferSizeOption 
        {
            get
            {
                if (_bufferSizeOption != null)
                    return _bufferSizeOption;

                _bufferSizeOption = new Option<int>(
                                            alias: "--buffer-size",
                                            getDefaultValue: () => 256,
                                            description: "The size of the buffer requested in the EventPipe session");
                _bufferSizeOption.AddValidator(CommandLineOptions.GreaterThanZeroValidator);
                return _bufferSizeOption;
            }
            private set {}
        }

        static private Option<int> _slowReaderOption = null;
        static public Option<int> SlowReaderOption 
        {
            get
            {
                if (_slowReaderOption != null)
                    return _slowReaderOption;

                _slowReaderOption = new Option<int>(
                                            alias: "--slow-reader",
                                            getDefaultValue: () => 0,
                                            description: "<Only valid for EventPipeEventSource reader> Delay every read by this many milliseconds.");
                _slowReaderOption.AddValidator(CommandLineOptions.GreaterThanOrEqualZeroValidator);
                return _slowReaderOption;
            }
            private set {}
        }

        static private Option<int> _minCoreOption = null;
        static public Option<int> MinCoreOption 
        {
            get
            {
                if (_minCoreOption != null)
                    return _minCoreOption;

                _minCoreOption = new Option<int>(
                                        alias: "--min-core",
                                        getDefaultValue: () => Environment.ProcessorCount,
                                        description: "The minimum number of cores to use.");
                _minCoreOption.AddValidator(CoreValueMustBeFeasibleValidator);
                return _minCoreOption;
            }
            private set {}
        }

        static private Option<int> _maxCoreOption = null;
        static public Option<int> MaxCoreOption 
        {
            get
            {
                if (_maxCoreOption != null)
                    return _maxCoreOption;

                _maxCoreOption = new Option<int>(
                                        alias: "--max-core",
                                        getDefaultValue: () => Environment.ProcessorCount,
                                        description: "The maximum number of cores to use.");
                _maxCoreOption.AddValidator(CoreValueMustBeFeasibleValidator);
                return _maxCoreOption;
            }
            private set {}
        }

        static public ValidateSymbol<OptionResult> CoreValueMustBeFeasibleValidator = (OptionResult result) =>
        {
            int val = result.GetValueOrDefault<int>();
            if (val < 1 || val > Environment.ProcessorCount)
                return $"Core count must be between 1 and {Environment.ProcessorCount}";
            return null;
        };

        static public ValidateSymbol<CommandResult> CoreMinMaxCoherentValidator = (CommandResult result) =>
        {
            int minCore = result.FindResultFor(MinCoreOption).GetValueOrDefault<int>();
            int maxCore = result.FindResultFor(MaxCoreOption).GetValueOrDefault<int>();
            if (minCore > maxCore)
                return $"(minCore={minCore}, maxCore={maxCore}) minCore must be less than or equal to maxCore.";
            return null;
        };
    }
}