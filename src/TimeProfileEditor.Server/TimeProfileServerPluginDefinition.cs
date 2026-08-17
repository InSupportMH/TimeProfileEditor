using System;
using System.Collections.Generic;
using System.Drawing;
using TimeProfileEditor.Server.Background;
using VideoOS.Platform;
using VideoOS.Platform.Background;

namespace TimeProfileEditor.Server
{
    /// <summary>
    /// Fixed identities for the server component. Separate from the client plugin's ids on
    /// purpose: MIP keeps one plugin per definition id, and giving both the same id would make
    /// whichever loaded first shadow the other wherever they meet.
    /// </summary>
    internal static class ServerIds
    {
        public static readonly Guid PluginDefinition = new Guid("6b1f4d92-8e37-4c05-a7d6-2f83c19b4e70");
        public static readonly Guid BackgroundPlugin = new Guid("e40a7c58-1d96-4b23-8f71-5c62a0d38e14");
    }

    /// <summary>
    /// The Event Server half of the plugin.
    ///
    /// It exists because only XProtect Corporate can grant a role configuration rights. On Expert
    /// and Professional+ the Management Server refuses an operator's write outright, whatever the
    /// plugin's own permissions say, so no amount of client code can let them save a time profile
    /// as themselves. The Event Server service account is a member of the Administrators role -
    /// that is a Milestone install requirement, not something this plugin arranges - so a component
    /// running inside it can perform the write the operator cannot, once it has established who is
    /// asking and that they have been granted it.
    ///
    /// Publishes no security actions and no UI. The permissions belong to the client plugin's
    /// namespace and <see cref="Security.PermissionOracle"/> checks those; a second set of tick
    /// boxes under Roles would be a second answer to the same question, and the two would
    /// eventually disagree.
    /// </summary>
    public class TimeProfileServerPluginDefinition : PluginDefinition
    {
        private readonly List<BackgroundPlugin> _backgroundPlugins = new List<BackgroundPlugin>();

        public override Guid Id => ServerIds.PluginDefinition;

        public override string Name => "Tidsprofiler (serverkomponent)";

        public override string Manufacturer => "Milestone MIP-plugin";

        public override string VersionString =>
            typeof(TimeProfileServerPluginDefinition).Assembly.GetName().Version.ToString();

        public override Image Icon => null;

        public override List<BackgroundPlugin> BackgroundPlugins => _backgroundPlugins;

        public override void Init()
        {
            // plugin.def already restricts loading to the Service environment. This is the second
            // check rather than the only one: a hand-edited or mis-packaged manifest must not be
            // able to start a component that acts with administrator rights inside a client.
            if (EnvironmentManager.Instance.EnvironmentType != EnvironmentType.Service)
            {
                ServerLog.Info($"Startar inte - värdmiljön är {EnvironmentManager.Instance.EnvironmentType}, " +
                               "komponenten körs bara i Event Server.");
                return;
            }

            _backgroundPlugins.Add(new TimeProfileServerPlugin());
        }

        public override void Close()
        {
            _backgroundPlugins.Clear();
        }
    }
}
