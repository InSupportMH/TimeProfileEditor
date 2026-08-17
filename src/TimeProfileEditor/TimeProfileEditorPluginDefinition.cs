using System;
using System.Collections.Generic;
using System.Drawing;
using TimeProfileEditor.Model;
using TimeProfileEditor.Security;
using TimeProfileEditor.Services;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Client;

namespace TimeProfileEditor
{
    /// <summary>
    /// Entry point MIP looks for. Loaded by both the Smart Client (where the workspace lives)
    /// and the Management Client (which is what publishes <see cref="SecurityActions"/> as a
    /// role tab, so permissions can be granted centrally instead of per client).
    /// </summary>
    public class TimeProfileEditorPluginDefinition : PluginDefinition
    {
        private static Image _icon;
        private static VideoOS.Platform.UI.Controls.VideoOSIconSourceBase _iconSource;

        private readonly List<WorkSpacePlugin> _workSpacePlugins = new List<WorkSpacePlugin>();
        private readonly List<ViewItemPlugin> _viewItemPlugins = new List<ViewItemPlugin>();
        private List<SecurityAction> _securityActions = new List<SecurityAction>();

        internal static TimeProfileViewItemPlugin ViewItemPluginInstance { get; private set; }

        internal static Image PluginIcon => _icon ?? (_icon = IconFactory.CreateClockIcon(24));

        /// <summary>
        /// Shared by the workspace tab and the view item. Built once and lazily, because it is a
        /// WPF object and the Management Client loads this same definition without a WPF UI.
        /// </summary>
        internal static VideoOS.Platform.UI.Controls.VideoOSIconSourceBase IconSource
        {
            get
            {
                if (_iconSource != null) return _iconSource;
                try
                {
                    _iconSource = IconFactory.CreateIconSource();
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.Log(false, nameof(TimeProfileEditorPluginDefinition),
                        "Kunde inte skapa vektorikonen: " + ex.Message);
                }

                return _iconSource;
            }
        }

        public override Guid Id => PluginIds.PluginDefinition;

        // Configured, not Effective: this is read before there is a server to ask about a licence,
        // and a measurement build is pinned at compile time so the metadata already answers it.
        public override string Name => SystemEdition.Configured == EditionMode.Measurement
            ? "Tidsprofiler (MÄTLÄGE)"
            : "Tidsprofiler";

        // Both read from the assembly rather than written out here. Management Client shows these
        // in its plugin list, the information panel in Smart Client shows the same two facts, and
        // a hand-maintained copy is how the version on one screen ends up being 1.0.0.0 while the
        // installed file says 1.6.0 - which is exactly what it said before this line was changed.
        public override string Manufacturer => PluginInfo.Developer;

        public override string VersionString => PluginInfo.FileVersion;

        public override Image Icon => PluginIcon;

        /// <summary>Names the tab that appears under Roles in Management Client.</summary>
        public override string RolesTabName => "Tidsprofiler";

        /// <summary>
        /// Published to the Management Server so an administrator can grant them per role.
        /// The ids are part of the plugin's contract - renaming one revokes it everywhere.
        /// </summary>
        public override List<SecurityAction> SecurityActions
        {
            get => _securityActions;
            set => _securityActions = value;
        }

        public override List<WorkSpacePlugin> WorkSpacePlugins => _workSpacePlugins;

        public override List<ViewItemPlugin> ViewItemPlugins => _viewItemPlugins;

        public override void Init()
        {
            // The list-based permission checks need an instance to ask the platform about.
            PluginSecurity.Attach(this);

            _securityActions = new List<SecurityAction>
            {
                new SecurityAction(SecurityActionIds.View, "Visa tidsprofiler"),
                new SecurityAction(SecurityActionIds.Edit, "Redigera tidsprofiler")
            };

            // Only the Smart Client has workspaces; in the Management Client this simply stays
            // empty and the plugin contributes nothing but its role tab.
            if (EnvironmentManager.Instance.EnvironmentType != EnvironmentType.SmartClient)
                return;

            ViewItemPluginInstance = new TimeProfileViewItemPlugin();
            _viewItemPlugins.Add(ViewItemPluginInstance);

            // Asking the server up front means a user who was explicitly refused never sees the
            // tab at all, rather than finding a workspace that turns out to be empty. Only an
            // explicit refusal hides it: if the check itself falls over, the workspace is offered
            // and explains itself, because a tab that silently does not exist gives whoever has to
            // fix it nothing to go on.
            bool allowed;
            try
            {
                allowed = PluginSecurity.CanView();
            }
            catch (Exception ex)
            {
                allowed = true;
                ChangeLog.Error("Behörighetskontrollen kunde inte köras - arbetsytan visas ändå, " +
                                "men redigering förblir stängd", ex);
            }

            if (allowed)
            {
                _workSpacePlugins.Add(new TimeProfileWorkSpacePlugin());
            }
            else
            {
                // Hiding the workspace is the right answer to a refusal, but it also takes away the
                // only place the user could have pressed "Kopiera diagnostik". So the report is
                // written to the MIP log instead - this is precisely the moment someone will need
                // to know why, and there is nothing left in the UI to ask.
                ChangeLog.Info("Arbetsytan Tidsprofiler visas inte - servern nekade behörigheten " +
                               $"'Visa tidsprofiler' ({PluginSecurity.LastStrategy})." +
                               Environment.NewLine + Diagnostics.Report(includeProbes: false));
            }
        }

        public override void Close()
        {
            _workSpacePlugins.Clear();
            _viewItemPlugins.Clear();
            ViewItemPluginInstance = null;
            PluginSecurity.Reset();
            _icon?.Dispose();
            _icon = null;
            _iconSource = null;
        }
    }
}
