using System;

namespace TimeProfileEditor
{
    /// <summary>
    /// Fixed identities for the plugin. These must never change between releases:
    /// the plugin id doubles as the id of the security namespace that holds the
    /// role permissions, so changing it would silently drop every granted permission.
    /// </summary>
    internal static class PluginIds
    {
        public static readonly Guid PluginDefinition = new Guid("3f9c1d64-5b47-4a2e-9d18-0c7a6e51b3f2");
        public static readonly Guid WorkSpace = new Guid("8a2e77b1-4c93-4f06-b5d7-1e6f2c904a58");
        public static readonly Guid ViewItem = new Guid("d51b8a03-62f4-4e79-88c1-9a3d7b45e206");
    }

    /// <summary>
    /// Ids of the role permissions this plugin publishes. They show up in Management
    /// Client under Roles -> Tidsprofiler once the plugin has been loaded there once.
    /// </summary>
    internal static class SecurityActionIds
    {
        public const string View = "TimeProfileEditor.View";
        public const string Edit = "TimeProfileEditor.Edit";
    }
}
