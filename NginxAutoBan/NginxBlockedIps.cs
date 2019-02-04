using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace NAB
{
    class NginxBlockedIps : IDisposable
    {
        private String rulesFilePath;
        private ConcurrentBag<String> knownBlockedIps;
        private Regex matchDenyIpRegex = new Regex(@"deny\s+(.+);");
        private ILogger logger = Log.ForContext<NginxBlockedIps>();
        private int blockThreshold;
        private Task nginxReloadTask;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private bool refreshRequested = false;
        private List<Matcher> whitelistedIps;

        private ConcurrentDictionary<String, int> pendingIpBlockings = new ConcurrentDictionary<string, int>();

        public NginxBlockedIps(String rulesFilePath, int blockThreshold, int maxRefreshInterval, IEnumerable<String> whitelistedIps)
        {
            this.rulesFilePath = rulesFilePath;
            this.blockThreshold = blockThreshold;

            this.whitelistedIps = (whitelistedIps ?? Enumerable.Empty<String>()).Select(t => new Matcher(t)).ToList();

            ReloadIpBlacklist();

            nginxReloadTask = Task.Factory.StartNew(() => {

                var sw = Stopwatch.StartNew();

                while (!cts.IsCancellationRequested) {

                    if (!(sw.Elapsed.TotalSeconds > maxRefreshInterval && refreshRequested))
                    {
                        try { Task.Delay(100, cts.Token).Wait(); }
                        catch { break; }
                    }
                    else
                    {
                        try
                        {
                            logger.Information("Reloading nginx config");

                            var psi = new ProcessStartInfo
                            {
                                FileName = "nginx",
                                Arguments = "-s reload",
                                UseShellExecute = false
                            };

                            var proc = new Process { StartInfo = psi };
                            proc.Start();
                            while (!proc.HasExited && !cts.IsCancellationRequested) {
                                try { Task.Delay(10, cts.Token).Wait(); }
                                catch { break; }
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Warning("Exception occurred when reloading nginx config: {message}", e.Message);
                        }

                        sw.Restart();
                        refreshRequested = false;
                    }
                }                
            });
        }

        public IEnumerable<String> BlockedIps => this.knownBlockedIps;

        public bool AutoRefreshNginx { get; set; } = true;

        public void ReloadIpBlacklist()
        {
            logger.Debug("Reloading IP blacklist");
            lock (rulesFilePath)
            {
                knownBlockedIps = new ConcurrentBag<string>(
                    File.ReadAllLines(rulesFilePath)
                        .Select(l => matchDenyIpRegex.Matches(l))
                        .Where(mc => mc.Count > 0)
                        .SelectMany(mc => mc.SelectMany(m => m.Groups.Skip(1).Select(c => c.Value.Trim())))
                        .Distinct()
                );
            }
            logger.Debug("Got {numIps} IPs", knownBlockedIps.Count);
        }

        public bool IsWhitelisted(String ip) => whitelistedIps.Any(m => m.IsMatch(ip));

        public bool ContainsIp(String ip) => knownBlockedIps.Contains(ip.Trim());

        public void BlockIp(String ip)
        {
            ip = ip.Trim();
            if (ContainsIp(ip))
                return;

            int numStrikes = pendingIpBlockings.AddOrUpdate(ip, 1, (_, c) => c + 1);
            if (numStrikes >= blockThreshold)
            {
                pendingIpBlockings.TryRemove(ip, out numStrikes);
                logger.Information("Applying IP block on {ip} for {numStrikes}/{threshold} strikes", ip, numStrikes, blockThreshold);
                knownBlockedIps.Add(ip);
                lock (rulesFilePath)
                    File.AppendAllText(rulesFilePath, $"deny {ip};\n");
                refreshRequested = true;
            }
            else
            {
                logger.Debug("1 strike against {ip} (currently at {numStrikes})", ip, numStrikes);
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            while (!nginxReloadTask.IsCompleted)
                Task.Delay(10).Wait();
        }
    }
}
