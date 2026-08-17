using System;
using System.Drawing;
using TimeProfileEditor.Services;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;

namespace TimeProfileEditor
{
    /// <summary>
    /// The "Tidsprofiler" tab in Smart Client. A workspace is a layout of view items rather than
    /// a control, so it hosts one full-size view item that carries the whole editor.
    /// </summary>
    internal class TimeProfileWorkSpacePlugin : WorkSpacePlugin
    {
        private ViewAndLayoutItem _viewAndLayoutItem;

        public override Guid Id => PluginIds.WorkSpace;

        public override string Name => "Tidsprofiler";

        public override VideoOSIconSourceBase IconSource => TimeProfileEditorPluginDefinition.IconSource;

        /// <summary>No cameras here, so the timeline and the live/playback toggle are noise.</summary>
        public override bool ShowTimeline => false;

        public override bool Live => false;

        /// <summary>The layout is fixed; there is nothing for an operator to rearrange.</summary>
        public override bool IsSetupStateSupported => false;

        /// <summary>
        /// Smart Client owns the workspace's layout object, so the single full-size cell holding
        /// the editor is filled in here rather than by overriding the property. This follows the
        /// shape of Milestone's own WorkSpacePlugin samples: the layout is rebuilt on every Init,
        /// because MIP hands out a fresh ViewAndLayoutItem per session rather than persisting one.
        /// </summary>
        public override void Init()
        {
            try
            {
                _viewAndLayoutItem = ViewAndLayoutItem;
                if (_viewAndLayoutItem == null) return;

                // MIP lays views out in a 1000x1000 relative space; one cell filling it all
                // gives the editor the whole workspace.
                _viewAndLayoutItem.Layout = new[] { new Rectangle(0, 0, 1000, 1000) };
                _viewAndLayoutItem.Name = Name;

                var viewItemPlugin = TimeProfileEditorPluginDefinition.ViewItemPluginInstance;
                if (viewItemPlugin != null)
                    _viewAndLayoutItem.InsertViewItemPlugin(0, viewItemPlugin, null);
            }
            catch (Exception ex)
            {
                ChangeLog.Error("Kunde inte skapa arbetsytans layout", ex);
            }
        }

        public override void Close()
        {
            _viewAndLayoutItem = null;
        }
    }
}
