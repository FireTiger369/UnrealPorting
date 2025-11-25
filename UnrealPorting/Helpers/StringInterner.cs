using System;
using System.Collections.Generic;

namespace UnrealPorting.Helpers
{
    public sealed class StringInterner
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public string Intern(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (_map.TryGetValue(value, out var pooled)) return pooled;
            _map[value] = value;
            return value;
        }

        public string Intern(ReadOnlySpan<char> span)
        {
            if (span.Length == 0) return string.Empty;
            // This ToString allocates once; the map then dedupes globally.
            var s = span.ToString();
            if (_map.TryGetValue(s, out var pooled)) return pooled;
            _map[s] = s;
            return s;
        }
    }
}
