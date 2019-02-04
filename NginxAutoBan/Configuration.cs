using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NAB
{
    class Configuration
    {
        private static IConfigurationRoot _root;
        public static IConfigurationRoot Root
        {
            get
            {
                if (_root == null)
                {
                    _root = new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.json")
                        .AddEnvironmentVariables()
                        .Build();
                }
                return _root;
            }
        }

        private static Configuration _instance = null;
        public static Configuration Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new Configuration();
                    Root.Bind(_instance);
                }
                return _instance;
            }
        }

        public NginxConfig Nginx { get; set; } = new NginxConfig();
        public ScanningConfig Scanning { get; set; } = new ScanningConfig();

        public class NginxConfig
        {
            public String LogFolder { get; set; }
            public String RulesFile { get; set; }
            public int MaxRefreshInterval { get; set; }
        }

        public class ScanningConfig
        {
            public List<String> Patterns { get; set; }
            public List<String> IpAddressPatterns { get; set; }
            public List<String> WhitelistedIps { get; set; }
            public int ViolationsThreshold { get; set; }
        }

        public bool Validate()
        {
            bool isValid = true;

            if (Scanning.IpAddressPatterns == null || Scanning.IpAddressPatterns.Count == 0)
            {
                isValid = false;
                Log.Error("IpAddressPatterns is empty or null");
            }
            else
            {
                foreach (var pattern in Scanning.IpAddressPatterns)
                {
                    try
                    {
                        new Regex(pattern);
                    }
                    catch (Exception e)
                    {
                        isValid = false;
                        Log.Error("Invalid value for IpAddressPattern {pattern}: {error}", pattern, e.Message);
                    }
                }
            }

            DirectoryInfo nginxLogFolder = null;
            try
            {
                nginxLogFolder = new DirectoryInfo(Nginx.LogFolder);
            }
            catch(Exception e)
            {
                isValid = false;
                Log.Error("Unable to check NGINX log folder at {folder} because: {message}", Nginx.LogFolder, e.Message);
            }

            if (nginxLogFolder != null)
            {
                if (!nginxLogFolder.Exists)
                {
                    isValid = false;
                    Log.Error("Nginx log folder {folder} does not exist", Nginx.LogFolder);
                }
                else
                {
                    var logFiles = nginxLogFolder.EnumerateFiles().Select(fi =>
                    {
                        try
                        {
                            using (var stream = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                return new { fi.FullName, IsOk = true, Reason = "" };
                            }
                        }
                        catch (Exception e)
                        {
                            return new { fi.FullName, IsOk = false, Reason = e.Message };
                        }
                    }).ToList();

                    var unreadableFiles = logFiles.Where(f => !f.IsOk).ToList();
                    if (unreadableFiles.Count > 0)
                    {
                        isValid = false;
                        Log.Error("Errors occurred when checking permissions for {numError}/{numTotal} files", unreadableFiles.Count, logFiles.Count);
                        foreach (var f in unreadableFiles)
                        {
                            Log.Error("-- Could not read file at {fullPath} because: {message}", f.FullName, f.Reason);
                        }
                    }
                }
            }

            try
            {
                using (var file = File.Open(Nginx.RulesFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            catch (Exception e)
            {
                isValid = false;
                Log.Error("Unable to open the NGINX rules file {fullPath} for read/write because: {message}: ", Nginx.RulesFile, e.Message);
            }

            if (Scanning.Patterns.Count == 0)
            {
                isValid = false;
                Log.Error("No scanning patterns were specified");
            }
            else
            {
                foreach (var pattern in Scanning.Patterns)
                {
                    try
                    {
                        new Regex(pattern);
                        Log.Information("Using scan pattern {pattern} as a regex", pattern);
                    }
                    catch (Exception e)
                    {
                        Log.Information("Using scan pattern {pattern} as plaintext match (regex parse failed: {message})", pattern, e.Message);
                    }
                }
            }

            return isValid;
        }
    }
}
