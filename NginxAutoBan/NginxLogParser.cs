using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NAB
{
    class NginxLogParser
    {
        private List<Matcher> matchers;
        private List<Regex> ipRegexes;

        public NginxLogParser(IEnumerable<Regex> ipRegexes, IEnumerable<String> matchers)
        {
            this.ipRegexes = ipRegexes.ToList();
            this.matchers = matchers.Select(t => new Matcher(t)).ToList();
        }

        public String GetMatchedIp(String logLine)
        {
            bool found = false;

            found = matchers.Any(m => m.IsMatch(logLine));

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
