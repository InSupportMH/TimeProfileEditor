using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;
using VideoOS.Platform.Login;
using VideoOS.Platform.Util;

namespace TimeProfileEditor.Security
{
    /// <summary>
    /// What this build is: the product, or an instrument.
    ///
    /// There used to be a Corporate and a Standard mode here, on the belief that a role can only
    /// be granted this plugin's permissions on Corporate. That was wrong, and it was wrong in a
    /// way worth recording, because it cost the project a whole architecture. Two different things
    /// were being conflated:
    ///
    ///   * Granting a role a subset of the *administrator* rights - GENERIC_WRITE on the Management
    ///     Server namespace, which is what a direct configuration write needs. That really is
    ///     Corporate only, and the licence advertises it as DifferentiatedAdministratorSecurity.
    ///
    ///   * A MIP plugin's own security namespace - the tick boxes an administrator sets under
    ///     Roles -> Tidsprofiler. That is a MIP feature and it exists on Express+, Professional+,
    ///     Expert and Corporate alike.
    ///
    /// Only the first is missing on Expert and Professional+. So the permission model is the same
    /// everywhere and needs no edition at all: the plugin asks what this user has been granted, and
    /// where the direct write is refused the Event Server component performs it. One binary, correct
    /// on every product tier, with nothing to configure and no way to install the wrong one.
    /// </summary>
    internal enum EditionMode
    {
        /// <summary>The product. Permissions come from the plugin's own security namespace.</summary>
        Normal,

        /// <summary>
        /// No permission check inside the plugin at all: every action is granted, to whoever is
        /// signed in.
        ///
        /// This exists to measure the one thing no other build can measure - what the *Management
        /// Server* does when a non-administrator saves a time profile. With the plugin's own gate
        /// in the way every attempt stops in this code and the server is never asked, so the answer
        /// that decides whether the Event Server component is needed at all stays unknown.
        ///
        /// It opens nothing up. The Management Server still judges every write, and this only stops
        /// the plugin pre-empting that judgement; what it does remove is the banner explaining a
        /// refusal, so an operator gets a raw server error instead of a sentence they can act on.
        /// Reachable only by pinning -p:Edition=Measurement at build time, never by default, and
        /// announced in the workspace name, the banner and the log wherever it is on - a build that
        /// does not check permissions must never be mistakable for one that does.
        /// </summary>
        Measurement
    }

    /// <summary>Whether the signed-in user holds the built-in Administrators role.</summary>
    internal enum AdminState
    {
        Yes,
        No,

        /// <summary>
        /// Nothing on the client could answer. Deliberately distinct from <see cref="No"/>:
        /// treating the two alike is what made the workspace disappear on systems where the
        /// role list simply is not published to the client.
        /// </summary>
        Unknown
    }

    /// <summary>What the Management Server lets this user do with the configuration.</summary>
    internal enum ConfigAccess
    {
        Allowed,
        Denied,

        /// <summary>The server could not be asked - a network or service problem, not a refusal.</summary>
        Unknown
    }

    /// <summary>
    /// What the connected system is, for reporting rather than for deciding.
    ///
    /// Nothing here gates a permission any more. The product tier settles one thing only - whether
    /// a role can be granted configuration rights, and so whether an ordinary operator's write can
    /// succeed directly or has to be performed by the Event Server component. That is discovered by
    /// attempting the write and reading the refusal, not by consulting a licence, because the
    /// refusal is the authority and the licence flag is a guess about it.
    ///
    /// The tier is still worth knowing when someone is reading a diagnostics report and wondering
    /// why the write took the long way round.
    /// </summary>
    internal static class SystemEdition
    {
        /// <summary>
        /// The licence feature that gates granting a role a subset of the administrator rights.
        /// Present on Corporate. It says nothing about this plugin's own security namespace, which
        /// exists on every tier - see <see cref="EditionMode"/> for why that distinction matters.
        /// </summary>
        public const string DifferentiatedAdminFeature = "DifferentiatedAdministratorSecurity";

        private static readonly object Sync = new object();

        private static EditionMode? _configured;
        private static bool? _delegatesRights;
        private static string _reason;
        private static ConfigAccess? _configAccess;

        /// <summary>How the administrator question was answered. Surfaced in diagnostics.</summary>
        public static string AdminSource { get; private set; }

        /// <summary>Why the configuration probe failed, when it did.</summary>
        public static string ConfigAccessError { get; private set; }

        /// <summary>
        /// What this build is. Baked in at compile time, and anything unrecognised reads as the
        /// product - a typo in a build argument must not silently produce a plugin that checks
        /// nothing.
        /// </summary>
        public static EditionMode Configured
        {
            get
            {
                if (_configured.HasValue) return _configured.Value;

                _configured = string.Equals(Metadata("Edition"), nameof(EditionMode.Measurement),
                    StringComparison.OrdinalIgnoreCase)
                    ? EditionMode.Measurement
                    : EditionMode.Normal;
                return _configured.Value;
            }
        }

        /// <summary>
        /// The MIP SDK this build was compiled against. A plug-in loads in that XProtect version
        /// and every later one but never an earlier one, so this is the first thing worth knowing
        /// when a client does not show the workspace at all.
        /// </summary>
        public static string CompiledAgainstMipSdk => Metadata("MipSdk") ?? "okänd";

        /// <summary>The MIP platform actually loaded by the host process.</summary>
        public static string RunningMipPlatform
        {
            get
            {
                try
                {
                    var location = typeof(EnvironmentManager).Assembly.Location;
                    return string.IsNullOrEmpty(location)
                        ? "okänd"
                        : FileVersionInfo.GetVersionInfo(location).FileVersion;
                }
                catch (Exception ex)
                {
                    return "okänd (" + ex.GetBaseException().Message + ")";
                }
            }
        }

        private static string Metadata(string key) =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value;

        /// <summary>How the product tier was determined. Shown in diagnostics.</summary>
        public static string Reason
        {
            get
            {
                if (!_delegatesRights.HasValue) DelegatesConfigurationRights();
                return _reason;
            }
        }

        public static void Reset()
        {
            lock (Sync)
            {
                _delegatesRights = null;
                _reason = null;
                _configAccess = null;
                AdminSource = null;
                ConfigAccessError = null;
            }
        }

        /// <summary>
        /// Whether this system can grant a role configuration rights, so that an ordinary
        /// operator's write can succeed directly.
        ///
        /// Reporting only. Nothing decides anything on this answer: the write is attempted and the
        /// server's refusal is what routes it through the Event Server component instead. A licence
        /// flag is a prediction about that refusal, and where the two disagree the refusal is right.
        /// </summary>
        public static bool DelegatesConfigurationRights()
        {
            if (_delegatesRights.HasValue) return _delegatesRights.Value;

            try
            {
                var license = EnvironmentManager.Instance.SystemLicense;
                if (license == null)
                {
                    _reason = "Licensinformationen var inte tillgänglig.";
                    _delegatesRights = false;
                }
                else if (license.IsFeatureEnabled(DifferentiatedAdminFeature))
                {
                    _reason = $"Licensen har {DifferentiatedAdminFeature} - roller kan ges " +
                              "konfigurationsrättigheter.";
                    _delegatesRights = true;
                }
                else
                {
                    _reason = $"Licensen saknar {DifferentiatedAdminFeature} - bara administratörer " +
                              "kan skriva konfiguration direkt.";
                    _delegatesRights = false;
                }
            }
            catch (Exception ex)
            {
                _reason = "Kunde inte läsa licensinformationen (" + ex.GetBaseException().Message + ").";
                _delegatesRights = false;
            }

            EnvironmentManager.Instance.Log(false, nameof(SystemEdition), $"Produktniva: {_reason}");

            return _delegatesRights.Value;
        }

        /// <summary>
        /// Whether the signed-in user holds the built-in Administrators role.
        ///
        /// Two sources are consulted, both server-issued: the role list that came back with the
        /// login, and the platform's own membership call. Neither is guaranteed to be answerable
        /// on every product, so an inconclusive result says so rather than pretending it is a
        /// refusal - <see cref="ConfigurationAccess"/> settles those cases.
        /// </summary>
        public static AdminState AdministratorState()
        {
            var roleId = SecurityAccess.AdministratorRoleId;
            var listed = FromGroupMembership(roleId);
            if (listed == AdminState.Yes)
            {
                AdminSource = "rollistan från inloggningen";
                return AdminState.Yes;
            }

            var asked = FromIsMember(roleId);
            if (asked == AdminState.Yes)
            {
                AdminSource = "SecurityAccess.IsMember";
                return AdminState.Yes;
            }

            // A "no" is only believed when the role list actually arrived. IsMember answering false
            // is not proof on its own - an unsupported call and a non-member look identical.
            if (listed == AdminState.No)
            {
                AdminSource = "rollistan från inloggningen";
                return AdminState.No;
            }

            AdminSource = "kunde inte avgöras på klienten";
            return AdminState.Unknown;
        }

        public static bool IsAdministrator() => AdministratorState() == AdminState.Yes;

        private static AdminState FromGroupMembership(Guid roleId)
        {
            try
            {
                var roles = CurrentLoginSettings()?.GroupMemberShip;
                if (roles == null || roles.Length == 0) return AdminState.Unknown;

                return roles.Any(r => Guid.TryParse(r, out var id) && id == roleId)
                    ? AdminState.Yes
                    : AdminState.No;
            }
            catch (Exception ex)
            {
                Log("Kunde inte lasa rollistan: " + ex.GetBaseException().Message);
                return AdminState.Unknown;
            }
        }

        private static AdminState FromIsMember(Guid roleId)
        {
            try
            {
                var serverId = CurrentServerId();
                var identity = CurrentLoginSettings()?.UserIdentity;
                if (serverId == null || string.IsNullOrEmpty(identity)) return AdminState.Unknown;

                // The role id has been seen written both bare and braced depending on the caller,
                // so both spellings are offered rather than betting on one.
                foreach (var spelling in new[] { roleId.ToString(), roleId.ToString("B") })
                {
                    if (SecurityAccess.IsMember(serverId, identity, spelling)) return AdminState.Yes;
                }

                return AdminState.No;
            }
            catch (Exception ex)
            {
                Log("IsMember misslyckades: " + ex.GetBaseException().Message);
                return AdminState.Unknown;
            }
        }

        /// <summary>
        /// Whether the Management Server grants this user configuration access at all.
        ///
        /// Diagnostics only now. It used to stand in for the permission check, on the belief that
        /// configuration access and permission to use this plugin were the same question on Expert
        /// and Professional+; they are not, and answering one with the other refused every operator
        /// the plugin exists for. What it is still good for is explaining a routed write: a user
        /// refused here is a user whose save will have to go through the Event Server component.
        ///
        /// The role list is what is asked for, not the time profiles. Measured against a live
        /// server: an operator reading the roles gets a clean NotAuthorizedMIPException, while the
        /// same operator reading the time profiles gets a successful, empty answer. XProtect hands
        /// back the items the caller may see rather than refusing, so "no time profiles" and "not
        /// allowed to see any" are indistinguishable - and only the first of the two questions has
        /// an answer worth acting on.
        ///
        /// Cached, because it costs a round trip and is consulted while the client is starting up.
        /// </summary>
        public static ConfigAccess ConfigurationAccess()
        {
            lock (Sync)
            {
                if (_configAccess.HasValue) return _configAccess.Value;

                var serverId = CurrentServerId();
                if (serverId == null)
                {
                    ConfigAccessError = "Ingen server att fråga.";
                    _configAccess = ConfigAccess.Unknown;
                    return _configAccess.Value;
                }

                try
                {
                    // Touching the collection is what forces the call.
                    var roles = new ManagementServer(serverId).RoleFolder?.Roles?.Count ?? 0;

                    ConfigAccessError = null;
                    _configAccess = ConfigAccess.Allowed;
                    Log($"Konfigurationsatkomst: Allowed ({roles} roller lastes).");
                }
                catch (Exception ex)
                {
                    ConfigAccessError = ex.GetBaseException().Message;
                    _configAccess = PluginSecurity.LooksLikePermissionProblem(ex)
                        ? ConfigAccess.Denied
                        : ConfigAccess.Unknown;

                    Log($"Konfigurationsatkomst: {_configAccess}. {ConfigAccessError}");
                }

                return _configAccess.Value;
            }
        }

        /// <summary>
        /// The product tier, in the words a support thread needs. Not a permission model - see
        /// <see cref="DelegatesConfigurationRights"/>.
        /// </summary>
        public static string ProductDescription() =>
            DelegatesConfigurationRights()
                ? "XProtect Corporate (roller kan ges konfigurationsrätt)"
                : "XProtect Expert / Professional+ (skrivning går via Event Server)";

        private static ServerId CurrentServerId() =>
            EnvironmentManager.Instance.MasterSite?.ServerId ?? Configuration.Instance.ServerFQID?.ServerId;

        private static LoginSettings CurrentLoginSettings()
        {
            var serverId = CurrentServerId();
            return serverId == null ? null : LoginSettingsCache.GetLoginSettings(serverId);
        }

        private static void Log(string message) =>
            EnvironmentManager.Instance.Log(false, nameof(SystemEdition), message);
    }
}
