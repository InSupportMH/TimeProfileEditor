using System;
using TimeProfileEditor.Views;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;

namespace TimeProfileEditor
{
    /// <summary>
    /// Carries the editor UI. It exists only to fill the plugin's own workspace, so it is hidden
    /// from setup mode - dropping a configuration editor into an arbitrary camera view would be
    /// confusing and gives no benefit.
    /// </summary>
    internal class TimeProfileViewItemPlugin : ViewItemPlugin
    {
        public override Guid Id => PluginIds.ViewItem;

        public override string Name => "Tidsprofiler";

        public override VideoOSIconSourceBase IconSource
        {
            get => TimeProfileEditorPluginDefinition.IconSource;
            protected set { /* the plugin supplies its own icon */ }
        }

        public override bool HideSetupItem => true;

        public override void Init()
        {
        }

        public override void Close()
        {
        }

        public override ViewItemManager GenerateViewItemManager() => new TimeProfileViewItemManager();
    }

    internal class TimeProfileViewItemManager : ViewItemManager
    {
        public TimeProfileViewItemManager() : base("TimeProfileViewItemManager")
        {
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl() =>
            new TimeProfileEditorView();
    }
}
