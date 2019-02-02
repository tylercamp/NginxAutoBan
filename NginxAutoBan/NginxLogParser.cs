using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NAB
{
    class NginxLogParser
    {
        private List<String> matchedStrings;
        private List<Regex> matchedRegexes;
        private List<Regex> ipRegexes;

        public NginxLogParser(IEnumerable<Regex> ipRegexes, IEnumerable<String> matchers)
        {
            this.ipRegexes = ipRegexes.ToList();

            this.matchedStrings = new List<string>();
            this.matchedRegexes = new List<Regex>();

            foreach (var matcher in matchers)
            {
                try
                {
                    this.matchedRegexes.Add(new Regex(matcher));
                }
                catch
                {
                    this.matchedStrings.Add(matcher);
                }
            }
        }

        public String GetMatchedIp(String logLine)
        {
            bool found = false;

            found = this.matchedStrings.Any(str => logLine.ToLower().Contains(str));
            found = found || this.matchedRegexes.Any(reg => reg.IsMatch(logLine));

            if (!found)
                return null;

            var ipMatch = ipRegexes.Where(r => r.IsMatch(logLine)).Select(r => r.Match(logLine)).FirstOrDefault();
            if (ipMatch == null)
                return null;

            var ip = ipMatch.Captures[0].Value;
            return ip.Trim();
        }
    }
}
