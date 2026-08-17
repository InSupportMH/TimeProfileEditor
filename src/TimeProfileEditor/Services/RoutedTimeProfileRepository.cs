using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TimeProfileEditor.Model;
using TimeProfileEditor.Protocol;

namespace TimeProfileEditor.Services
{
    /// <summary>
    /// Decides whether a read or a write goes straight to the Management Server or through the
    /// Event Server component, and hides the difference from everything above it.
    ///
    /// WHY BOTH PATHS EXIST
    ///
    /// A time profile is configuration, and only XProtect Corporate can grant a role the right to
    /// write configuration. On Corporate an operator with the plugin's Edit permission writes
    /// directly, one hop, and the component is never involved - it need not even be installed. On
    /// Expert and Professional+ the Management Server refuses that write for everyone but an
    /// administrator, whatever the plugin's own permissions say, and the request goes to the Event
    /// Server component instead: it runs under an account that is an administrator, and it checks
    /// the caller's permission against the same tick boxes before doing anything.
    ///
    /// WHY THE DIRECT PATH IS TRIED FIRST
    ///
    /// The product tier is not consulted. The refusal is: the server is the authority on what it
    /// will accept, and a licence flag is only a prediction about that. Trying and reading the
    /// answer is therefore correct on a tier nobody has tested yet, where a guess would not be. It
    /// also means one client binary behaves correctly everywhere, with nothing to configure and no
    /// way to install the wrong one.
    ///
    /// READS TAKE THE SAME ROUTE, FOR A DIFFERENT REASON
    ///
    /// XProtect does not refuse a read it disagrees with - it hands back the items the caller may
    /// see, which for an operator on Professional+ is none. So a direct read succeeds and returns
    /// an empty list, and there is nothing to edit. Where the direct read comes back empty and the
    /// component answers, the component's list is used: it is the same permission being enforced,
    /// just by the party that can see past it.
    /// </summary>
    internal sealed class RoutedTimeProfileRepository
    {
        private readonly TimeProfileRepository _direct = new TimeProfileRepository();
        private readonly ServerComponentChannel _server = new ServerComponentChannel();

        /// <summary>What the last operation did, for the status line and the log.</summary>
        public string LastRoute { get; private set; }

        public IReadOnlyList<TimeProfileInfo> LoadProfiles()
        {
            var direct = _direct.LoadProfiles();
            if (direct != null && direct.Count > 0)
            {
                LastRoute = "direkt";
                return direct;
            }

            // Empty is ambiguous - it means "you may see none" and "there are none" alike - so the
            // component is asked before concluding anything. If it is not there, an empty list is
            // the honest answer and the workspace says so.
            var answer = _server.Availability();
            if (answer == null || answer.Status != ResponseStatus.Ok) return direct;

            var routed = _server.LoadProfiles();
            if (routed == null || routed.Status != ResponseStatus.Ok || routed.Profiles == null)
                return direct;

            LastRoute = "via Event Server";
            ChangeLog.Info($"Hämtade {routed.Profiles.Count} tidsprofiler via serverkomponenten.");
            return routed.Profiles.Select(p => p.ToModel()).ToList();
        }

        public ProfileSchedule LoadSchedule(Guid profileId)
        {
            var direct = _direct.LoadSchedule(profileId);
            if (direct?.Profile != null)
            {
                LastRoute = "direkt";
                return direct;
            }

            var answer = _server.Availability();
            if (answer == null || answer.Status != ResponseStatus.Ok) return direct;

            var routed = _server.LoadSchedule(profileId);
            if (routed == null || routed.Status != ResponseStatus.Ok) return direct;

            var profile = routed.Profiles?.FirstOrDefault();
            if (profile == null) return direct;

            LastRoute = "via Event Server";

            return new ProfileSchedule
            {
                Profile = profile.ToModel(),
                Entries = WireEntry.ToModel(routed.Entries),
                LastModified = ParseTimestamp(routed.LastModified)
            };
        }

        /// <summary>
        /// Whether a direct save's outcome is one the component should be asked about.
        ///
        /// Both of these mean the same thing operationally: the Management Server would not let this
        /// user do it, and nothing was written. A refusal says so outright. A profile that is not
        /// visible says so by omission - see <see cref="SaveStatus.NotVisible"/> - and it is the
        /// commoner of the two on Expert and Professional+, where an operator's configuration read
        /// comes back empty and every profile therefore looks deleted.
        ///
        /// Nothing else is routed. A save that failed for another reason - a stale timestamp, a
        /// malformed entry, the server being down - is reported as it happened, because sending it a
        /// second way would at best repeat the failure and at worst apply half of it twice. That
        /// argument turns on whether anything was written, which is why it does not cover these two.
        /// </summary>
        internal static bool ShouldRoute(SaveStatus status) =>
            status == SaveStatus.PermissionDenied || status == SaveStatus.NotVisible;

        /// <summary>
        /// Saves, directly if the server allows it and through the component if it does not.
        /// </summary>
        public SaveOutcome Save(Guid profileId, IReadOnlyList<ScheduleEntry> desired,
                                IReadOnlyList<ScheduleEntry> baseline, DateTime expectedLastModified)
        {
            var direct = _direct.Save(profileId, desired, baseline, expectedLastModified);
            if (!ShouldRoute(direct.Status))
            {
                LastRoute = "direkt";
                return direct;
            }

            var invisible = direct.Status == SaveStatus.NotVisible;

            var answer = _server.Availability();
            if (answer == null)
            {
                // No component, so the direct answer stands - but say which answer it was. An
                // operator told only "you lack permission" on a system where nobody but an
                // administrator can ever write goes to ask for a permission that cannot be granted.
                ChangeLog.Info(invisible
                    ? "Tidsprofilen gick inte att läsa som den här användaren och ingen serverkomponent svarade."
                    : "Skrivningen nekades och ingen serverkomponent svarade.");

                if (invisible)
                    return SaveOutcome.Fail(
                        "Tidsprofilen gick inte att läsa som du - den kan ha tagits bort, eller så " +
                        "får du inte läsa konfigurationen. Ingen serverkomponent svarade som kunde " +
                        "avgöra vilket - be administratören installera Tidsprofiler på Event Server.");

                return SaveOutcome.Denied(direct.Message + " Ingen serverkomponent svarade heller - " +
                                          "be administratören installera Tidsprofiler på Event Server.");
            }

            if (answer.Status != ResponseStatus.Ok)
                return SaveOutcome.Denied(answer.Message ??
                                          "Du saknar behörighet att ändra denna tidsprofil.");

            var routed = _server.Save(profileId, desired, baseline, expectedLastModified);
            if (routed == null)
                return SaveOutcome.Fail("Serverkomponenten svarade inte. Försök igen, eller " +
                                        "kontrollera att Event Server körs.");

            LastRoute = "via Event Server";
            ChangeLog.Info($"Sparade tidsprofil {profileId} via serverkomponenten: {routed.Status}.");

            return Translate(routed);
        }

        /// <summary>
        /// What is worth warning this user about before they start editing, or null when there is
        /// nothing. Explains the situation rather than deciding anything - the save still tries.
        ///
        /// Silence when the component answers and permits, on purpose. A banner that is always
        /// there is a banner nobody reads, and "your save will be carried by the Event Server" is
        /// not the operator's problem to hold in mind.
        /// </summary>
        public string DescribeRoute()
        {
            var answer = _server.Availability();

            if (answer == null)
                return "Servern tar inte emot ändringar direkt från dig, och ingen serverkomponent " +
                       "svarar. Be administratören installera Tidsprofiler på Event Server - " +
                       "annars går ändringarna inte att spara.";

            if (answer.Status == ResponseStatus.Ok) return null;

            return answer.Message;
        }

        private static SaveOutcome Translate(ServerResponse response)
        {
            switch (response.Status)
            {
                case ResponseStatus.Denied:
                    return SaveOutcome.Denied(response.Message);

                case ResponseStatus.Failed:
                    return SaveOutcome.Fail(response.Message);

                default:
                    var outcome = new SaveOutcome
                    {
                        Status = response.Status == ResponseStatus.NothingToDo
                            ? SaveStatus.NothingToDo
                            : SaveStatus.Success,
                        Message = response.Message
                    };

                    // The list the component applied, not the one this client proposed. They should
                    // be the same, and where they are not it is the component's account that is
                    // true - it is what actually touched the configuration, and it is what its own
                    // audit line recorded.
                    if (response.Changes != null) outcome.AppliedChanges.AddRange(response.Changes);
                    return outcome;
            }
        }

        /// <summary>
        /// A timestamp that will not parse becomes MinValue, and that is on purpose: the next save
        /// compares against it, finds it does not match what the server holds, and asks the operator
        /// to reload. Guessing DateTime.Now here would turn a broken timestamp into a silent
        /// overwrite of somebody else's edit.
        /// </summary>
        private static DateTime ParseTimestamp(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.MinValue;
    }
}
