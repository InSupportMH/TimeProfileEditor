using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;
using VideoOS.Platform.Login;
using VideoOS.Platform.Util;

namespace TimeProfileEditor.Security
{
    /// <summary>
    /// A single block of text describing everything that decides whether this plugin works on the
    /// machine it is running on.
    ///
    /// It exists because the interesting cases happen on someone else's server: the banner has room
    /// for one sentence, and copying a diagnostics tool onto a production machine is a bigger ask
    /// than pressing a button. So the plugin can describe itself, and the description can be pasted
    /// into a support thread.
    ///
    /// Everything here is read-only.
    /// </summary>
    internal static class Diagnostics
    {
        /// <param name="includeProbes">
        /// Whether to make the extra server calls that test token validation. On when a person asked
        /// for the report; off when it is written automatically at startup, where a handful of
        /// unasked-for round trips on every launch would be a poor trade.
        /// </param>
        public static string Report(bool includeProbes = true)
        {
            var text = new StringBuilder();
            text.AppendLine("=== Tidsprofiler - diagnostik ===");
            text.AppendLine("Tidpunkt          : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            Section(text, "Plugin", () =>
            {
                text.AppendLine("Version           : " + Assembly.GetExecutingAssembly().GetName().Version);
                // Which copy is running. MIP scans several MIPPlugins folders and keeps the first
                // plugin with a given id, so a stray second copy - a diagnostics folder, an older
                // hand-placed one - quietly wins over the installed one and the MSI appears to have
                // no effect. The path is the only thing that tells them apart.
                text.AppendLine("Laddad från       : " + Safely(() =>
                {
                    var location = Assembly.GetExecutingAssembly().Location;
                    return string.IsNullOrEmpty(location) ? "(okänd)" : location;
                }));
                text.AppendLine("Utgåva i paketet  : " + SystemEdition.Configured);
                text.AppendLine("Byggt mot MIP SDK : " + SystemEdition.CompiledAgainstMipSdk);
                text.AppendLine("Plattform i drift : " + SystemEdition.RunningMipPlatform);
                text.AppendLine("Värdprocess       : " + EnvironmentManager.Instance.EnvironmentType);
            });

            var serverId = EnvironmentManager.Instance.MasterSite?.ServerId
                           ?? Configuration.Instance.ServerFQID?.ServerId;
            var settings = serverId == null ? null : LoginSettingsCache.GetLoginSettings(serverId);

            Section(text, "Inloggning", () =>
            {
                text.AppendLine("Server            : " + Describe(serverId));
                text.AppendLine("Servernamn        : " + Configuration.Instance.ServerName);
                text.AppendLine("Användare         : " + settings?.FullyQualifiedUserName);
                text.AppendLine("Identitet (SID)   : " + settings?.UserIdentity);
                text.AppendLine("ServerType        : " + settings?.ServerType);
                text.AppendLine("Basic-användare   : " + settings?.IsBasicUser);
            });

            // What the client holds that could prove to a server-side component who it is. The
            // token itself is never written here: it is a bearer credential, and this text is
            // built to be pasted into a support thread.
            Section(text, "Identitetsbärare", () =>
            {
                text.AppendLine("OAuth-identitet   : " + settings?.IsOAuthIdentity);
                text.AppendLine("Klassisk token    : " + Presence(settings?.Token));
                text.AppendLine("Token giltig till : " + settings?.TokenTimeToLive.ToString("yyyy-MM-dd HH:mm:ss"));

                var cache = settings?.IdentityTokenCache;
                if (cache == null)
                {
                    text.AppendLine("IdentityTokenCache: (saknas)");
                    return;
                }

                text.AppendLine("IdentityTokenCache: " + Presence(cache.Token));
                text.AppendLine("Utgången          : " + Safely(() => cache.IsTokenExpired().ToString()));
                foreach (var claim in new[] { "sub", "name", "preferred_username", "iss", "aud", "client_id" })
                    text.AppendLine(("Claim " + claim).PadRight(18) + ": " +
                                    Safely(() => cache.GetClaimValue(claim)));

                // GetClaimValue reads the id token, which a basic user does not get - so the lines
                // above can all fail while the access token in hand is a perfectly good JWT. The
                // server component has to find an identity in *that* one, so this reports which
                // names it carries. Names only: the values identify a person, and this text is
                // written to be pasted into a support thread.
                text.AppendLine("Claims i token    : " + Safely(() =>
                {
                    var names = JwtReader.ReadClaims(cache.Token).Keys.ToList();
                    return names.Count == 0 ? "(inga - token är inte en läsbar JWT)" : string.Join(", ", names);
                }));
            });

            // Whether a server-side component could establish who this client is. MIP's message
            // channel carries no identity, so the client has to present a credential - and that is
            // only worth anything if the server verifies it with the issuer instead of believing
            // the claim. Deliberately corrupted copies are sent alongside the real one: the answer
            // that matters is that they are refused.
            Section(text, "Tokenvalidering", () =>
            {
                if (!includeProbes)
                {
                    text.AppendLine("(hoppades över - tryck \"Kopiera diagnostik\" för att testa mot servern)");
                    return;
                }

                var token = settings?.IdentityTokenCache?.Token;
                if (string.IsNullOrEmpty(token))
                {
                    text.AppendLine("Ingen JWT att presentera - serverkomponenten kan inte känna igen " +
                                    "den här klienten.");
                    return;
                }

                if (serverId == null)
                {
                    text.AppendLine("Ingen server att fråga.");
                    return;
                }

                var profiles = Endpoint(serverId, "/api/rest/v1/timeProfiles");

                text.AppendLine("Endpunkt          : " + profiles);
                text.AppendLine("Äkta token        : " + Attempt(profiles, token));
                text.AppendLine("Ändrad nyttolast  : " + Forged(profiles, token, 1) + "   (ska vara 401)");
                text.AppendLine("Ändrad signatur   : " + Forged(profiles, token, 2) + "   (ska vara 401)");

                // That endpoint cannot settle the question on its own for a user who may see
                // nothing. "Recognised, allowed, and there is nothing here for you" and "not
                // recognised at all" can both come back as 200 with an empty list, and an empty
                // list is what this user gets. This one separates them: a genuine token is answered
                // 403 - the server knows who is asking and refuses the operation - where an unknown
                // caller gets 401. The two answers being different is itself the proof that the
                // token was read and checked, and it is the proof the server component rests on.
                var roles = Endpoint(serverId, "/api/rest/v1/roles");

                text.AppendLine("Kontrollendpunkt  : " + roles);
                text.AppendLine("  äkta token      : " + Attempt(roles, token) + "   (403 = igenkänd)");
                text.AppendLine("  ändrad signatur : " + Forged(roles, token, 2) + "   (401 = okänd)");
            });

            Section(text, "Roller", () =>
            {
                var roles = settings?.GroupMemberShip;
                text.AppendLine("Söker efter       : " + SecurityAccess.AdministratorRoleId + " (Administrators)");
                if (roles == null)
                    text.AppendLine("Rollista          : (ingen skickades med inloggningen)");
                else if (roles.Length == 0)
                    text.AppendLine("Rollista          : (tom)");
                else
                    foreach (var role in roles)
                        text.AppendLine("Roll              : " + role);

                text.AppendLine("Administratör     : " + SystemEdition.AdministratorState() +
                                " (avgjort av " + SystemEdition.AdminSource + ")");
            });

            Section(text, "Behörighetsläge", () =>
            {
                text.AppendLine("Bygge             : " + SystemEdition.Configured);
                text.AppendLine("Produktnivå       : " + SystemEdition.ProductDescription());
                text.AppendLine("Orsak             : " + SystemEdition.Reason);
                text.AppendLine("Konfig-åtkomst    : " + SystemEdition.ConfigurationAccess());
                if (!string.IsNullOrEmpty(SystemEdition.ConfigAccessError))
                    text.AppendLine("  detalj          : " + SystemEdition.ConfigAccessError);
            });

            // The control group. XProtect answers a caller without configuration rights by handing
            // back the items they may see rather than by refusing, so an empty time profile list on
            // its own says nothing. If these are empty too it is the user's rights; if only the time
            // profiles are, the problem is with them.
            Section(text, "Configuration API", () =>
            {
                if (serverId == null)
                {
                    text.AppendLine("Ingen server att fråga.");
                    return;
                }

                ManagementServer ms;
                try
                {
                    ms = new ManagementServer(serverId);
                    text.AppendLine("ManagementServer  : " + ms.ComputerName + "." + ms.DomainName +
                                    " (version " + ms.Version + ")");
                }
                catch (Exception ex)
                {
                    text.AppendLine("ManagementServer  : FEL " + Explain(ex));
                    return;
                }

                Count(text, "Inspelningsservrar", () => ms.RecordingServerFolder?.RecordingServers?.Count);
                Count(text, "Roller", () => ms.RoleFolder?.Roles?.Count);
                Count(text, "Kameragrupper", () => ms.CameraGroupFolder?.CameraGroups?.Count);
                Count(text, "Anv.def. händelser", () => ms.UserDefinedEventFolder?.UserDefinedEvents?.Count);

                try
                {
                    var folder = ms.TimeProfileFolder;
                    if (folder == null)
                    {
                        text.AppendLine("TimeProfileFolder : null");
                        return;
                    }

                    folder.ClearChildrenCache();
                    var profiles = folder.TimeProfiles ?? new List<TimeProfile>();
                    text.AppendLine("Tidsprofiler      : " + profiles.Count + " st");
                    foreach (var profile in profiles)
                        text.AppendLine("  " + profile.Name + " [" + profile.TimeProfileType + "]");
                }
                catch (Exception ex)
                {
                    text.AppendLine("Tidsprofiler      : FEL " + Explain(ex));
                }
            });

            Section(text, "Vad kontrollen svarar", () =>
            {
                foreach (var action in new[] { SecurityActionIds.View, SecurityActionIds.Edit })
                {
                    var state = PluginSecurity.Evaluate(action);
                    text.AppendLine(action.PadRight(28) + " = " + state);
                    if (!string.IsNullOrEmpty(PluginSecurity.LastStrategy))
                        text.AppendLine("  avgjordes av    : " + PluginSecurity.LastStrategy);
                    if (!string.IsNullOrEmpty(PluginSecurity.LastError))
                        text.AppendLine("  fel             : " + PluginSecurity.LastError);
                }
            });

            return text.ToString();
        }

        /// <summary>
        /// Describes a credential without disclosing it. Length and shape are enough to tell a JWT
        /// from an opaque ticket from nothing at all, and none of it is usable by a reader.
        /// </summary>
        private static string Presence(string secret)
        {
            if (secret == null) return "(null)";
            if (secret.Length == 0) return "(tom)";

            var shape = secret.Count(c => c == '.') == 2 ? "JWT" : "ogenomskinlig";
            return $"finns, {secret.Length} tecken, {shape}";
        }

        private static Uri Endpoint(ServerId serverId, string path) =>
            new UriBuilder(serverId.ServerScheme ?? "http", serverId.ServerHostname,
                serverId.Serverport, path).Uri;

        private static string Forged(Uri uri, string token, int segment)
        {
            var tampered = Tamper(token, segment);
            return tampered == null ? "(gick inte att bygga)" : Attempt(uri, tampered);
        }

        /// <summary>
        /// A copy of the token with one character changed inside the segment given - 1 is the
        /// payload, 2 the signature - or null when the token has no such segment.
        ///
        /// The character is taken from the middle, and that detail is the whole test. Base64 packs
        /// six bits into every character, so the *last* character of a segment carries bits the
        /// decoded bytes never use: four of them for a 256-byte RS256 signature, which means
        /// sixteen different final characters decode to one and the same signature. Changing the
        /// last character is therefore a one-in-four chance of producing a token that is not
        /// altered at all, and the report then reads as the server accepting a forgery when nothing
        /// was forged. That is not a thought experiment - it happened here on 2026-08-11, when the
        /// same server on the same endpoint refused the tampered token for one user and accepted it
        /// for the next.
        ///
        /// A character in the middle has no spare bits to hide in. Change it and the bytes differ,
        /// so the signed content differs, so any server that checks signatures must refuse it.
        /// </summary>
        private static string Tamper(string token, int segment)
        {
            var parts = token.Split('.');
            if (parts.Length <= segment || parts[segment].Length < 3) return null;

            var chars = parts[segment].ToCharArray();
            var at = chars.Length / 2;
            chars[at] = chars[at] == 'a' ? 'b' : 'a';
            parts[segment] = new string(chars);

            return string.Join(".", parts);
        }

        /// <summary>
        /// Calls an endpoint with a bearer token and reports the outcome, plus how many items came
        /// back. The items themselves are left out - they are configuration, and this text is
        /// written to be pasted somewhere - but the count has to be there: a 200 carrying an empty
        /// list and a 200 carrying the real profiles are the same status code and completely
        /// different answers about what this user may reach.
        /// </summary>
        private static string Attempt(Uri uri, string token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET";
                request.Timeout = 15000;
                request.Headers["Authorization"] = "Bearer " + token;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var outcome = (int)response.StatusCode + " " + response.StatusCode;
                    var count = CountItems(response);
                    return count == null ? outcome : outcome + ", " + count + " poster";
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                return response == null
                    ? ex.Message
                    : (int)response.StatusCode + " " + response.StatusCode;
            }
            catch (Exception ex)
            {
                return "FEL " + ex.GetBaseException().Message;
            }
        }

        /// <summary>
        /// How many objects a REST collection response carries, or null when the body is not one.
        ///
        /// The API Gateway wraps collections as {"array":[...]}. Counting the top-level braces
        /// inside that array is enough and keeps this dependency-free; the alternative is dragging
        /// a JSON parser into a plugin that otherwise needs none, for a diagnostic line.
        /// </summary>
        private static int? CountItems(HttpWebResponse response)
        {
            try
            {
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null) return null;
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var body = reader.ReadToEnd();
                        var start = body.IndexOf("\"array\"", StringComparison.Ordinal);
                        if (start < 0) return null;

                        start = body.IndexOf('[', start);
                        if (start < 0) return null;

                        var depth = 0;
                        var items = 0;
                        for (var i = start + 1; i < body.Length; i++)
                        {
                            var c = body[i];
                            if (c == '{')
                            {
                                if (depth == 0) items++;
                                depth++;
                            }
                            else if (c == '}') depth--;
                            else if (c == ']' && depth == 0) break;
                        }

                        return items;
                    }
                }
            }
            catch
            {
                // A body that cannot be read tells us nothing, and the status code already did the
                // job this probe exists for.
                return null;
            }
        }

        private static string Safely(Func<string> read)
        {
            try
            {
                return read() ?? "(null)";
            }
            catch (Exception ex)
            {
                return "FEL " + ex.GetBaseException().Message;
            }
        }

        private static void Section(StringBuilder text, string title, Action body)
        {
            text.AppendLine();
            text.AppendLine("--- " + title + " ---");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                text.AppendLine("FEL: " + Explain(ex));
            }
        }

        private static void Count(StringBuilder text, string label, Func<int?> read)
        {
            try
            {
                text.AppendLine(label.PadRight(18) + ": " + (read()?.ToString() ?? "null"));
            }
            catch (Exception ex)
            {
                text.AppendLine(label.PadRight(18) + ": FEL " + Explain(ex));
            }
        }

        private static string Describe(ServerId id) =>
            id == null
                ? "(null)"
                : id.ServerHostname + ":" + id.Serverport + " typ=" + id.ServerType + " id=" + id.Id;

        /// <summary>The whole exception chain. Configuration API faults hide the useful part inside.</summary>
        private static string Explain(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                parts.Add(e.GetType().Name + ": " + e.Message);
            return string.Join(" <- ", parts);
        }
    }
}
