using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using TimeProfileEditor.Model;
using TimeProfileEditor.Security;
using TimeProfileEditor.Services;

namespace TimeProfileEditor.Harness
{
    /// <summary>
    /// Exercises the repository against a real Management Server.
    ///
    /// The read checks are safe to run anywhere. The write checks (--write) create and modify a
    /// throwaway profile and are meant for a lab system, never production.
    ///
    ///   TimeProfileEditor.Harness.exe [--server http://localhost] [--write]
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        private static int Main(string[] args)
        {
            var server = ArgValue(args, "--server") ?? "http://localhost";
            var write = args.Contains("--write", StringComparer.OrdinalIgnoreCase);

            try
            {
                Connect(server);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Kunde inte logga in: " + ex.GetBaseException().Message);
                return 2;
            }

            var repository = new TimeProfileRepository();

            if (args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
            {
                Diagnose();
                return 0;
            }

            // The exact text the plugin's own "Kopiera diagnostik" button produces, so the two can
            // be compared and the report is exercised by something other than a mouse click.
            if (args.Contains("--report", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(Security.Diagnostics.Report());
                return 0;
            }

            if (args.Contains("--tokenprobe", StringComparer.OrdinalIgnoreCase))
            {
                ProbeTokenValidation(server);
                return 0;
            }

            // The write checks leave their scratch profile behind so a failing run can be inspected.
            // This is how it gets tidied away afterwards.
            if (args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Tar bort testprofilen 'TEST - Harness'...");
                DeleteProfile("TEST - Harness");
                Console.WriteLine("Klart.");
                return 0;
            }

            RunReadChecks(repository);
            CheckTimeParsing();
            CheckCalendarCoverage();
            CheckPermissionClassification();
            CheckSaveRouting();
            CheckPluginInfo();

            if (write) RunWriteChecks(repository);
            else Console.WriteLine("\n(Skrivtester hoppades över - kör med --write för att inkludera dem.)");

            Console.WriteLine($"\n===== {_passed} godkända, {_failed} underkända =====");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Reports what the server thinks about this plugin's permissions, without changing
        /// anything. Run it when the plugin says the permission could not be checked.
        /// </summary>
        private static void Diagnose()
        {
            Section("Diagnostik");

            var serverId = VideoOS.Platform.EnvironmentManager.Instance.MasterSite?.ServerId
                           ?? VideoOS.Platform.Configuration.Instance.ServerFQID?.ServerId;
            Console.WriteLine($"  Server            : {serverId?.ServerHostname}");

            var settings = serverId == null
                ? null
                : VideoOS.Platform.Login.LoginSettingsCache.GetLoginSettings(serverId);
            Console.WriteLine($"  Anvandare         : {settings?.FullyQualifiedUserName}");
            Console.WriteLine($"  SID (identity)    : {settings?.UserIdentity}");
            Console.WriteLine($"  Plugin-id         : {PluginIds.PluginDefinition}");
            Console.WriteLine($"  ServerType        : {settings?.ServerType}");
            Console.WriteLine($"  Basic-anvandare   : {settings?.IsBasicUser}");

            Console.WriteLine("\n  --- MIP-versioner ---");
            Console.WriteLine($"  Pluginet byggt mot: {SystemEdition.CompiledAgainstMipSdk}");
            Console.WriteLine($"  Plattform i drift : {SystemEdition.RunningMipPlatform}");
            Console.WriteLine("  (Ett plugin laddas i den XProtect-version det byggts mot och senare, aldrig tidigare.)");

            Console.WriteLine("\n  --- Produktniva och behorighetslage ---");
            try
            {
                var license = VideoOS.Platform.EnvironmentManager.Instance.SystemLicense;
                var differentiated = license?.IsFeatureEnabled(SystemEdition.DifferentiatedAdminFeature);
                Console.WriteLine($"  {SystemEdition.DifferentiatedAdminFeature} : {differentiated?.ToString() ?? "okand"}");
                Console.WriteLine($"  Inbyggt i paketet : {SystemEdition.Configured}");
                Console.WriteLine($"  Produktniva       : {SystemEdition.ProductDescription()}");
                Console.WriteLine($"  Orsak             : {SystemEdition.Reason}");
                Console.WriteLine($"  Administrator     : {SystemEdition.AdministratorState()} ({SystemEdition.AdminSource})");
                Console.WriteLine($"  Roller            : {string.Join(", ", settings?.GroupMemberShip ?? new string[0])}");
                Console.WriteLine($"  Admin-roll-id     : {VideoOS.Platform.Util.SecurityAccess.AdministratorRoleId}");
                Console.WriteLine($"  Konfig-atkomst    : {SystemEdition.ConfigurationAccess()}");
                if (!string.IsNullOrEmpty(SystemEdition.ConfigAccessError))
                    Console.WriteLine($"      fel           : {SystemEdition.ConfigAccessError}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Kunde inte lasa licensinformationen: " + ex.GetBaseException().Message);
            }

            Console.WriteLine("\n  --- Sakerhetsnamnrymd pa servern ---");
            try
            {
                var client = new VideoOS.Platform.Proxy.SecurityApi.SecurityApiClient();
                client.Initialize(settings, false);

                // Listing namespaces only exists from MIP SDK 26.1, and this tool is compiled
                // against the older SDK the plugin targets. Asked for reflectively so the report is
                // richer on a new platform without the tool refusing to build for an old one.
                var ours = TryFindNamespace(client);
                if (ours == null)
                {
                    Console.WriteLine("  (Plattformen kan inte lista namnrymder - hoppar over.)");
                }
                else if (ours == NoSuchNamespace)
                {
                    Console.WriteLine("  Namnrymden saknas. Management Client har inte laddat pluginet an.");
                }
                else
                {
                    Console.WriteLine("  Hittad namnrymd:");
                    foreach (var line in (IEnumerable<string>)ours)
                        Console.WriteLine("      " + line);
                }

                foreach (var action in new[] { SecurityActionIds.View, SecurityActionIds.Edit })
                    Console.WriteLine($"  HasPermission({action}) = {client.HasPermission(PluginIds.PluginDefinition, action)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  SecurityApi misslyckades: " + ex.GetBaseException().Message);
            }

            Console.WriteLine("\n  --- Vad pluginet skulle svara ---");
            foreach (var action in new[] { SecurityActionIds.View, SecurityActionIds.Edit })
            {
                PluginSecurity.Reset();
                var state = PluginSecurity.Evaluate(action);
                Console.WriteLine($"  {action,-28} = {state}");
                if (!string.IsNullOrEmpty(PluginSecurity.LastStrategy))
                    Console.WriteLine($"      avgjordes av : {PluginSecurity.LastStrategy}");
                if (!string.IsNullOrEmpty(PluginSecurity.LastError))
                    Console.WriteLine($"      fel          : {PluginSecurity.LastError}");
            }

            DiagnoseConfiguration();

            Console.WriteLine("\n  Obs: kord som fristaende verktyg saknas Smart Clients egen sakerhetsstack,");
            Console.WriteLine("  sa SecurityAccess-strategierna kan svara annorlunda har an i Smart Client.");
        }

        /// <summary>
        /// Reports what the Configuration API actually hands this user.
        ///
        /// The point is to separate three cases that all look like "no time profiles" from the
        /// outside: the API is unreachable, the API answers but filters everything away because
        /// the role may not see configuration, or the API answers and the profiles genuinely are
        /// somewhere else. So other folders are read alongside the time profiles - if those are
        /// empty too, it is the user's rights, not the time profiles.
        /// </summary>
        private static void DiagnoseConfiguration()
        {
            Console.WriteLine("\n  --- Configuration API ---");

            var master = VideoOS.Platform.EnvironmentManager.Instance.MasterSite;
            var current = VideoOS.Platform.EnvironmentManager.Instance.CurrentSite;
            var fromFqid = VideoOS.Platform.Configuration.Instance.ServerFQID?.ServerId;
            var serverId = master?.ServerId ?? fromFqid;

            Console.WriteLine($"  Servernamn        : {VideoOS.Platform.Configuration.Instance.ServerName}");
            Console.WriteLine($"  ServerId (master) : {Describe(master?.ServerId)}");
            Console.WriteLine($"  ServerId (current): {Describe(current?.ServerId)}");
            Console.WriteLine($"  ServerId (FQID)   : {Describe(fromFqid)}");

            if (serverId == null)
            {
                Console.WriteLine("  Ingen server att fraga - avbryter.");
                return;
            }

            VideoOS.Platform.ConfigurationItems.ManagementServer ms;
            try
            {
                ms = new VideoOS.Platform.ConfigurationItems.ManagementServer(serverId);
                Console.WriteLine($"  ManagementServer  : {ms.ComputerName}.{ms.DomainName} (version {ms.Version})");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ManagementServer gick inte att oppna: " + Explain(ex));
                return;
            }

            // Read a few unrelated folders as a control group.
            CountFolder("Inspelningsservrar", () => ms.RecordingServerFolder?.RecordingServers?.Count);
            CountFolder("Roller", () => ms.RoleFolder?.Roles?.Count);
            CountFolder("Anvandardefinierade handelser", () => ms.UserDefinedEventFolder?.UserDefinedEvents?.Count);
            CountFolder("Kameragrupper", () => ms.CameraGroupFolder?.CameraGroups?.Count);

            try
            {
                var folder = ms.TimeProfileFolder;
                if (folder == null)
                {
                    Console.WriteLine("  TimeProfileFolder : NULL - servern erbjuder ingen tidsprofilmapp.");
                    return;
                }

                Console.WriteLine($"  TimeProfileFolder : Path={folder.Path} DisplayName={folder.DisplayName}");
                folder.ClearChildrenCache();

                var profiles = folder.TimeProfiles;
                Console.WriteLine($"  Tidsprofiler      : {profiles?.Count ?? 0} st");
                foreach (var tp in profiles ?? new List<VideoOS.Platform.ConfigurationItems.TimeProfile>())
                    Console.WriteLine($"      '{tp.Name}' [{tp.TimeProfileType}] {tp.Guid}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Tidsprofiler gick inte att lasa: " + Explain(ex));
            }
        }

        private static void CountFolder(string label, Func<int?> read)
        {
            try
            {
                Console.WriteLine($"  {label,-30}: {read()?.ToString() ?? "null"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-30}: FEL {Explain(ex)}");
            }
        }

        private static string Describe(VideoOS.Platform.ServerId id) =>
            id == null ? "(null)" : $"{id.ServerHostname}:{id.Serverport} typ={id.ServerType} id={id.Id}";

        /// <summary>The whole exception chain. Configuration API faults hide the useful part inside.</summary>
        private static string Explain(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                parts.Add($"{e.GetType().Name}: {e.Message}");
            return string.Join(" <- ", parts);
        }

        /// <summary>Sentinel telling "the platform answered, and our namespace was not there" apart
        /// from "the platform could not be asked".</summary>
        private static readonly object NoSuchNamespace = new object();

        /// <summary>
        /// Describes the plugin's security namespace on the server, or null when the running
        /// platform has no way to enumerate namespaces (MIP SDK before 26.1).
        /// </summary>
        private static object TryFindNamespace(object client)
        {
            var type = client.GetType();
            var load = type.GetMethod("LoadAllSecurityNamespaces", Type.EmptyTypes);
            var all = type.GetProperty("AllSecurityNamespaces");
            if (load == null || all == null) return null;

            load.Invoke(client, null);
            if (!(all.GetValue(client) is System.Collections.IEnumerable namespaces)) return null;

            foreach (var entry in namespaces)
            {
                var id = entry?.GetType().GetProperty("id")?.GetValue(entry) as string;
                if (!Guid.TryParse(id, out var parsed) || parsed != PluginIds.PluginDefinition) continue;

                var lines = new List<string>();
                foreach (var property in entry.GetType().GetProperties())
                {
                    if (property.GetValue(entry) is string text && !string.IsNullOrEmpty(text))
                        lines.Add($"{property.Name} = {text}");
                }

                if (entry.GetType().GetProperty("securityActions")?.GetValue(entry) is System.Collections.IEnumerable actions)
                {
                    foreach (var action in actions)
                    {
                        var text = string.Join("/", action.GetType().GetProperties()
                            .Where(p => p.PropertyType == typeof(string))
                            .Select(p => p.GetValue(action) as string)
                            .Where(s => !string.IsNullOrEmpty(s)));
                        lines.Add("action: " + text);
                    }
                }

                return lines;
            }

            return NoSuchNamespace;
        }

        private static void RunReadChecks(TimeProfileRepository repository)
        {
            Section("Läsning");

            var profiles = repository.LoadProfiles();
            Console.WriteLine($"  {profiles.Count} tidsprofil(er) hittades.");
            Check("Listan är sorterad på namn",
                profiles.Select(p => p.Name).SequenceEqual(
                    profiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)));

            foreach (var profile in profiles)
            {
                var schedule = repository.LoadSchedule(profile.Id);
                if (schedule == null)
                {
                    Check($"Schema kunde läsas för '{profile.Name}'", false);
                    continue;
                }

                var weekly = schedule.Entries.Count(e => e.Kind == ScheduleEntryKind.Weekly);
                var other = schedule.Entries.Count - weekly;
                Console.WriteLine($"  '{profile.Name}' [{profile.ProfileType}]: {weekly} veckotid(er), {other} övrig(a)");

                foreach (var entry in schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Weekly))
                {
                    Console.WriteLine($"      {entry.Describe()}");
                    Check($"    '{entry.Describe()}' har minst en dag", entry.Days != DayFlags.None);
                    Check($"    '{entry.Describe()}' har positiv längd", entry.Duration > TimeSpan.Zero);
                }

                if (profile.IsSunclock)
                    Check($"Sunclock-profilen '{profile.Name}' har inga veckotider", weekly == 0);
            }
        }

        /// <summary>
        /// Checks whether a server-side component could establish *who* a client is from the token
        /// that client holds.
        ///
        /// This is the load-bearing question for the Event Server component. MIP's message channel
        /// carries no identity, so a caller has to present a credential - and the whole point is
        /// lost unless the server verifies it with the issuer rather than believing the claim. The
        /// standard OpenID Connect userinfo endpoint answers exactly that: valid token in, subject
        /// out, 401 otherwise.
        ///
        /// The token is never printed. It is a bearer credential; anyone holding it is the user.
        /// </summary>
        private static void ProbeTokenValidation(string server)
        {
            Section("Tokenvalidering");

            var serverId = VideoOS.Platform.EnvironmentManager.Instance.MasterSite?.ServerId
                           ?? VideoOS.Platform.Configuration.Instance.ServerFQID?.ServerId;
            var settings = serverId == null
                ? null
                : VideoOS.Platform.Login.LoginSettingsCache.GetLoginSettings(serverId);

            var token = settings?.IdentityTokenCache?.Token;
            if (string.IsNullOrEmpty(token))
            {
                Check("Klienten har en JWT att presentera", false, "IdentityTokenCache saknar token");
                return;
            }

            Check("Klienten har en JWT att presentera", true, $"{token.Length} tecken");

            var idp = new Uri(new Uri(server), "/idp/");
            string userinfo;
            try
            {
                var discovery = Fetch(new Uri(idp, ".well-known/openid-configuration"), null);
                userinfo = Between(discovery, "\"userinfo_endpoint\":\"", "\"");
                Check("IDP:n publicerar userinfo-endpunkt", !string.IsNullOrEmpty(userinfo), userinfo);
            }
            catch (Exception ex)
            {
                Check("IDP:n publicerar userinfo-endpunkt", false, ex.GetBaseException().Message);
                return;
            }

            // What the token says about itself. Unsigned inspection only - it proves nothing on its
            // own, but it shows which issuer and audience a verifier would have to accept.
            Console.WriteLine("\n  Innehåll (overifierat):");
            foreach (var line in DecodePayload(token))
                Console.WriteLine("    " + line);

            try
            {
                var discovery = Fetch(new Uri(idp, ".well-known/openid-configuration"), null);
                var jwks = Between(discovery, "\"jwks_uri\":\"", "\"")?.Replace("\\u0026", "&");
                var keys = Fetch(new Uri(jwks), null);
                var count = keys.Split(new[] { "\"kty\"" }, StringSplitOptions.None).Length - 1;
                Check("IDP:n publicerar signeringsnycklar", count > 0, count + " nyckel/nycklar");
            }
            catch (Exception ex)
            {
                Check("IDP:n publicerar signeringsnycklar", false, ex.GetBaseException().Message);
            }

            // A forged token must be refused wherever validation ends up happening, so each
            // candidate endpoint is tried with both.
            var forged = token.Substring(0, token.Length - 1) + (token.EndsWith("A") ? "B" : "A");
            Console.WriteLine("\n  Kandidatendpunkter:");
            foreach (var candidate in new[]
                     {
                         userinfo,
                         new Uri(new Uri(server), "/api/rest/v1/loginInfo").ToString(),
                         new Uri(new Uri(server), "/api/rest/v1/timeProfiles").ToString(),
                         new Uri(new Uri(server), "/api/rest/v1/roles").ToString()
                     })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                Console.WriteLine($"    {candidate}");
                Console.WriteLine($"      äkta      : {Attempt(candidate, token)}");
                Console.WriteLine($"      förfalskad: {Attempt(candidate, forged)}");
            }

            Console.WriteLine($"\n===== {_passed} godkända, {_failed} underkända =====");
        }

        private static string Attempt(string uri, string token)
        {
            try
            {
                var body = Fetch(new Uri(uri), token);
                return "200 OK, " + body.Length + " tecken" +
                       (body.Length < 200 ? ": " + body.Replace("\n", " ") : "");
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
                return ex.GetBaseException().Message;
            }
        }

        /// <summary>Reads a JWT's claim set without verifying anything. Diagnostics only.</summary>
        private static IEnumerable<string> DecodePayload(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return new[] { "(inte en JWT)" };

            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));

                // Split on top-level commas so each claim lands on its own line; good enough for a
                // report, and it avoids dragging in a JSON parser for four lines of output.
                return json.Trim('{', '}').Split(new[] { "\",\"" }, StringSplitOptions.None)
                    .Select(part => part.Trim('"'))
                    .ToList();
            }
            catch (Exception ex)
            {
                return new[] { "kunde inte avkodas: " + ex.Message };
            }
        }

        private static string Fetch(Uri uri, string bearerToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Timeout = 15000;
            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers["Authorization"] = "Bearer " + bearerToken;

            using (var response = request.GetResponse())
            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            from += start.Length;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : text.Substring(from, to - from);
        }

        /// <summary>
        /// Telling a refusal apart from a fault decides whether the workspace is hidden or shown
        /// read-only, so the classification is pinned rather than left to a substring match that
        /// happens to work today. The message text below is verbatim from a live Expert server.
        /// </summary>
        private static void CheckPermissionClassification()
        {
            Section("Klassificering av serverfel");

            var refusal = new VideoOS.Platform.NotAuthorizedMIPException(
                "VMO61008: You do not have sufficient permissions to complete the operation.");

            Check("NotAuthorized läses som nekad", PluginSecurity.LooksLikePermissionProblem(refusal));

            Check("Nekad känns igen även när den är inbäddad",
                PluginSecurity.LooksLikePermissionProblem(
                    new InvalidOperationException("Anropet misslyckades", refusal)));

            // Same exception type, translated message: the type alone has to carry it.
            Check("Nekad känns igen utan engelsk text",
                PluginSecurity.LooksLikePermissionProblem(
                    new VideoOS.Platform.NotAuthorizedMIPException("VMO61008: Otillräckliga rättigheter.")));

            Check("Nätverksfel läses inte som nekad",
                !PluginSecurity.LooksLikePermissionProblem(
                    new VideoOS.Platform.CommunicationMIPException("The remote server did not respond.")));

            Check("Tidsgräns läses inte som nekad",
                !PluginSecurity.LooksLikePermissionProblem(
                    new TimeoutException("The operation has timed out.")));
        }

        /// <summary>
        /// Which direct-save outcomes get a second attempt through the Event Server component.
        ///
        /// This is the rule that broke on philip-pc: an operator on Professional+ reads an empty
        /// configuration, so the profile they had just opened could not be found when saving, and
        /// the save reported it as gone and stopped. The component had answered a list and a read
        /// five seconds earlier and was never asked. The two halves of the rule matter in opposite
        /// directions, so both are pinned here:
        ///
        ///   - too narrow, and a save that could have succeeded is refused;
        ///   - too wide, and a save that already wrote something is replayed, applying half of it
        ///     twice. There is no transaction to undo that.
        /// </summary>
        private static void CheckSaveRouting()
        {
            Section("Vidareroutning av sparning");

            Check("Nekad skrivning går vidare till serverkomponenten",
                RoutedTimeProfileRepository.ShouldRoute(SaveStatus.PermissionDenied));

            Check("Osynlig tidsprofil går vidare till serverkomponenten",
                RoutedTimeProfileRepository.ShouldRoute(SaveStatus.NotVisible));

            // Everything below this line wrote something, or failed for a reason a second attempt
            // would only repeat.
            Check("Halvt tillämpad sparning görs inte om",
                !RoutedTimeProfileRepository.ShouldRoute(SaveStatus.PartiallyApplied));
            Check("Misslyckad sparning görs inte om",
                !RoutedTimeProfileRepository.ShouldRoute(SaveStatus.Failed));
            Check("Konflikt görs inte om",
                !RoutedTimeProfileRepository.ShouldRoute(SaveStatus.Conflict));
            Check("Lyckad sparning görs inte om",
                !RoutedTimeProfileRepository.ShouldRoute(SaveStatus.Success));
            Check("Oförändrat schema görs inte om",
                !RoutedTimeProfileRepository.ShouldRoute(SaveStatus.NothingToDo));

            // The classification itself, against the live server. A profile id nobody has ever
            // issued cannot be found by any caller, which is the same answer an operator gets for a
            // profile that does exist - and that is the point: the client cannot tell the two apart
            // and must not pretend otherwise. Writes nothing; it returns at the lookup.
            var unknown = new TimeProfileRepository { PermissionCheck = () => PermissionState.Granted }
                .Save(Guid.NewGuid(), new List<ScheduleEntry>(), new List<ScheduleEntry>(), default(DateTime));

            Check("Okänd tidsprofil rapporteras som osynlig, inte som borttagen",
                unknown.Status == SaveStatus.NotVisible, unknown.Status + ": " + unknown.Message);
        }

        /// <summary>
        /// What the information panel states about the plugin.
        ///
        /// Every one of these facts fails silently. A version that reads 0.0.0.0, a developer that
        /// reverted to the assembly default, a row that renders blank - the panel still opens and
        /// still looks finished, and the first person to notice is whoever is quoting the wrong
        /// version in a support thread. Nothing here needs a server.
        /// </summary>
        private static void CheckPluginInfo()
        {
            Section("Om-panelen");

            Check("Versionen är känd", PluginInfo.Version != "0.0.0.0" &&
                                       !string.IsNullOrWhiteSpace(PluginInfo.Version), PluginInfo.Version);

            // The two spellings of the same number. They come from one <Version> in the csproj, and
            // this is what notices if that stops being true.
            Check("Fyrsiffrig version stämmer med den visade",
                PluginInfo.FileVersion.StartsWith(PluginInfo.Version + "."),
                PluginInfo.Version + " / " + PluginInfo.FileVersion);

            Check("Utvecklaren är den som byggt paketet",
                PluginInfo.Developer == PluginInfo.DeveloperName, PluginInfo.Developer);

            Check("Språket anges", PluginInfo.Language == "Svenska", PluginInfo.Language);

            var facts = PluginInfo.Facts;
            Check("Panelen har fem rader", facts.Count == 5, facts.Count + " rader");
            Check("Ingen rad är tom",
                facts.All(f => !string.IsNullOrWhiteSpace(f.Label) && !string.IsNullOrWhiteSpace(f.Value)),
                string.Join(", ", facts.Select(f => f.Label + "=" + f.Value)));

            // Licence deliberately has no value yet. The row must still say something rather than
            // render as an empty line that reads like a bug.
            var license = facts.FirstOrDefault(f => f.Label == "Licens");
            Check("Licensraden säger att den inte är angiven",
                license != null && license.Value == "Ej angiven", license?.Value);

            Check("Hjälptexten har rubrik och brödtext överallt",
                HelpText.All.Count > 0 &&
                HelpText.All.All(t => !string.IsNullOrWhiteSpace(t.Title) &&
                                      !string.IsNullOrWhiteSpace(t.Body)),
                HelpText.All.Count + " avsnitt");
        }

        /// <summary>
        /// The day mask and the HH:mm:ss strings are the two places where a silent off-by-one
        /// would corrupt a customer's schedule, so they get checked explicitly.
        /// </summary>
        private static void CheckTimeParsing()
        {
            Section("Dagmask och tidsformat");

            Check("Söndag = 1", (int)DayOfWeek.Sunday.ToFlag() == 1);
            Check("Måndag = 2", (int)DayOfWeek.Monday.ToFlag() == 2);
            Check("Lördag = 64", (int)DayOfWeek.Saturday.ToFlag() == 64);
            Check("Vardagar = 62", (int)DayFlags.Weekdays == 62);
            Check("Alla dagar = 127", (int)DayFlags.All == 127);

            Check("Vardagar beskrivs som 'Vardagar'", DayFlags.Weekdays.Describe() == "Vardagar");
            Check("Enstaka dag beskrivs i klartext", DayFlags.Monday.Describe() == "Måndag");
            Check("Två dagar får 'och'", DayFlags.Monday.Describe() != null &&
                                          (DayFlags.Monday | DayFlags.Friday).Describe() == "måndag och fredag");

            var entry = new ScheduleEntry
            {
                Days = DayFlags.Weekdays,
                Start = TimeSpan.FromHours(22),
                Duration = TimeSpan.FromHours(8)
            };
            Check("22:00 + 8h passerar midnatt", entry.CrossesMidnight);
            Check("22:00 + 8h slutar 06:00 nästa dag", entry.End == TimeSpan.FromHours(30));
        }

        /// <summary>
        /// The month calendar turns patterns into real dates, and a wrong date there is believed:
        /// an operator who sees a blank Saturday concludes the profile does not cover Saturdays.
        /// Nothing here talks to a server - it is arithmetic, and this is where it gets checked.
        /// </summary>
        private static void CheckCalendarCoverage()
        {
            Section("Kalenderns dagar");

            // Saturdays 10:00-14:00, but only up to and including 15 August.
            var saturdays = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.Weekly,
                Days = DayFlags.Saturday,
                Start = TimeSpan.FromHours(10),
                Duration = TimeSpan.FromHours(4),
                RangeStart = new DateTime(2026, 1, 1),
                RangeEnd = new DateTime(2026, 8, 15)
            };

            Check("Veckomönster gäller på sin veckodag",
                Coverage.AppliesOn(saturdays, new DateTime(2026, 8, 8)));
            Check("Veckomönster gäller inte på andra dagar",
                !Coverage.AppliesOn(saturdays, new DateTime(2026, 8, 9)));
            Check("Veckomönster gäller till och med sista dagen",
                Coverage.AppliesOn(saturdays, new DateTime(2026, 8, 15)));

            // The whole reason the calendar exists: this one looks perfectly healthy in the week
            // grid and has quietly stopped applying.
            Check("Veckomönster gäller inte efter giltighetstidens slut",
                !Coverage.AppliesOn(saturdays, new DateTime(2026, 8, 22)));
            Check("Veckomönster gäller inte före giltighetstidens början",
                !Coverage.AppliesOn(saturdays, new DateTime(2025, 12, 27)));

            // Wednesdays 22:00 for eight hours, valid for that Wednesday alone.
            var night = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.Weekly,
                Days = DayFlags.Wednesday,
                Start = TimeSpan.FromHours(22),
                Duration = TimeSpan.FromHours(8),
                RangeStart = new DateTime(2026, 8, 12),
                RangeEnd = new DateTime(2026, 8, 12)
            };

            var wednesday = Coverage.For(new DateTime(2026, 8, 12), new[] { night });
            Check("Nattpass börjar 22:00 på sin egen dag",
                wednesday.Spans.Count == 1 && wednesday.Spans[0].From == TimeSpan.FromHours(22) &&
                wednesday.Spans[0].To == TimeSpan.FromHours(24));

            // The morning after belongs to Wednesday's occurrence, so it lands even though the
            // pattern's last day was Wednesday. Getting this backwards would hide a night shift.
            var thursday = Coverage.For(new DateTime(2026, 8, 13), new[] { night });
            Check("Nattpass fortsätter till 06:00 dagen efter",
                thursday.Spans.Count == 1 && thursday.Spans[0].From == TimeSpan.Zero &&
                thursday.Spans[0].To == TimeSpan.FromHours(6));

            Check("Nattpasset når inte fredagen",
                !Coverage.For(new DateTime(2026, 8, 14), new[] { night }).IsCovered);

            var allDay = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                AllDayEvent = true,
                OccurrenceStart = new DateTime(2026, 8, 20),
                OccurrenceEnd = new DateTime(2026, 8, 20)
            };
            var heldag = Coverage.For(new DateTime(2026, 8, 20), new[] { allDay });
            Check("Heldag täcker hela dygnet", heldag.Total == TimeSpan.FromHours(24));
            Check("Heldag räknas som enstaka datum", heldag.HasDate && !heldag.HasWeekly);
            Check("Heldag spiller inte över till dagen efter",
                !Coverage.For(new DateTime(2026, 8, 21), new[] { allDay }).IsCovered);

            var overnight = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                OccurrenceStart = new DateTime(2026, 8, 20, 23, 0, 0),
                OccurrenceEnd = new DateTime(2026, 8, 21, 1, 0, 0)
            };
            Check("Enstaka datum klipps vid midnatt",
                Coverage.For(new DateTime(2026, 8, 20), new[] { overnight }).Total == TimeSpan.FromHours(1));
            Check("Enstaka datum fortsätter in på nästa dag",
                Coverage.For(new DateTime(2026, 8, 21), new[] { overnight }).Total == TimeSpan.FromHours(1));

            // Two intervals over the same hours are one covered hour, not two - the summary in the
            // calendar panel counts these.
            var office = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.Weekly,
                Days = DayFlags.Weekdays,
                Start = TimeSpan.FromHours(8),
                Duration = TimeSpan.FromHours(9),
                RangeStart = new DateTime(2026, 1, 1)
            };
            var lunch = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                OccurrenceStart = new DateTime(2026, 8, 20, 12, 0, 0),
                OccurrenceEnd = new DateTime(2026, 8, 20, 13, 0, 0)
            };
            var overlapping = Coverage.For(new DateTime(2026, 8, 20), new[] { office, lunch });
            Check("Överlappande tider räknas en gång", overlapping.Total == TimeSpan.FromHours(9));
            Check("Båda källorna syns var för sig",
                overlapping.HasWeekly && overlapping.HasDate && overlapping.Spans.Count == 2);

            // ISO weeks, where the turn of the year is the only hard part. 2026 begins on a
            // Thursday, so its week 1 starts on 29 December 2025 and the year has 53 weeks.
            Check("1 januari 2026 ligger i vecka 1",
                SwedishDates.WeekNumber(new DateTime(2026, 1, 1)) == 1);
            Check("29 december 2025 ligger också i vecka 1",
                SwedishDates.WeekNumber(new DateTime(2025, 12, 29)) == 1);
            Check("31 december 2026 ligger i vecka 53",
                SwedishDates.WeekNumber(new DateTime(2026, 12, 31)) == 53);
            Check("10 augusti 2026 ligger i vecka 33",
                SwedishDates.WeekNumber(new DateTime(2026, 8, 10)) == 33);
            Check("Måndag och söndag i samma vecka får samma nummer",
                SwedishDates.WeekNumber(new DateTime(2026, 8, 10)) ==
                SwedishDates.WeekNumber(new DateTime(2026, 8, 16)));

            // Which week the week grid shows comes from here, so a day landing in the wrong week
            // puts an enstaka datum in a column the operator is not looking at. Sunday is the trap:
            // DayOfWeek numbers it 0, a Swedish week ends with it.
            foreach (var day in new[] { 10, 11, 12, 13, 14, 15, 16 })
                Check($"{day} augusti 2026 hör till veckan som börjar 10 augusti",
                    SwedishDates.MondayOf(new DateTime(2026, 8, day)) == new DateTime(2026, 8, 10),
                    SwedishDates.MondayOf(new DateTime(2026, 8, day)).ToString("yyyy-MM-dd"));

            Check("17 augusti börjar en ny vecka",
                SwedishDates.MondayOf(new DateTime(2026, 8, 17)) == new DateTime(2026, 8, 17));

            // Same week as the ISO check above: week 1 of 2026 starts in December 2025.
            Check("1 januari 2026 hör till veckan som börjar 29 december 2025",
                SwedishDates.MondayOf(new DateTime(2026, 1, 1)) == new DateTime(2025, 12, 29));

            Check("Klockslaget följer inte med veckans start",
                SwedishDates.MondayOf(new DateTime(2026, 8, 15, 23, 45, 0)) == new DateTime(2026, 8, 10));
        }

        private static void RunWriteChecks(TimeProfileRepository repository)
        {
            Section("Skrivning");

            // The plugin's security namespace only exists once Management Client has loaded the
            // plugin. On a bare lab server it has not, so the client-side gate is replaced here.
            // The Management Server still applies this user's role to every call.
            var realPermission = PluginSecurity.CanEdit();
            Console.WriteLine($"  Klientsidig behörighetskontroll rapporterar: {realPermission}");
            repository.PermissionCheck = () => PermissionState.Granted;

            // Delete and recreate rather than reuse. A previous failed run can leave bookings behind,
            // and duplicates there would skew every count that follows - the tests must measure this
            // build, not the wreckage of the last one.
            var name = "TEST - Harness";
            DeleteProfile(name);
            Console.WriteLine($"  Skapar testprofilen '{name}'...");
            if (!CreateProfile(name)) { Check("Kunde skapa testprofil", false); return; }

            var profile = repository.LoadProfiles().FirstOrDefault(p => p.Name == name);
            if (profile == null) { Check("Testprofilen finns", false); return; }

            // Load once, edit a copy, save the pair. The baseline and the edited list must come
            // from the same read so their client-side keys line up - that pairing is what tells an
            // edited interval apart from a deleted one plus a new one.
            ProfileSchedule schedule = null;
            List<ScheduleEntry> baseline = new List<ScheduleEntry>();
            List<ScheduleEntry> desired = new List<ScheduleEntry>();

            void Reload()
            {
                schedule = repository.LoadSchedule(profile.Id);
                // Everything the plugin is willing to write - weekly patterns and one-off dates.
                baseline = schedule.Entries.Where(e => e.IsEditable).ToList();
                desired = baseline.Select(e => e.Clone()).ToList();
            }

            SaveOutcome Commit() => repository.Save(profile.Id, desired, baseline, schedule.LastModified);

            // --- Start from a known state
            Reload();
            desired.Clear();
            var wiped = Commit();
            Check("Kunde tömma testprofilen", wiped.IsSuccess, wiped.Message);

            Reload();
            Check("Profilen är tom", baseline.Count == 0, $"{baseline.Count} kvar");

            // --- Add
            desired.Add(new ScheduleEntry
            {
                Days = DayFlags.Weekdays, Start = TimeSpan.FromHours(8),
                Duration = TimeSpan.FromHours(9), Subject = "Kontorstid"
            });
            desired.Add(new ScheduleEntry
            {
                Days = DayFlags.Saturday, Start = TimeSpan.FromHours(10),
                Duration = TimeSpan.FromHours(4), Subject = "Lördag"
            });
            var addResult = Commit();
            Check("Två tider kunde läggas till", addResult.IsSuccess, addResult.Message);

            Reload();
            Check("Två veckotider finns", baseline.Count == 2, $"fick {baseline.Count}");

            var office = baseline.FirstOrDefault(e => e.Subject == "Kontorstid");
            Check("Kontorstid har rätt dagar", office?.Days == DayFlags.Weekdays, office?.Days.ToString());
            Check("Kontorstid börjar 08:00", office?.Start == TimeSpan.FromHours(8), office?.Start.ToString());
            Check("Kontorstid är 9 timmar", office?.Duration == TimeSpan.FromHours(9), office?.Duration.ToString());

            // --- An untouched schedule must not generate a single server call
            var noop = Commit();
            Check("Oförändrat schema ger 'inget att spara'", noop.Status == SaveStatus.NothingToDo, noop.Status.ToString());

            // --- Edit
            var target = desired.First(e => e.Subject == "Kontorstid");
            target.Start = TimeSpan.FromHours(6.5);
            target.Duration = TimeSpan.FromHours(10);
            target.Days = DayFlags.Monday | DayFlags.Wednesday | DayFlags.Friday;
            var editResult = Commit();
            Check("Ändring kunde sparas", editResult.IsSuccess, editResult.Message);

            Reload();
            office = baseline.FirstOrDefault(e => e.Subject == "Kontorstid");
            Check("Ändrad starttid sparades", office?.Start == TimeSpan.FromHours(6.5), office?.Start.ToString());
            Check("Ändrad längd sparades", office?.Duration == TimeSpan.FromHours(10), office?.Duration.ToString());
            Check("Ändrade dagar sparades",
                office?.Days == (DayFlags.Monday | DayFlags.Wednesday | DayFlags.Friday), office?.Days.ToString());
            Check("Antalet tider är oförändrat efter en ändring", baseline.Count == 2, $"fick {baseline.Count}");

            // --- Interval crossing midnight
            desired.Add(new ScheduleEntry
            {
                Days = DayFlags.Friday, Start = TimeSpan.FromHours(22),
                Duration = TimeSpan.FromHours(8), Subject = "Natt"
            });
            var nightResult = Commit();
            Check("Tid över midnatt kunde sparas", nightResult.IsSuccess, nightResult.Message);

            Reload();
            var night = baseline.FirstOrDefault(e => e.Subject == "Natt");
            Check("Nattpasset läses tillbaka som 22:00 + 8h",
                night != null && night.Start == TimeSpan.FromHours(22) && night.Duration == TimeSpan.FromHours(8),
                night == null ? "saknas" : $"{night.Start} + {night.Duration}");

            // --- Removal
            desired.RemoveAll(e => e.Subject == "Natt");
            var removeResult = Commit();
            Check("Borttagning kunde sparas", removeResult.IsSuccess, removeResult.Message);

            Reload();
            Check("Nattpasset är borta", baseline.All(e => e.Subject != "Natt"));

            // --- Add, edit and remove in one save. Each hits a different server call and they run
            //     in sequence against the same profile, so the combination is worth its own check
            //     rather than trusting that three passing halves make a whole.
            var beforeCombined = baseline.Count;
            desired.First(e => e.Subject == "Kontorstid").Start = TimeSpan.FromHours(9);
            desired.First(e => e.Subject == "Kontorstid").Duration = TimeSpan.FromHours(4);
            desired.RemoveAll(e => e.Subject == "Lördag");
            desired.Add(new ScheduleEntry
            {
                Days = DayFlags.Sunday, Start = TimeSpan.FromHours(13),
                Duration = TimeSpan.FromHours(2), Subject = "Söndagspass"
            });

            var combinedResult = Commit();
            Check("Lägga till, ändra och ta bort i samma sparning", combinedResult.IsSuccess, combinedResult.Message);
            Check("Alla tre ändringarna rapporteras", combinedResult.AppliedChanges.Count == 3,
                string.Join(" / ", combinedResult.AppliedChanges));

            Reload();
            Check("Antalet tider stämmer efter kombinerad sparning", baseline.Count == beforeCombined,
                $"{baseline.Count} mot förväntade {beforeCombined}");
            Check("Den ändrade tiden fick nya värden",
                baseline.Any(e => e.Subject == "Kontorstid" && e.Start == TimeSpan.FromHours(9) &&
                                  e.Duration == TimeSpan.FromHours(4)));
            Check("Den borttagna tiden är borta", baseline.All(e => e.Subject != "Lördag"));
            Check("Den nya tiden finns", baseline.Any(e => e.Subject == "Söndagspass" && e.Days == DayFlags.Sunday));

            // --- A whole-day interval. The server parses the duration string with day semantics,
            //     so "24:00:00" would come back as 24 DAYS. Asking for a full day must therefore
            //     produce something just under 24 hours, never something measured in days.
            desired.Add(new ScheduleEntry
            {
                Days = DayFlags.Thursday, Start = TimeSpan.Zero,
                Duration = TimeSpan.FromHours(24), Subject = "Heldygn"
            });
            var fullDayResult = Commit();
            Check("Dygnslångt intervall kunde sparas", fullDayResult.IsSuccess, fullDayResult.Message);

            Reload();
            var whole = baseline.FirstOrDefault(e => e.Subject == "Heldygn");
            Check("Dygnslångt intervall blir inte flera dygn",
                whole != null && whole.Duration < TimeSpan.FromHours(24),
                whole == null ? "saknas" : whole.Duration.ToString());
            Check("Dygnslångt intervall täcker nästan hela dygnet",
                whole != null && whole.Duration >= TimeSpan.FromHours(23), whole?.Duration.ToString());
            Check("Dygnslångt intervall är fortfarande redigerbart",
                whole != null && whole.Kind == ScheduleEntryKind.Weekly, whole?.Kind.ToString());

            // --- Two identical intervals: deleting one must leave exactly one behind, even though
            //     removals resolve their target by content.
            Reload();
            for (var i = 0; i < 2; i++)
            {
                desired.Add(new ScheduleEntry
                {
                    Days = DayFlags.Tuesday, Start = TimeSpan.FromHours(15),
                    Duration = TimeSpan.FromHours(1), Subject = "Tvilling"
                });
            }
            Check("Två identiska tider kunde sparas", Commit().IsSuccess);

            Reload();
            Check("Båda tvillingarna finns", baseline.Count(e => e.Subject == "Tvilling") == 2,
                baseline.Count(e => e.Subject == "Tvilling").ToString());

            desired.Remove(desired.First(e => e.Subject == "Tvilling"));
            Check("En av tvillingarna kunde tas bort", Commit().IsSuccess);

            Reload();
            Check("Exakt en tvilling är kvar", baseline.Count(e => e.Subject == "Tvilling") == 1,
                baseline.Count(e => e.Subject == "Tvilling").ToString());

            // --- Validity range on a weekly pattern
            Reload();
            var limited = new ScheduleEntry
            {
                Days = DayFlags.Monday, Start = TimeSpan.FromHours(12),
                Duration = TimeSpan.FromHours(1), Subject = "Sommartid",
                RangeStart = new DateTime(2026, 6, 1),
                RangeEnd = new DateTime(2026, 8, 31)
            };
            desired.Add(limited);
            Check("Veckotid med giltighetsperiod kunde sparas", Commit().IsSuccess);

            Reload();
            var stored = baseline.FirstOrDefault(e => e.Subject == "Sommartid");
            Check("Startdatum sparades", stored?.RangeStart == new DateTime(2026, 6, 1), stored?.RangeStart.ToString("yyyy-MM-dd"));
            Check("Slutdatum sparades", stored?.RangeEnd == new DateTime(2026, 8, 31), stored?.RangeEnd?.ToString("yyyy-MM-dd") ?? "null");
            Check("Perioden gor inte tiden oredigerbar", stored?.Kind == ScheduleEntryKind.Weekly, stored?.Kind.ToString());

            // A saved range must compare equal to itself, or the entry would look changed forever.
            Check("Oforandrad period ger 'inget att spara'", Commit().Status == SaveStatus.NothingToDo);

            Reload();
            desired.First(e => e.Subject == "Sommartid").RangeEnd = null;
            Check("Perioden kunde tas bort (tills vidare)", Commit().IsSuccess);
            Reload();
            Check("Slutdatum ar borta",
                baseline.First(e => e.Subject == "Sommartid").RangeEnd == null,
                baseline.First(e => e.Subject == "Sommartid").RangeEnd?.ToString());

            // --- One-off dates
            Reload();
            var beforeDates = baseline.Count(e => e.Kind == ScheduleEntryKind.SingleOccurrence);
            desired.Add(new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                OccurrenceStart = new DateTime(2026, 12, 24, 8, 0, 0),
                OccurrenceEnd = new DateTime(2026, 12, 24, 13, 0, 0),
                Subject = "Julafton"
            });
            desired.Add(new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                AllDayEvent = true,
                OccurrenceStart = new DateTime(2026, 12, 25),
                Subject = "Juldagen"
            });
            Check("Enstaka datum kunde sparas", Commit().IsSuccess);

            Reload();
            Check("Bada datumen finns",
                baseline.Count(e => e.Kind == ScheduleEntryKind.SingleOccurrence) == beforeDates + 2);

            var eve = baseline.FirstOrDefault(e => e.Subject == "Julafton");
            Check("Julafton har ratt starttid", eve?.OccurrenceStart == new DateTime(2026, 12, 24, 8, 0, 0), eve?.OccurrenceStart?.ToString());
            Check("Julafton har ratt sluttid", eve?.OccurrenceEnd == new DateTime(2026, 12, 24, 13, 0, 0), eve?.OccurrenceEnd?.ToString());

            var day = baseline.FirstOrDefault(e => e.Subject == "Juldagen");
            Check("Juldagen ar en heldag", day?.AllDayEvent == true, day?.AllDayEvent.ToString());

            // Round trip stability matters most here: the server normalises all-day bookings, so a
            // mismatch would make the entry look edited on every later save.
            var stability = Commit();
            Check("Enstaka datum ar stabila over en tom sparning",
                stability.Status == SaveStatus.NothingToDo, stability.Message);

            // --- Editing a one-off date
            Reload();
            var target2 = desired.First(e => e.Subject == "Julafton");
            target2.OccurrenceStart = new DateTime(2026, 12, 24, 10, 0, 0);
            target2.OccurrenceEnd = new DateTime(2026, 12, 24, 15, 30, 0);
            var editDate = Commit();
            Check("Enstaka datum kunde andras", editDate.IsSuccess, $"{editDate.Status}: {editDate.Message}");

            Reload();
            eve = baseline.FirstOrDefault(e => e.Subject == "Julafton");
            Check("Andrad starttid sparades", eve?.OccurrenceStart == new DateTime(2026, 12, 24, 10, 0, 0), eve?.OccurrenceStart?.ToString());
            Check("Andrad sluttid sparades", eve?.OccurrenceEnd == new DateTime(2026, 12, 24, 15, 30, 0), eve?.OccurrenceEnd?.ToString());
            Check("Antalet datum ar oforandrat efter en andring",
                baseline.Count(e => e.Kind == ScheduleEntryKind.SingleOccurrence) == beforeDates + 2,
                baseline.Count(e => e.Kind == ScheduleEntryKind.SingleOccurrence).ToString());

            // --- Removing a one-off date
            Reload();
            desired.RemoveAll(e => e.Subject == "Julafton");
            var removeDate = Commit();
            Check("Enstaka datum kunde tas bort", removeDate.IsSuccess, $"{removeDate.Status}: {removeDate.Message}");
            Reload();
            Check("Julafton ar borta", baseline.All(e => e.Subject != "Julafton"));
            Check("Juldagen ar kvar", baseline.Any(e => e.Subject == "Juldagen"));

            // --- Stale timestamp must be refused
            Reload();
            foreach (var e in desired) e.Start = TimeSpan.FromHours(5);
            var stale = repository.Save(profile.Id, desired, baseline, new DateTime(2000, 1, 1));
            Check("Föråldrad tidsstämpel ger konflikt", stale.Status == SaveStatus.Conflict, stale.Status.ToString());

            // --- The permission gate must actually block
            Reload();
            var expected = baseline.Count;
            desired.Clear();
            repository.PermissionCheck = () => PermissionState.Denied;
            var denied = Commit();
            Check("Nekad behörighet stoppar sparning", denied.Status == SaveStatus.PermissionDenied, denied.Status.ToString());
            repository.PermissionCheck = () => PermissionState.Granted;

            Reload();
            Check("Inget skrevs när behörighet saknades", baseline.Count == expected,
                $"{baseline.Count} mot {expected}");
        }

        private static void DeleteProfile(string name)
        {
            try
            {
                var serverId = VideoOS.Platform.Configuration.Instance.ServerFQID.ServerId;
                var folder = new VideoOS.Platform.ConfigurationItems.ManagementServer(serverId).TimeProfileFolder;
                folder.ClearChildrenCache();
                var existing = folder.TimeProfiles.FirstOrDefault(t => t.Name == name);
                if (existing == null) return;

                folder.RemoveTimeProfile(existing.Path);
                folder.ClearChildrenCache();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Kunde inte ta bort gammal testprofil: " + ex.GetBaseException().Message);
            }
        }

        private static bool CreateProfile(string name)
        {
            try
            {
                var serverId = VideoOS.Platform.Configuration.Instance.ServerFQID.ServerId;
                var folder = new VideoOS.Platform.ConfigurationItems.ManagementServer(serverId).TimeProfileFolder;
                var task = folder.AddTimeProfile(name, "Skapad av TimeProfileEditor.Harness", "Calendar");
                folder.ClearChildrenCache();
                return task.State == VideoOS.Platform.ConfigurationItems.StateEnum.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Kunde inte skapa profil: " + ex.GetBaseException().Message);
                return false;
            }
        }

        private static void Connect(string server)
        {
            VideoOS.Platform.SDK.Environment.Initialize();
            var uri = new Uri(server);

            // The newer overloads are built around explicit tokens and identity providers. This
            // tool deliberately signs in as the Windows user running it, so that the server applies
            // that account's role to the calls under test - which is what the plugin does too.
#pragma warning disable CS0618
            VideoOS.Platform.SDK.Environment.AddServer(uri, CredentialCache.DefaultNetworkCredentials);
            VideoOS.Platform.SDK.Environment.Login(uri, true);
#pragma warning restore CS0618
            Console.WriteLine($"Inloggad mot {server}\n");
        }

        private static string ArgValue(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("===== " + title + " =====");
        }

        private static void Check(string what, bool ok, string detail = null)
        {
            if (ok) _passed++; else _failed++;
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  OK   " : "  FEL  ");
            Console.ForegroundColor = previous;
            Console.WriteLine(what + (ok || string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
        }
    }
}

