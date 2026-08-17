using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Login;

namespace TimeProfileEditor.Services
{
    /// <summary>
    /// Records who changed which time profile, when, and what changed.
    ///
    /// This writes the operator-facing trail to the MIP client log. It is deliberately not the
    /// only record: because every write goes through the Configuration API as the signed-in
    /// user, the Management Server also writes its own entry to the XProtect audit log
    /// (Server Logs -> audit log) under that user's name. That server-side entry is the one
    /// that cannot be edited from a client, so treat this log as the readable companion to it
    /// rather than as the authoritative record.
    /// </summary>
    internal static class ChangeLog
    {
        private const string Source = "TimeProfileEditor";

        public static void Saved(string profileName, IEnumerable<string> changes)
        {
            var list = changes?.ToList() ?? new List<string>();
            Write(false,
                $"Tidsprofil '{profileName}' sparad av {CurrentUser()} ({list.Count} ändring(ar)):" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", list));
        }

        public static void Refused(string profileName, string reason)
        {
            Write(true, $"Ändring av tidsprofil '{profileName}' nekades för {CurrentUser()}: {reason}");
        }

        public static void Failed(string profileName, string reason)
        {
            Write(true, $"Ändring av tidsprofil '{profileName}' misslyckades för {CurrentUser()}: {reason}");
        }

        public static void Info(string message) => Write(false, message);

        public static void Error(string message, Exception ex = null) =>
            Write(true, ex == null ? message : $"{message}: {ex.GetBaseException().Message}");

        private static void Write(bool isError, string text)
        {
            try
            {
                EnvironmentManager.Instance.Log(isError, Source, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
            }
            catch
            {
                // Logging must never be the reason a save fails.
            }
        }

        private static string CurrentUser()
        {
            try
            {
                var serverId = EnvironmentManager.Instance.MasterSite?.ServerId
                               ?? Configuration.Instance.ServerFQID?.ServerId;
                if (serverId == null) return "okänd användare";

                var settings = LoginSettingsCache.GetLoginSettings(serverId);
                return settings?.FullyQualifiedUserName
                       ?? settings?.UserName
                       ?? "okänd användare";
            }
            catch
            {
                return "okänd användare";
            }
        }
    }
}
