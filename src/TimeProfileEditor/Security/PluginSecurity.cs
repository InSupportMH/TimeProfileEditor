using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Login;
using VideoOS.Platform.Proxy.SecurityApi;
using VideoOS.Platform.Util;

namespace TimeProfileEditor.Security
{
    /// <summary>
    /// Why a permission was granted or refused. The UI needs the distinction: a user who
    /// was explicitly denied gets a different message from an administrator who has not
    /// finished installing the plugin yet.
    /// </summary>
    internal enum PermissionState
    {
        Granted,
        Denied,

        /// <summary>
        /// The server has never seen this plugin's security namespace, which means the
        /// Management Client has not loaded the plugin yet. Nobody can have been granted
        /// anything, so editing stays closed, but that is an install problem - not a
        /// statement about this user.
        /// </summary>
        NotRegistered,

        /// <summary>The permission could not be evaluated. Treated as denied.</summary>
        Unavailable
    }

    /// <summary>
    /// Server-backed permission checks.
    ///
    /// This is the *first* of two gates. It decides what the UI offers, and it asks the
    /// Management Server rather than trusting anything local, so it cannot be flipped by
    /// editing a client-side file. It is still only a UI gate: the authoritative check is
    /// the Management Server rejecting a Configuration API write from a role that may not
    /// perform it (see <see cref="Services.TimeProfileRepository"/>). Both must hold.
    ///
    /// Three different platform APIs can answer the question and they are not equally
    /// available in every host - the REST client works in a standalone SDK process but not
    /// necessarily inside the Smart Client, which already owns an initialised security
    /// stack. So all of them are tried and the answers combined, rather than betting the
    /// feature on one call.
    /// </summary>
    internal static class PluginSecurity
    {
        private static readonly object Sync = new object();
        private static SecurityApiClient _client;
        private static bool? _namespaceRegistered;
        private static PluginDefinition _definition;

        /// <summary>Last failure text, surfaced in the UI so a broken check can be diagnosed.</summary>
        public static string LastError { get; private set; }

        /// <summary>Which strategy produced the answer. Logged to make support questions answerable.</summary>
        public static string LastStrategy { get; private set; }

        /// <summary>Called by the plugin definition so the list-based checks have an instance to ask about.</summary>
        public static void Attach(PluginDefinition definition)
        {
            lock (Sync) _definition = definition;
        }

        /// <summary>Drops cached permissions. Call after login or when the user asks for a refresh.</summary>
        public static void Reset()
        {
            lock (Sync)
            {
                try { _client?.Close(); } catch { /* the client is being thrown away anyway */ }
                _client = null;
                _namespaceRegistered = null;
                LastError = null;
                LastStrategy = null;
                SystemEdition.Reset();
            }
        }

        public static PermissionState CanEdit() => Evaluate(SecurityActionIds.Edit);

        /// <summary>
        /// Whether the workspace should be offered at all.
        ///
        /// Hides the tab only on an explicit refusal. When the permission cannot be determined -
        /// the namespace is not registered yet, or the security service did not answer - the tab
        /// stays, because the alternative is a plugin that silently does not exist and gives an
        /// administrator nothing to diagnose. That costs nothing: the tab only ever displays
        /// configuration the Management Server itself agreed to hand this user, and editing
        /// remains closed in both of those states.
        /// </summary>
        public static bool CanView() => Evaluate(SecurityActionIds.View) != PermissionState.Denied;

        public static PermissionState Evaluate(string actionId)
        {
            // A measurement build says yes to everything, so that a refusal in the report can only
            // have come from the Management Server and not from this file. See
            // EditionMode.Measurement for why that is worth a build of its own. The second gate is
            // untouched: the server still judges the save.
            if (SystemEdition.Configured == EditionMode.Measurement)
            {
                LastStrategy = "MÄTLÄGE - pluginet kontrollerar ingen behörighet";
                LastError = null;
                return PermissionState.Granted;
            }

            var failures = new List<string>();
            var sawDenial = false;
            var sawNotRegistered = false;

            foreach (var strategy in Strategies())
            {
                PermissionState state;
                try
                {
                    state = strategy.Evaluate(actionId);
                }
                catch (Exception ex)
                {
                    failures.Add($"{strategy.Name}: {ex.GetBaseException().Message}");
                    continue;
                }

                switch (state)
                {
                    case PermissionState.Granted:
                        // One authoritative yes is enough - a strategy that cannot see the
                        // permission must not be able to veto one that can.
                        LastError = null;
                        LastStrategy = strategy.Name;
                        return PermissionState.Granted;

                    case PermissionState.Denied:
                        // Only a source that answers for *this* user may refuse. The others read
                        // the administration-side view of the permission, which is not populated
                        // for an ordinary operator in the Smart Client - so their "no" means "I
                        // cannot see it", and letting that hide the workspace refuses exactly the
                        // users the feature exists for.
                        if (strategy.Authoritative)
                        {
                            sawDenial = true;
                            LastStrategy = strategy.Name;
                        }
                        else
                        {
                            failures.Add($"{strategy.Name}: såg ingen rättighet (inte avgörande)");
                        }

                        break;

                    case PermissionState.NotRegistered:
                        sawNotRegistered = true;
                        break;

                    default:
                        failures.Add($"{strategy.Name}: kunde inte svara");
                        break;
                }
            }

            if (sawDenial)
            {
                LastError = null;
                return PermissionState.Denied;
            }

            if (sawNotRegistered)
            {
                LastError = null;
                return PermissionState.NotRegistered;
            }

            LastError = string.Join(" | ", failures);
            EnvironmentManager.Instance.Log(true, nameof(PluginSecurity),
                $"Behorigheten '{actionId}' kunde inte avgoras. {LastError}");
            return PermissionState.Unavailable;
        }

        /// <summary>
        /// Whether an exception is the Management Server refusing rather than something breaking.
        ///
        /// The type name is checked before the text: the server raises NotAuthorizedMIPException
        /// for a refusal, and that survives translation in a way "You do not have sufficient
        /// permissions" does not.
        /// </summary>
        internal static bool LooksLikePermissionProblem(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var name = e.GetType().Name;
                if (name.IndexOf("NotAuthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("AccessDenied", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                if (LooksLikePermissionProblem(e.Message)) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a server error text reads as a refusal rather than a fault. Shared with the
        /// repository so a save failure and a configuration probe classify the same way.
        /// </summary>
        internal static bool LooksLikePermissionProblem(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var m = message.ToLowerInvariant();
            return m.Contains("denied") || m.Contains("permission") || m.Contains("not authorized")
                   || m.Contains("unauthorized") || m.Contains("access") && m.Contains("right")
                   || m.Contains("behörighet") || m.Contains("forbidden");
        }

        private sealed class Strategy
        {
            public string Name;
            public Func<string, PermissionState> Evaluate;

            /// <summary>
            /// Whether this source answers for the signed-in user, and may therefore refuse one.
            /// A source that reads the permission from the administration side can confirm a right
            /// but never rule one out, because not seeing it and not being allowed it look the same
            /// from there.
            /// </summary>
            public bool Authoritative;
        }

        private static IEnumerable<Strategy> Strategies()
        {
            // The server-backed per-user question first: it is the only one whose "no" means no.
            yield return new Strategy
            {
                Name = "SecurityApi.HasPermission",
                Evaluate = ViaSecurityApi,
                Authoritative = true
            };
            yield return new Strategy
            {
                Name = "SecurityAccess.CheckPermission",
                Evaluate = ViaCheckPermission
            };
            yield return new Strategy
            {
                Name = "SecurityAccess.PermittedActionList",
                Evaluate = ViaPermittedActionList
            };
        }

        /// <summary>
        /// Asks the Management Server which of this plugin's actions a named identity holds.
        ///
        /// WHICH OVERLOAD, AND WHY NOT THE OTHER ONE
        ///
        /// A permission is not stored against a namespace but against an object inside it, and the
        /// two methods that look interchangeable name different objects. Decompiled from MIP 25.1.3,
        /// which is what this builds against:
        ///
        ///     GetPermittedActionList(identity, definition)
        ///         -> IsPermittedByActionList(id, identity, actions, definition.Id.ToUpperInvariant())
        ///     GetOverallPermittedActionList(identity, definition)
        ///         -> IsPermittedByActionList(id, identity, actions, "/")
        ///
        /// The tick boxes an administrator sets under Roles are written against the plugin's own id,
        /// which is also the object the client's own CheckPermission(pluginId, actionId) consults.
        /// Nothing is stored against "/", so the Overall variant comes back empty for every user who
        /// is not an administrator - and it is silent about it, because it swallows its exceptions
        /// and returns an empty list either way. This code called that one first, so this strategy
        /// has never once confirmed a right for an ordinary operator.
        ///
        /// What hid it is that an administrator never reaches the object at all: the administrator
        /// bypass answers first, so any check run by an administrator says yes and the mechanism
        /// looks sound. A control question answered by an administrator cannot detect a wrong object.
        ///
        /// Still advisory rather than authoritative, for a different reason: an empty list means
        /// "refused" and "the call threw" alike, and the client has no second identity to ask about
        /// to tell those apart. The Event Server component does - it is an administrator, so it can
        /// ask about itself and check the channel is alive - which is why the refusal is decided
        /// there and only confirmed here.
        /// </summary>
        private static PermissionState ViaPermittedActionList(string actionId)
        {
            var definition = _definition;
            var identity = CurrentIdentity();
            if (definition == null || string.IsNullOrEmpty(identity)) return PermissionState.Unavailable;

            var permitted = SecurityAccess.GetPermittedActionList(identity, definition);
            if (permitted == null) return PermissionState.Unavailable;

            return permitted.Any(a => string.Equals(a, actionId, StringComparison.OrdinalIgnoreCase))
                ? PermissionState.Granted
                : PermissionState.Denied;
        }

        /// <summary>
        /// The platform's own plugin-level check. It reports a refusal by throwing, and an
        /// environment problem looks identical from here, so its negative answer is only ever
        /// "unavailable" and the refusal is left to the authoritative source.
        /// </summary>
        private static PermissionState ViaCheckPermission(string actionId)
        {
            try
            {
                SecurityAccess.CheckPermission(PluginIds.PluginDefinition, actionId);
                return PermissionState.Granted;
            }
            catch (Exception)
            {
                return PermissionState.Unavailable;
            }
        }

        private static PermissionState ViaSecurityApi(string actionId)
        {
            var client = GetClient();
            if (client == null) return PermissionState.Unavailable;
            if (_namespaceRegistered == false) return PermissionState.NotRegistered;

            return client.HasPermission(PluginIds.PluginDefinition, actionId)
                ? PermissionState.Granted
                : PermissionState.Denied;
        }

        private static string CurrentIdentity()
        {
            var serverId = CurrentServerId();
            if (serverId == null) return null;

            var settings = LoginSettingsCache.GetLoginSettings(serverId);
            return settings?.UserIdentity;
        }

        private static ServerId CurrentServerId() =>
            EnvironmentManager.Instance.MasterSite?.ServerId ?? Configuration.Instance.ServerFQID?.ServerId;

        private static SecurityApiClient GetClient()
        {
            lock (Sync)
            {
                if (_client != null) return _client;

                var serverId = CurrentServerId();
                if (serverId == null) return null;

                var loginSettings = LoginSettingsCache.GetLoginSettings(serverId);
                if (loginSettings == null) return null;

                var client = new SecurityApiClient();
                client.Initialize(loginSettings, false);

                _namespaceRegistered = TryDetectNamespace(client);

                if (_namespaceRegistered == false)
                {
                    EnvironmentManager.Instance.Log(false, nameof(PluginSecurity),
                        "Pluginets sakerhetsnamnrymd finns inte pa servern. Starta Management Client en gang " +
                        "med pluginet installerat sa registreras behorigheterna under Roller -> Tidsprofiler.");
                }

                _client = client;
                return _client;
            }
        }

        /// <summary>
        /// Whether the Management Server knows this plugin's security namespace, or null when the
        /// running platform cannot say.
        ///
        /// Listing namespaces only exists from MIP SDK 26.1. The plugin is compiled against 25.2 so
        /// that a single package loads on both 2025 R2 and 2026 R1, and this answer is worth having
        /// where it is available - it separates "this user was refused" from "Management Client has
        /// never loaded the plugin, so nobody can have been granted anything". So it is asked for
        /// reflectively and simply skipped on the older platform.
        /// </summary>
        private static bool? TryDetectNamespace(SecurityApiClient client)
        {
            try
            {
                var type = client.GetType();
                var load = type.GetMethod("LoadAllSecurityNamespaces", Type.EmptyTypes);
                var all = type.GetProperty("AllSecurityNamespaces");
                if (load == null || all == null) return null;

                load.Invoke(client, null);
                if (!(all.GetValue(client) is IEnumerable namespaces)) return null;

                foreach (var entry in namespaces)
                {
                    var id = entry?.GetType().GetProperty("id")?.GetValue(entry) as string;
                    if (Guid.TryParse(id, out var parsed) && parsed == PluginIds.PluginDefinition)
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(false, nameof(PluginSecurity),
                    "Kunde inte lasa serverns sakerhetsnamnrymder: " + ex.GetBaseException().Message);
                return null;
            }
        }
    }
}
