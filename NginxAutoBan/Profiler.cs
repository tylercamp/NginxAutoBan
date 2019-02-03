using System;
using System.Diagnostics;
using Serilog;

namespace NAB
{
    class Profiler : IDisposable
    {
        Stopwatch sw;
        String label;
        ILogger logger;
        
        public Profiler(String label, ILogger logger)
        {
            this.sw = Stopwatch.StartNew();
            this.label = label;
            this.logger = logger;
        }

        public void Dispose() => logger.Debug("{label} took {ms}ms", label, sw.ElapsedMilliseconds);
    }
}