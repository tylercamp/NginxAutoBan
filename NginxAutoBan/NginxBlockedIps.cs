using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NAB
{
    class NginxBlockedIps
    {
        private String rulesFilePath;
        private List<String> knownBlockedIps;
        private Regex matchDenyIpRegex = new Regex(@"deny\s+(.+);");

        private ILogger logger = Log.ForContext<NginxBlockedIps>();
        private int blockThreshold;

        private ConcurrentDictionary<String, int> pendingIpBlockings = new ConcurrentDictionary<string, int>();

        public NginxBlockedIps(String rulesFilePath, int blockThreshold)
        {
            this.rulesFilePath = rulesFilePath;
            this.blockThreshold = blockThreshold;

            ReloadIpBlacklist();
        }

        public IEnumerable<String> BlockedIps => this.knownBlockedIps;

        public void ReloadIpBlacklist()
        {
            logger.Debug("Reloading IP blacklist");
            lock (rulesFilePath)
            {
                knownBlockedIps = File.ReadAllLines(rulesFilePath)
                    .Select(l => matchDenyIpRegex.Matches(l))
                    .Where(mc => mc.Count > 0)
                    .SelectMany(mc => mc.SelectMany(m => m.Groups.Skip(1).Select(c => c.Value.Trim())))
                    .Distinct()
                    .ToList();
            }
            logger.Debug("Got {numIps} IPs", knownBlockedIps.Count);
        }

        public bool ContainsIp(String ip)
        {
            ip = ip.Trim();
            return knownBlockedIps.Contains(ip);
        }

        public void BlockIp(String ip)
        {
            lock (rulesFilePath)
            {
                ip = ip.Trim();
                if (ContainsIp(ip))
                    return;

                int numStrikes = pendingIpBlockings.AddOrUpdate(ip, 1, (_, c) => c + 1);
                if (numStrikes >= blockThreshold)
                {
                    pendingIpBlockings.TryRemove(ip, out numStrikes);
                    logger.Information("Applying IP block on {ip} for {numStrikes}/{threshold} strikes", ip, numStrikes, blockThreshold);
                    File.AppendAllText(rulesFilePath, $"deny {ip};\n");
                    knownBlockedIps.Add(ip);

                    try
                    {
                        ReloadNginxConfig();
                    }
                    catch (Exception e)
                    {
                        logger.Warning("Exception occurred when reloading nginx config: {message}", e.Message);
                    }
                }
                else
                {
                    logger.Debug("1 strike against {ip} (currently at {numStrikes})", ip, numStrikes);
                }
            }
        }

        public void ReloadNginxConfig()
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
            proc.WaitForExit();
        }
    }
}
