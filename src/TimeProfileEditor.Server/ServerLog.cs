using System;
using VideoOS.Platform;

namespace TimeProfileEditor.Server
{
    /// <summary>
    /// The component's trail in the Event Server log.
    ///
    /// This is the only place a change made through the server component is recorded under the
    /// operator's name. The client-side log records what the operator asked for; the XProtect
    /// audit log will record the write under the *service account*, because that is who performed
    /// it. Neither of those says "user X changed profile Y", so this does - otherwise the audit
    /// requirement in the specification is met by a trail that names the wrong party.
    /// </summary>
    internal static class ServerLog
    {
        private const string Source = "TimeProfileEditor.Server";

        public static void Info(string message) => Write(false, message);

        public static void Error(string message, Exception ex = null) =>
            Write(true, ex == null ? message : $"{message}: {ex.GetBaseException().Message}");

        /// <summary>
        /// The audit line. Deliberately one entry per outcome, refusals included: a refused
        /// attempt is the more interesting half of an audit trail.
        /// </summary>
        public static void Audit(string caller, string action, string profile, string outcome,
                                 string detail = null)
        {
            Write(false, string.Join(" | ", new[]
            {
                "REVISION",
                "användare=" + (caller ?? "okänd"),
                "åtgärd=" + action,
                "profil=" + (profile ?? "-"),
                "utfall=" + outcome,
                detail
            }.RemoveEmpty()));
        }

        private static void Write(bool isError, string text)
        {
            try
            {
                EnvironmentManager.Instance.Log(isError, Source,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
            }
            catch
            {
                // Logging must never be why a save fails - but see Audit above: if this throws,
                // the only record of the change is the service account's audit entry.
            }
        }
    }

    internal static class StringArrayExtensions
    {
        public static string[] RemoveEmpty(this string[] values)
        {
            var kept = new System.Collections.Generic.List<string>(values.Length);
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) kept.Add(value);
            return kept.ToArray();
        }
    }
}
