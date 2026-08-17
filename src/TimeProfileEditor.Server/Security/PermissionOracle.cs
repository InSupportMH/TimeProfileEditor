using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Login;
using VideoOS.Platform.Util;

namespace TimeProfileEditor.Server.Security
{
    /// <summary>
    /// Asks the Management Server what a given identity is allowed to do with this plugin.
    ///
    /// THE MECHANISM
    ///
    /// A component holding an administrative session may ask the server about a *different*
    /// identity. That is what lets this component act for an operator without simply lending them
    /// the Event Server's rights: the caller is established from their token, and what that caller
    /// may do is answered by the same authority, and the same tick boxes, as everywhere else.
    ///
    /// It replaces a text file of permitted users. A file was the wrong answer to a question that
    /// turned out to have a right one - permissions belong under Roles in Management Client, which
    /// is what the brief asked for, and a second list on the server would have been a second answer
    /// to the same question that eventually disagreed with the first.
    ///
    /// WHICH OVERLOAD, AND WHY NOT THE OBVIOUS ONE
    ///
    /// A permission is stored against an object inside the namespace, not against the namespace,
    /// and the two methods that look interchangeable name different objects. Decompiled from MIP
    /// 25.1.3, the version this builds against:
    ///
    ///     GetPermittedActionList(identity, node, null)
    ///         -> IsPermittedByActionList(kind, identity, actions, node.ItemKind.ToUpperInvariant())
    ///     GetOverallPermittedActionList(identity, node)
    ///         -> IsPermittedByActionList(kind, identity, actions, "/")
    ///
    /// The tick boxes are written against the plugin's own id, which is also the object the client's
    /// CheckPermission(pluginId, actionId) consults. Nothing is stored against "/", so the Overall
    /// variant returns an empty list for every user who is not an administrator - silently, because
    /// it swallows its own exceptions and returns an empty list for a failure too.
    ///
    /// What hides that mistake is worth stating: an administrator never reaches the object at all,
    /// because the administrator bypass answers first. So any check run by an administrator says
    /// yes, and a control question answered by an administrator cannot detect a wrong object. Both
    /// projects here have been bitten by it.
    ///
    /// FAILING CLOSED
    ///
    /// <see cref="Ask"/> returns null when nothing was learned, never true, and the caller treats
    /// unknown as refusal. A component that cannot check permissions has no business performing
    /// privileged writes.
    /// </summary>
    internal static class PermissionOracle
    {
        /// <summary>
        /// True, false, or null when the question could not be put at all. Null is not a soft no -
        /// nothing was learned, and the caller must refuse.
        /// </summary>
        public static bool? Ask(string identity, string action)
        {
            if (string.IsNullOrWhiteSpace(identity)) return null;

            var permitted = PermittedActions(identity, action);
            if (permitted == null)
            {
                Explain(identity, action, "anropet kastade, så ingenting blev känt");
                return null;
            }

            if (permitted.Contains(action, StringComparer.OrdinalIgnoreCase)) return true;

            // Nothing came back, and from here a refusal and a call that never arrived look
            // identical - the platform returns an empty list for both. The only thing that separates
            // them is a question whose answer is already known. This service's own identity is an
            // administrator, so it must come back holding the action; if it does, the server really
            // was reached and really did say no.
            //
            // Every way out of here that is not that control question has to be null. Answering
            // false instead would send an administrator to tick boxes that are already ticked while
            // the real fault - this service being unable to ask anything - goes unmentioned.
            var us = OurIdentity();

            if (string.IsNullOrEmpty(us))
            {
                Explain(identity, action,
                    "serverkomponenten har ingen egen session att kontrollera svaret mot, så ett " +
                    "riktigt nej går inte att skilja från ett uppslag som misslyckades");
                return null;
            }

            if (string.Equals(us, identity, StringComparison.OrdinalIgnoreCase))
            {
                Explain(identity, action,
                    "den som frågar är serverkomponentens egen identitet, som är administratör och " +
                    "måste ha allt - ett tomt svar betyder alltså att uppslaget misslyckades");
                return null;
            }

            var ours = PermittedActions(us, action);
            if (ours == null || !ours.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                Explain(identity, action,
                    "kontrollfrågan kom också tillbaka tom - den här tjänstens egen " +
                    "administratörsidentitet '" + us + "' saknade också " + action + ", så det är " +
                    "frågandet som är trasigt och inte den som frågar som saknar rättighet");
                return null;
            }

            Explain(identity, action,
                "nekad. Kanalen fungerar - kontrollfrågan för '" + us + "' besvarades - så det här " +
                "är Management Servers eget svar för den här identiteten mot objekt " +
                PluginIds.PluginDefinition.ToString().ToUpperInvariant());
            return false;
        }

        /// <summary>
        /// Whether this service can ask the question at all, phrased as the problem or null when
        /// there is none. Run at startup and written to the log, because the answer is otherwise
        /// only discoverable by an operator failing to save something.
        ///
        /// The control question is the same one <see cref="Ask"/> uses: our own identity is an
        /// administrator in the VMS, so it must come back holding whatever it is asked about.
        /// Anything else means the permissions this component enforces are not reaching it, and
        /// every request will be refused however the roles are configured.
        /// </summary>
        public static string SelfTest()
        {
            var us = OurIdentity();
            if (string.IsNullOrEmpty(us))
                return "tjänsten har ännu ingen egen inloggning, så den kan inte fråga Management " +
                       "Server om någonting";

            var ours = PermittedActions(us, SecurityActionIds.Edit);
            if (ours == null)
                return "frågan om vår egen identitet '" + us + "' kastade";

            if (!ours.Contains(SecurityActionIds.Edit, StringComparer.OrdinalIgnoreCase))
                return "frågan om vår egen identitet '" + us + "' kom tillbaka utan " +
                       SecurityActionIds.Edit + ", vilket en administrativ session måste ha - " +
                       "pluginets säkerhetsnamnrymd når alltså inte den här processen. Starta " +
                       "Management Client en gång med pluginet installerat.";

            return null;
        }

        /// <summary>
        /// The list is asked for one action at a time on purpose. The node carries the questions,
        /// and building it per call keeps the answer unambiguous: a list containing the action means
        /// that action, not "something was permitted".
        /// </summary>
        private static List<string> PermittedActions(string identity, string action)
        {
            try
            {
                // ItemKind is constructor-only, so the node is built rather than initialised. The
                // call reads three things from it: ItemKind as the security namespace, ItemKind
                // again as the object id when no parent item is given, and SecurityActions as the
                // questions. The rest is filler it never looks at.
                var node = new ItemNode(
                    PluginIds.PluginDefinition, Guid.Empty,
                    "Tidsprofiler", null, "Tidsprofiler", null,
                    ItemsAllowed.None, Category.Unknown, false)
                {
                    SecurityActions = new List<SecurityAction> { new SecurityAction(action, action) }
                };

                // parentItem null on purpose: with no parent the object id becomes the plugin's own
                // id, which is where Management Client writes the tick boxes. Passing an item would
                // ask about that item instead, and nothing is stored against one.
                return SecurityAccess.GetPermittedActionList(identity, node, null) ?? new List<string>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The Event Server's own session. Looked up fresh each time rather than cached: the service
        /// reconnects, and a stale session would start answering no to everything long after the
        /// problem had cleared.
        /// </summary>
        private static string OurIdentity()
        {
            try
            {
                var master = EnvironmentManager.Instance?.MasterSite?.ServerId;
                if (master != null)
                {
                    var settings = LoginSettingsCache.GetLoginSettings(master);
                    if (settings != null) return settings.UserIdentity;
                }

                return LoginSettingsCache.LoginSettings?.FirstOrDefault()?.UserIdentity;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// One line per verdict, on the machine where the verdict is made. A refusal is the only
        /// thing the caller ever sees, and a refusal alone cannot say whether the answer came from
        /// the Management Server or from a question that never got there.
        /// </summary>
        private static void Explain(string identity, string action, string what) =>
            ServerLog.Info($"Behörighetskontroll för '{identity ?? "?"}' på {action}: {what}");
    }
}
