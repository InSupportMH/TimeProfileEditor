using System;
using System.Collections.Generic;
using System.Threading;
using TimeProfileEditor.Protocol;
using VideoOS.Platform;
using VideoOS.Platform.Login;
using VideoOS.Platform.Messaging;

namespace TimeProfileEditor.Services
{
    /// <summary>
    /// Talks to the Event Server component over the MIP message channel.
    ///
    /// The channel is a bus: a message goes out, and every listener sees it. So each request carries
    /// a correlation id and the reply is matched on it - two operators saving at the same moment
    /// would otherwise be able to read each other's answers, and a slow reply would be picked up by
    /// whoever asked next.
    ///
    /// Everything here blocks. The callers are already on a background thread - the view model runs
    /// repository work there so the UI keeps painting - and a request that needs an answer before it
    /// can report success is easier to get right as a blocking call than as a continuation.
    ///
    /// The channel carries no authority of its own. It presents the caller's token and identity and
    /// the component decides everything; nothing here is a permission check, and a request that
    /// should be refused is refused at the other end.
    /// </summary>
    internal sealed class ServerComponentChannel
    {
        /// <summary>
        /// How long to wait for a reply. Generous, because the answer travels through the Event
        /// Server to the Management Server and back, and the alternative to waiting is telling an
        /// operator their save failed when it may well be about to succeed.
        /// </summary>
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Shorter, because this one only asks whether anybody is there. A component that has not
        /// answered in ten seconds is treated as absent, and the client falls back to writing
        /// directly - which is the right behaviour on Corporate, where there is no component and
        /// none is needed.
        /// </summary>
        private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

        private static readonly object Sync = new object();
        private static bool _started;

        /// <summary>
        /// Response ids already listened for, and the callers waiting on an answer.
        ///
        /// One filter per message id for the life of the process, not one per request. The first
        /// version registered a filter per call and unregistered it afterwards, which the Event
        /// Server's own broker objected to out loud - measured on philip-pc at 17:43:07:
        ///
        ///     ERROR - MessageBroker  Same messageId being registered multiple times:
        ///             TimeProfileEditor.Ping.Response  philip.test
        ///
        /// Two overlapping requests are ordinary here - the workspace loads a list, checks the route
        /// and may save, all on background threads - so the registration has to be something that
        /// happens once rather than something each request does and undoes.
        /// </summary>
        private static readonly HashSet<string> Listening = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Waiter> Waiting =
            new Dictionary<string, Waiter>(StringComparer.Ordinal);

        private sealed class Waiter
        {
            public readonly ManualResetEventSlim Arrived = new ManualResetEventSlim(false);
            public ServerResponse Response;
        }

        /// <summary>Serialises the ping, so a burst of callers asks the component once.</summary>
        private static readonly object PingSync = new object();

        /// <summary>Last thing the component said about this user, and when.</summary>
        private static ServerResponse _lastPing;
        private static DateTime _pingedAt = DateTime.MinValue;

        /// <summary>
        /// Whether the component is installed and what it says about this user, asked at most
        /// occasionally.
        ///
        /// A positive answer is held longer than a negative one on purpose. The usual reason for a
        /// negative is that the Event Server is still starting - its message service is measurably
        /// not up for the first ten seconds or so - and a client that decided "no component" once at
        /// login and never looked again would stay wrong until the operator restarted Smart Client.
        /// </summary>
        public ServerResponse Availability()
        {
            // Held across the round trip on purpose. Several callers reach this at once when the
            // workspace opens, and without it they would each spend ten seconds waiting for the
            // same silence.
            lock (PingSync)
            {
                if (Fresh(out var cached)) return cached;

                var answer = Send(ServerProtocol.PingRequest, NewRequest(), PingTimeout);

                lock (Sync)
                {
                    _lastPing = answer;
                    _pingedAt = DateTime.UtcNow;
                }

                return answer;
            }
        }

        private static bool Fresh(out ServerResponse cached)
        {
            lock (Sync)
            {
                cached = _lastPing;
                if (_lastPing == null) return false;

                var keep = _lastPing.Status == ResponseStatus.Ok
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromSeconds(45);

                return DateTime.UtcNow - _pingedAt < keep;
            }
        }

        /// <summary>Drops what we think we know, so the next question is asked afresh.</summary>
        public static void Forget()
        {
            lock (Sync)
            {
                _lastPing = null;
                _pingedAt = DateTime.MinValue;
            }
        }

        public ServerResponse LoadProfiles() =>
            Send(ServerProtocol.LoadRequest, NewRequest(), ReplyTimeout);

        public ServerResponse LoadSchedule(Guid profileId)
        {
            var request = NewRequest();
            request.ProfileId = profileId.ToString();
            return Send(ServerProtocol.LoadRequest, request, ReplyTimeout);
        }

        public ServerResponse Save(Guid profileId, System.Collections.Generic.IReadOnlyList<Model.ScheduleEntry> desired,
                                   System.Collections.Generic.IReadOnlyList<Model.ScheduleEntry> baseline,
                                   DateTime expectedLastModified)
        {
            var request = NewRequest();
            request.ProfileId = profileId.ToString();
            request.Entries = WireEntry.From(desired);
            request.Baseline = WireEntry.From(baseline);
            request.ExpectedLastModified =
                expectedLastModified.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            return Send(ServerProtocol.SaveRequest, request, ReplyTimeout);
        }

        // ---- plumbing ---------------------------------------------------------------------

        /// <summary>
        /// A request carrying who is asking. Both halves of that travel: the token is what proves
        /// it, the identity is the spelling the Management Server's permission API needs. The
        /// component requires them to agree before it believes either.
        /// </summary>
        private static ClientRequest NewRequest()
        {
            var settings = CurrentLogin();

            return new ClientRequest
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                Token = settings?.IdentityTokenCache?.Token,
                Identity = settings?.UserIdentity
            };
        }

        /// <summary>
        /// Sends and waits for the reply with the matching correlation id, or null if none came.
        ///
        /// Null means "no answer", never "no". A timeout and a refusal must not be reported the same
        /// way: a refusal is about the user and a silence is about the installation, and telling an
        /// operator they lack a permission when the Event Server is simply down sends them to their
        /// administrator with the wrong question.
        /// </summary>
        private ServerResponse Send(string requestId, ClientRequest request, TimeSpan timeout)
        {
            var responseId = requestId.Replace(".Request", ".Response");

            try
            {
                var messages = Messages();
                if (messages == null) return null;

                Listen(messages, responseId);

                var waiter = new Waiter();
                lock (Sync) Waiting[request.CorrelationId] = waiter;

                try
                {
                    messages.TransmitMessage(
                        new Message(requestId, ServerProtocol.ToJson(request)), null, null, null);

                    if (waiter.Arrived.Wait(timeout)) return waiter.Response;

                    ChangeLog.Info($"Serverkomponenten svarade inte inom " +
                                   $"{timeout.TotalSeconds:0} s på {requestId}.");
                    return null;
                }
                finally
                {
                    // Removed but never disposed. The handler may already hold this waiter and be
                    // about to signal it, and disposing underneath it would throw inside a MIP
                    // callback - where the exception belongs to nobody.
                    lock (Sync) Waiting.Remove(request.CorrelationId);
                }
            }
            catch (Exception ex)
            {
                ChangeLog.Error("Kunde inte tala med serverkomponenten", ex);
                return null;
            }
        }

        private static void Listen(MessageCommunication messages, string responseId)
        {
            lock (Sync)
            {
                if (!Listening.Add(responseId)) return;

                messages.RegisterCommunicationFilter(
                    OnResponse, new CommunicationIdFilter(responseId));
            }
        }

        /// <summary>
        /// Hands a reply to whoever is waiting for it, by correlation id.
        ///
        /// An answer nobody is waiting for is dropped without comment. Several clients share this
        /// bus and most of what arrives here belongs to one of the others; treating that as an error
        /// would fill the log with other people's traffic.
        /// </summary>
        private static object OnResponse(Message message, FQID destination, FQID source)
        {
            try
            {
                var response = ServerProtocol.FromJson<ServerResponse>(message.Data as string);
                if (string.IsNullOrEmpty(response?.CorrelationId)) return null;

                Waiter waiter;
                lock (Sync)
                {
                    if (!Waiting.TryGetValue(response.CorrelationId, out waiter)) return null;
                    Waiting.Remove(response.CorrelationId);
                }

                waiter.Response = response;
                waiter.Arrived.Set();
            }
            catch (Exception ex)
            {
                ChangeLog.Error("Kunde inte tolka svaret från serverkomponenten", ex);
            }

            return null;
        }

        private static MessageCommunication Messages()
        {
            var serverId = CurrentServerId();
            if (serverId == null) return null;

            lock (Sync)
            {
                if (!_started)
                {
                    MessageCommunicationManager.Start(serverId);
                    _started = true;
                }
            }

            return MessageCommunicationManager.Get(serverId);
        }

        private static ServerId CurrentServerId() =>
            EnvironmentManager.Instance.MasterSite?.ServerId ?? Configuration.Instance.ServerFQID?.ServerId;

        private static LoginSettings CurrentLogin()
        {
            var serverId = CurrentServerId();
            return serverId == null ? null : LoginSettingsCache.GetLoginSettings(serverId);
        }
    }
}
