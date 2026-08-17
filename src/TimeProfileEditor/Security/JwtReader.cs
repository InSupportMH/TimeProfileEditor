using System;
using System.Collections.Generic;
using System.Text;

namespace TimeProfileEditor.Security
{
    /// <summary>
    /// Reads the payload of a JWT without verifying anything about it.
    ///
    /// That is only ever safe when something else has already established that the token is
    /// genuine - on the server, the Management Server accepting it; in the diagnostics, nothing at
    /// all, which is why the report prints claim *names* and not their values.
    ///
    /// Shared source between the client and the Event Server component so the two cannot disagree
    /// about what a token says.
    /// </summary>
    internal static class JwtReader
    {
        /// <summary>
        /// The payload's top-level name/value pairs, or an empty set if the token is not a JWT.
        ///
        /// A minimal reader rather than a JSON parser: the payload is flat, the values wanted are
        /// strings, and the alternative is a serializer dependency for two lookups. Nested objects
        /// and arrays are skipped over rather than parsed - nothing here needs them.
        /// </summary>
        public static Dictionary<string, string> ReadClaims(string token)
        {
            var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(token)) return claims;

            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return claims;

                var payload = Encoding.UTF8.GetString(FromBase64Url(parts[1]));

                foreach (var pair in SplitTopLevel(payload))
                {
                    var colon = IndexOfSeparator(pair);
                    if (colon <= 0) continue;

                    var name = Unquote(pair.Substring(0, colon));
                    var value = Unquote(pair.Substring(colon + 1));
                    if (name.Length > 0 && !claims.ContainsKey(name)) claims[name] = value;
                }
            }
            catch
            {
                // A token that cannot be decoded carries no claims. Callers treat that as "no
                // identity", which is the safe reading either way.
            }

            return claims;
        }

        /// <summary>The first claim present out of the names given, or null.</summary>
        public static string First(IReadOnlyDictionary<string, string> claims, params string[] names)
        {
            foreach (var name in names)
                if (claims.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                    return value;

            return null;
        }

        private static int IndexOfSeparator(string pair)
        {
            var inString = false;
            for (var i = 0; i < pair.Length; i++)
            {
                var c = pair[i];
                if (c == '"' && (i == 0 || pair[i - 1] != '\\')) inString = !inString;
                else if (!inString && c == ':') return i;
            }

            return -1;
        }

        private static string Unquote(string value) => value.Trim().Trim('"');

        /// <summary>Splits a JSON object on commas that are not inside a string or a bracket.</summary>
        private static IEnumerable<string> SplitTopLevel(string json)
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("{")) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("}")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var depth = 0;
            var inString = false;
            var start = 0;

            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\')) inString = !inString;
                else if (!inString && (c == '[' || c == '{')) depth++;
                else if (!inString && (c == ']' || c == '}')) depth--;
                else if (!inString && depth == 0 && c == ',')
                {
                    yield return trimmed.Substring(start, i - start);
                    start = i + 1;
                }
            }

            if (start < trimmed.Length) yield return trimmed.Substring(start);
        }

        private static byte[] FromBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            return Convert.FromBase64String(padded);
        }
    }
}
