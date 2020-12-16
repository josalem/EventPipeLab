using System;

namespace Common
{
    public class Config
    {
        public int MaxCores { get; set; } = 4;
        public int MinCores  { get; set; }= 1;
        public int NumberOfThreads { get; set; } = -1;
        public int EventRate { get; set; } = -1;
        public string ReaderType { get; set; } = "EPES";
        public int EventSize { get; set; } = 100;
        public BurstPattern Pattern { get; set; } = BurstPattern.NONE;
    }
}