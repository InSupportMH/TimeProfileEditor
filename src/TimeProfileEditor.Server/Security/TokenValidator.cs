using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TimeProfileEditor.Security;
using VideoOS.Platform;

namespace TimeProfileEditor.Server.Security
{
    /// <summary>Who the server decided is asking, and whether it believes them.</summary>
    internal sealed class CallerIdentity
    {
        public bool IsAuthentic { get; set; }

        /// <summary>The user's SID, as the token states it. Null when it could not be read.</summary>
        public string Subject { get; set; }

        /// <summary>Human-readable name, for the audit trail. Never used to decide anything.</summary>
        public string Name { get; set; }

        /// <summary>Why the caller was not accepted. Null when they were.</summary>
        public string Failure { get; set; }

        public string Describe() => Name == null ? Subject ?? "okänd" : $"{Name} ({Subject})";

        /// <summary>
        /// Whether the identity a request names is the one its token proves.
        ///
        /// The permission question has to be asked with the spelling XProtect uses - the client's
        /// LoginSettings.UserIdentity - while the only thing that proves who is asking is the
        /// token. So the request carries the first and this binds it to the second. Neither alone
        /// would do: an unbound identity is a suggestion, and a subject claim is whatever the
        /// identity provider decided to write there.
        ///
        /// If they disagree, both are written to the log. Should they turn out to be different
        /// spellings of the same person on some system, that line is what will say so - and until
        /// it does, refusing is the only honest answer.
        /// </summary>
        public bool Owns(string claimed, out string refusal)
        {
            refusal = null;

            if (string.IsNullOrWhiteSpace(claimed))
            {
                refusal = "Begäran uppgav ingen identitet.";
                return false;
            }

            if (Normalise(claimed) == Normalise(Subject)) return true;

            ServerLog.Info($"Identiteten i begäran ('{claimed}') stämmer inte med den token " +
                           $"beskriver ('{Subject}'). Begäran avvisas.");
            refusal = "Identiteten i begäran stämmer inte med inloggningen som följde med den.";
            return false;
        }

        private static string Normalise(string value) =>
            value?.Trim().Trim('{', '}').ToUpperInvariant() ?? string.Empty;

        public static CallerIdentity Reject(string why) =>
            new CallerIdentity { IsAuthentic = false, Failure = why };
    }

    /// <summary>
    /// Establishes who a request came from.
    ///
    /// The MIP message channel carries no identity - a message says whatever its sender put in it -
    /// so the client presents its own bearer token and this decides what to make of it. Two
    /// separate questions, answered in this order and never merged:
    ///
    ///   1. Is the token genuine? Asked of the Management Server, by presenting it. Measured on a
    ///      2025 R2 Professional+ system:
    ///
    ///          /timeProfiles  genuine token              -> 200
    ///          /timeProfiles  one signature byte changed -> 401
    ///          /roles         genuine token              -> 403   (recognised, not permitted)
    ///          /roles         one signature byte changed -> 401   (not recognised at all)
    ///
    ///      The signature is therefore checked by XProtect itself, which is the only party that
    ///      can, and this component never has to hold a signing key. The last two lines are what
    ///      settle it: 403 and 401 are different answers, so the token was read rather than
    ///      ignored - a distinction /timeProfiles cannot make for a user who may see no profiles,
    ///      because "recognised, and there is nothing here for you" is also a 200 with an empty
    ///      body.
    ///
    ///   2. Whose is it? Read from the token's own payload, but *only after* step 1 succeeded.
    ///      Reading claims from an unverified JWT would be reading an attacker's own description
    ///      of themselves; reading them from one XProtect has just vouched for is not.
    ///
    /// A bearer token is holder-of-key-less: whoever presents it is treated as the user. That is
    /// the same trust model the rest of XProtect uses for the same token, so the component is not
    /// weaker than the system it runs in - but it does mean the channel must stay inside the VMS.
    /// </summary>
    internal static class TokenValidator
    {
        /// <summary>
        /// How long an accepted token is trusted without asking again. Short enough that a
        /// disabled user stops getting through quickly, long enough that a burst of requests from
        /// one editor session does not become a burst of round trips.
        /// </summary>
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, CachedResult> Cache =
            new Dictionary<string, CachedResult>(StringComparer.Ordinal);

        private sealed class CachedResult
        {
            public CallerIdentity Identity;
            public DateTime Expires;
        }

        public static CallerIdentity Validate(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return CallerIdentity.Reject("Ingen token skickades med begäran.");

            // Keyed by a digest, so the tokens themselves are never held in a dictionary that
            // outlives the request or could end up in a memory dump under a readable name.
            var key = Digest(token);

            lock (Sync)
            {
                if (Cache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
                    return cached.Identity;
            }

            var identity = Establish(token);

            lock (Sync)
            {
                Prune();
                Cache[key] = new CachedResult
                {
                    Identity = identity,
                    // A refusal is cached far more briefly. The usual cause is an expired token,
                    // and the client will have a fresh one moments later.
                    Expires = DateTime.UtcNow + (identity.IsAuthentic ? CacheFor : TimeSpan.FromSeconds(10))
                };
            }

            return identity;
        }

        private static CallerIdentity Establish(string token)
        {
            var serverId = EnvironmentManager.Instance.MasterSite?.ServerId;
            if (serverId == null)
                return CallerIdentity.Reject("Serverkomponenten vet inte vilken Management Server " +
                                             "den ska kontrollera token mot.");

            var probe = new UriBuilder(serverId.ServerScheme ?? "http", serverId.ServerHostname,
                serverId.Serverport, "/api/rest/v1/timeProfiles").Uri;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(probe);
                request.Method = "GET";
                request.Timeout = 15000;
                request.Headers["Authorization"] = "Bearer " + token;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        return CallerIdentity.Reject(
                            $"Management Server svarade {(int)response.StatusCode} på tokenkontrollen.");
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && (response.StatusCode == HttpStatusCode.Unauthorized ||
                                         response.StatusCode == HttpStatusCode.Forbidden))
                    return CallerIdentity.Reject("Management Server godtog inte den token som skickades med.");

                // A server that cannot be reached is not the same as a token that was refused, and
                // must not be treated as one - but it is still a refusal, because the alternative
                // is acting on an unverified claim whenever the network is having a bad day.
                return CallerIdentity.Reject("Kunde inte nå Management Server för att kontrollera " +
                                             "token: " + ex.Message);
            }
            catch (Exception ex)
            {
                return CallerIdentity.Reject("Tokenkontrollen misslyckades: " +
                                             ex.GetBaseException().Message);
            }

            // Safe only here: the call above established that XProtect accepts this exact token.
            // Reading claims from an unverified JWT would be reading an attacker's own description
            // of themselves and writing it down as fact.
            var claims = JwtReader.ReadClaims(token);
            var subject = JwtReader.First(claims, "sub", "sid", "nameid", "user_id",
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (string.IsNullOrEmpty(subject))
                return CallerIdentity.Reject("Token godtogs av servern men innehåller ingen " +
                                             "identitet som går att koppla till en behörighet.");

            return new CallerIdentity
            {
                IsAuthentic = true,
                Subject = subject,
                Name = JwtReader.First(claims, "name", "preferred_username", "unique_name",
                    "upn", "client_id")
            };
        }

        private static string Digest(string token)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token)))
                    .Replace("-", string.Empty);
        }

        private static void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var stale in Cache.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                Cache.Remove(stale);
        }
    }
}
