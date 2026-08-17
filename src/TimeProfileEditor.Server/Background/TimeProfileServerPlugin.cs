using System;
using System.Collections.Generic;
using System.Linq;
using TimeProfileEditor.Protocol;
using TimeProfileEditor.Security;
using TimeProfileEditor.Server.Security;
using TimeProfileEditor.Services;
using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Messaging;

namespace TimeProfileEditor.Server.Background
{
    /// <summary>
    /// Answers the Smart Client's requests from inside the Event Server.
    ///
    /// Every request goes through the same three steps, in this order, with no way past them:
    ///
    ///   1. <see cref="TokenValidator"/> establishes that the caller is who the message says, by
    ///      asking the Management Server whether the presented token is genuine and then requiring
    ///      that it names the identity the request claims.
    ///   2. <see cref="PermissionOracle"/> asks the Management Server what that identity may do,
    ///      against the same tick boxes an administrator sets under Roles -> Tidsprofiler.
    ///   3. Only then does <see cref="TimeProfileRepository"/> touch the configuration.
    ///
    /// The reads are gated too, not only the writes. It would be tempting to let anyone list the
    /// profiles - the client's own workspace is already visible to everyone - but this component
    /// reads them with administrator rights, so an ungated read here hands out configuration that
    /// the Management Server had deliberately withheld from that user.
    /// </summary>
    internal sealed class TimeProfileServerPlugin : BackgroundPlugin
    {
        private readonly List<object> _filters = new List<object>();

        private MessageCommunication _messages;

        public override Guid Id => ServerIds.BackgroundPlugin;

        public override string Name => "Tidsprofiler - serverkomponent";

        public override List<EnvironmentType> TargetEnvironments =>
            new List<EnvironmentType> { EnvironmentType.Service };

        public override void Init()
        {
            try
            {
                var serverId = EnvironmentManager.Instance.MasterSite?.ServerId;
                if (serverId == null)
                {
                    ServerLog.Error("Ingen masterserver att lyssna på - komponenten startar inte.");
                    return;
                }

                MessageCommunicationManager.Start(serverId);
                _messages = MessageCommunicationManager.Get(serverId);

                Listen(ServerProtocol.PingRequest, OnPing);
                Listen(ServerProtocol.LoadRequest, OnLoad);
                Listen(ServerProtocol.SaveRequest, OnSave);

                // Said once, at startup, because the alternative way to discover it is an operator
                // being refused a save for a reason that has nothing to do with them. It is only a
                // warning: the component still starts and still answers, and every answer is a
                // refusal until this is fixed - which is the safe direction.
                var problem = PermissionOracle.SelfTest();
                ServerLog.Info(problem == null
                    ? "Serverkomponenten är igång och kan fråga Management Server om behörigheter."
                    : "Serverkomponenten är igång, MEN kan inte kontrollera behörigheter: " + problem);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Kunde inte starta serverkomponenten", ex);
            }
        }

        public override void Close()
        {
            foreach (var filter in _filters)
            {
                try { _messages?.UnRegisterCommunicationFilter(filter); }
                catch (Exception ex) { ServerLog.Info("Avregistrering misslyckades: " + ex.Message); }
            }

            _filters.Clear();
            _messages = null;
        }

        private void Listen(string messageId, Func<ClientRequest, FQID, ServerResponse> handler)
        {
            _filters.Add(_messages.RegisterCommunicationFilter(
                (message, destination, source) => Dispatch(message, source, messageId, handler),
                new CommunicationIdFilter(messageId)));
        }

        /// <summary>
        /// Turns a message into a request, runs the handler, and sends the answer back to whoever
        /// asked - specifically to them, not to the bus. A response can carry configuration, and
        /// broadcasting it would hand every connected client what one of them was allowed to read.
        /// </summary>
        private object Dispatch(Message message, FQID source, string messageId,
                                Func<ClientRequest, FQID, ServerResponse> handler)
        {
            ServerResponse response;
            ClientRequest request = null;

            try
            {
                request = ServerProtocol.FromJson<ClientRequest>(message.Data as string);
                if (request == null)
                {
                    response = Failed(null, "Begäran gick inte att läsa.");
                }
                else if (request.Protocol != ServerProtocol.Version)
                {
                    // Refuse rather than guess. The two halves ship as separate MSIs, so a
                    // mismatched pair is a normal state during a rollout, and the operator needs
                    // to be told which one to update instead of getting an odd failure later.
                    response = Failed(request.CorrelationId,
                        $"Klienten talar version {request.Protocol} och serverkomponenten version " +
                        $"{ServerProtocol.Version}. Uppdatera den av dem som är äldst.");
                }
                else
                {
                    response = handler(request, source);
                }
            }
            catch (Exception ex)
            {
                ServerLog.Error($"Fel vid hantering av {messageId}", ex);
                response = Failed(request?.CorrelationId, "Ett fel uppstod i serverkomponenten.");
            }

            Reply(messageId, response, source);
            return null;
        }

        private void Reply(string requestId, ServerResponse response, FQID source)
        {
            try
            {
                var responseId = requestId.Replace(".Request", ".Response");
                _messages.TransmitMessage(
                    new Message(responseId, ServerProtocol.ToJson(response)), source, null, null);
            }
            catch (Exception ex)
            {
                ServerLog.Error("Kunde inte skicka svaret till klienten", ex);
            }
        }

        // ---- handlers -------------------------------------------------------------------

        /// <summary>
        /// Lets a client find out whether the component is installed and whether this user may
        /// edit, without asking it to do anything. The client needs that to decide what to offer
        /// before the operator has tried and been refused.
        /// </summary>
        private ServerResponse OnPing(ClientRequest request, FQID source)
        {
            return Authorised(request, SecurityActionIds.Edit, out var caller, out var refusal)
                ? Ok(request.CorrelationId, $"Serverkomponenten svarar. {caller.Describe()} får spara.")
                : Denied(request.CorrelationId, refusal);
        }

        private ServerResponse OnLoad(ClientRequest request, FQID source)
        {
            if (!Authorised(request, SecurityActionIds.View, out var caller, out var refusal))
            {
                ServerLog.Audit(caller?.Describe(), "läsa", request.ProfileId, "nekad", refusal);
                return Denied(request.CorrelationId, refusal);
            }

            var repository = Repository(allowed: true);

            if (string.IsNullOrEmpty(request.ProfileId))
            {
                var profiles = repository.LoadProfiles();
                ServerLog.Audit(caller.Describe(), "lista", null, "ok", $"{profiles.Count} profiler");
                return new ServerResponse
                {
                    CorrelationId = request.CorrelationId,
                    Status = ResponseStatus.Ok,
                    Profiles = profiles.Select(WireProfile.From).ToList()
                };
            }

            if (!Guid.TryParse(request.ProfileId, out var profileId))
                return Failed(request.CorrelationId, "Ogiltigt profil-id.");

            var schedule = repository.LoadSchedule(profileId);
            ServerLog.Audit(caller.Describe(), "läsa", schedule?.Profile?.Name ?? request.ProfileId, "ok");

            return new ServerResponse
            {
                CorrelationId = request.CorrelationId,
                Status = ResponseStatus.Ok,
                Profiles = schedule?.Profile == null
                    ? new List<WireProfile>()
                    : new List<WireProfile> { WireProfile.From(schedule.Profile) },
                Entries = WireEntry.From(schedule?.Entries),
                LastModified = schedule?.LastModified.ToString("o",
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        private ServerResponse OnSave(ClientRequest request, FQID source)
        {
            if (!Authorised(request, SecurityActionIds.Edit, out var caller, out var refusal))
            {
                ServerLog.Audit(caller?.Describe(), "spara", request.ProfileId, "nekad", refusal);
                return Denied(request.CorrelationId, refusal);
            }

            if (!Guid.TryParse(request.ProfileId, out var profileId))
                return Failed(request.CorrelationId, "Ogiltigt profil-id.");

            // Parsing the timestamp is not optional: DateTime.MinValue would compare unequal to
            // whatever the server holds and turn every save into a stale-edit refusal, while
            // DateTime.Now would disable the check entirely. A request that cannot say what it was
            // editing gets told to reload rather than quietly overwriting someone.
            if (!DateTime.TryParse(request.ExpectedLastModified,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var expected))
                return Failed(request.CorrelationId,
                    "Begäran saknar tidsstämpel för vad som lästes in. Läs om profilen och försök igen.");

            var outcome = Repository(allowed: true).Save(
                profileId,
                WireEntry.ToModel(request.Entries),
                WireEntry.ToModel(request.Baseline),
                expected);

            // This component reads as the Event Server's own account, which is an administrator, so
            // a profile it cannot see is genuinely gone and it can say so plainly. The repository's
            // hedged wording is for the client, where reading nothing is the ordinary state for an
            // operator and says nothing about whether the profile exists.
            if (outcome.Status == SaveStatus.NotVisible)
                outcome.Message = "Tidsprofilen finns inte längre. Den kan ha tagits bort av någon annan.";

            ServerLog.Audit(caller.Describe(), "spara", request.ProfileId, outcome.Status.ToString(),
                outcome.AppliedChanges.Count == 0
                    ? outcome.Message
                    : string.Join("; ", outcome.AppliedChanges));

            return new ServerResponse
            {
                CorrelationId = request.CorrelationId,
                Status = Translate(outcome.Status),
                Message = outcome.Message,
                Changes = outcome.AppliedChanges.ToList()
            };
        }

        // ---- plumbing -------------------------------------------------------------------

        /// <summary>
        /// The gate. Three questions in a fixed order, and a no to any of them ends the request:
        /// is the token genuine, does it belong to the identity the request names, and has that
        /// identity been granted this action.
        ///
        /// Order matters. Asking the Management Server what an unproven identity may do would give
        /// a perfectly accurate answer about the wrong person, and looking permissive because the
        /// answer was true is exactly the failure this component exists to avoid.
        /// </summary>
        private static bool Authorised(ClientRequest request, string action,
                                       out CallerIdentity caller, out string refusal)
        {
            caller = TokenValidator.Validate(request.Token);

            if (!caller.IsAuthentic)
            {
                refusal = caller.Failure;
                caller = null;
                return false;
            }

            if (!caller.Owns(request.Identity, out refusal)) return false;

            var held = PermissionOracle.Ask(request.Identity, action);
            if (held == true)
            {
                refusal = null;
                return true;
            }

            // A permission that could not be checked is not a permission granted, but it is also
            // not the user's fault, and the two get different sentences. The server log carries the
            // detail either way - see PermissionOracle.Explain.
            refusal = held == null
                ? "Behörigheten kunde inte kontrolleras mot Management Server. Försök igen, eller " +
                  "be administratören titta i Event Server-loggen."
                : "Du saknar behörighet att ändra denna tidsprofil. Rättigheten ges per roll under " +
                  "Roller -> Tidsprofiler i Management Client.";
            return false;
        }

        /// <summary>
        /// A repository whose own permission gate answers with the decision this component already
        /// made.
        ///
        /// The repository checks before it writes - that is its second gate and the reason a save
        /// cannot be forced by calling it directly. Here it would otherwise ask
        /// <see cref="PluginSecurity"/>, which on the Event Server describes the *service account*
        /// and would say yes to everyone. Wiring it to the real answer keeps the gate meaningful
        /// instead of turning it into a rubber stamp.
        /// </summary>
        private static TimeProfileRepository Repository(bool allowed) =>
            new TimeProfileRepository
            {
                PermissionCheck = () => allowed ? PermissionState.Granted : PermissionState.Denied
            };

        private static string Translate(SaveStatus status)
        {
            switch (status)
            {
                case SaveStatus.Success: return ResponseStatus.Ok;
                case SaveStatus.NothingToDo: return ResponseStatus.NothingToDo;
                case SaveStatus.PermissionDenied: return ResponseStatus.Denied;
                default: return ResponseStatus.Failed;
            }
        }

        private static ServerResponse Ok(string correlationId, string message) =>
            new ServerResponse
            {
                CorrelationId = correlationId,
                Status = ResponseStatus.Ok,
                Message = message
            };

        private static ServerResponse Denied(string correlationId, string message) =>
            new ServerResponse
            {
                CorrelationId = correlationId,
                Status = ResponseStatus.Denied,
                Message = message ?? "Du saknar behörighet att ändra denna tidsprofil."
            };

        private static ServerResponse Failed(string correlationId, string message) =>
            new ServerResponse
            {
                CorrelationId = correlationId,
                Status = ResponseStatus.Failed,
                Message = message ?? "Det gick inte att spara ändringarna."
            };
    }
}
