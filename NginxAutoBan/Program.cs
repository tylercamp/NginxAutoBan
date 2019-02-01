using Serilog;
using System;
using System.IO;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NAB
{
    class Program
    {
        static bool shouldExit = false;

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (a, b) => shouldExit = true;
            Console.CancelKeyPress += (a, b) => shouldExit = true;
            AssemblyLoadContext.Default.Unloading += (a) => shouldExit = true;

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration.Root)
                .CreateLogger();

            ILogger logger = Log.ForContext<Program>();

            var config = Configuration.Instance;
            if (!config.Validate())
            {
                logger.Error("Errors were found while validating your configuration.");
                Console.ReadLine();
                return;
            }

            logger.Information("Successfully validated configuration.");

            var logParser = new NginxLogParser(new Regex(config.Scanning.IpAddressPattern), config.Scanning.Patterns);
            var ipBlocker = new NginxBlockedIps(config.Nginx.RulesFile, config.Scanning.ViolationsThreshold);
            using (var logFolder = new NginxLogsWatcher(config.Nginx.LogFolder))
            {
                logFolder.NewLine += (fullPath, text) =>
                {
                    var fileName = Path.GetFileName(fullPath);
                    logger.Verbose("Got line from {file}: {line}", fileName, text);

                    var ip = logParser.GetMatchedIp(text);
                    if (ip == null)
                    {
                        logger.Verbose("Ignoring line from {fileName} since there were no matches: {line}", text);
                        return;
                    }

                    if (ipBlocker.ContainsIp(ip))
                    {
                        logger.Verbose("Ignoring line from {fileName} since {ip} is already blocked", fileName, ip);
                        return;
                    }

                    logger.Debug("Blocking {ip} for: {line}", ip, text);
                    ipBlocker.BlockIp(ip);
                };

                while (!shouldExit)
                    Task.Delay(50).Wait();
            }

            logger.Information("Exiting...");
            Log.CloseAndFlush();
        }
    }
}
