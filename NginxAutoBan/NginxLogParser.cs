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
        private Regex ipRegex;

        public NginxLogParser(Regex ipRegex, List<String> matchers)
        {
            this.ipRegex = ipRegex;

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

            // TODO - Support multiple IP match formats
            if (!found || !ipRegex.IsMatch(logLine))
                return null;

            var ip = ipRegex.Match(logLine).Captures[0].Value;
            return ip.Trim();
        }
    }
}
