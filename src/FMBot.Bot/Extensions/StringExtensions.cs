using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FMBot.Bot.Extensions
{
    public static class StringExtensions
    {
        public static IEnumerable<string> SplitByMessageLength(this string str)
        {
            var messageLength = 2000;

            for (int index = 0; index < str.Length; index += messageLength)
            {
                yield return str.Substring(index, Math.Min(messageLength, str.Length - index));
            }
        }

        public static string FilterOutMentions(this string str)
        {
            var pattern = new Regex("(@everyone|@here|<@)");
            return pattern.Replace(str, "");
        }

        public static bool ContainsMentions(this string str)
        {
            var matchesPattern = Regex.Match(str, "(@everyone|@here|<@)");
            return matchesPattern.Success;
        }
    }
}
