using System;
using System.Text.RegularExpressions;

namespace NAB
{
    class Matcher
    {
        String sourceString;
        Regex regex;

        public Matcher(String sourceString)
        {
            this.sourceString = sourceString;
            try { regex = new Regex(this.sourceString); }
            catch { }
        }

        public bool IsMatch(String text) => regex?.IsMatch(text) ?? sourceString.ToLower().Contains(text.ToLower());
    }
}