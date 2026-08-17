using System;
using System.Collections.Generic;
using System.Reflection;

namespace TimeProfileEditor.Model
{
    /// <summary>One labelled line of the information panel.</summary>
    internal sealed class Fact
    {
        public Fact(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }

    /// <summary>
    /// What the plugin says about itself.
    ///
    /// The same handful of facts - name, version, developer - are on display in three places at
    /// once: the information panel in Smart Client, the plugin list in Management Client, and the
    /// file properties in Explorer. They are read out of the assembly here rather than written
    /// down a second time, because the way three copies of a version number go wrong is that
    /// someone updates one of them.
    /// </summary>
    internal static class PluginInfo
    {
        /// <summary>Used when the assembly carries no company of its own.</summary>
        public const string DeveloperName = "Nordic InSupport Nätverksvideo AB";

        /// <summary>Swedish throughout - texts, dates, weekday names and week numbers alike.</summary>
        public const string Language = "Svenska";

        /// <summary>
        /// Deliberately blank. There is no licence text to state yet, and an invented one would be
        /// worse than none; the panel shows "Ej angiven" rather than an empty row, so a reader can
        /// tell the difference between undecided and broken.
        /// </summary>
        public const string License = "";

        public static string Name => Read<AssemblyProductAttribute>(a => a.Product) ?? "Tidsprofiler";

        public static string Description =>
            Read<AssemblyTitleAttribute>(a => a.Title) ?? "Tillägg för XProtect Smart Client.";

        public static string Developer => Read<AssemblyCompanyAttribute>(a => a.Company) ?? DeveloperName;

        /// <summary>
        /// The version to quote in a support thread: "1.6.0", the way the MSI is named, rather
        /// than the four-part "1.6.0.0". Anything after a '+' is build metadata identifying a
        /// commit rather than a release, and is dropped.
        /// </summary>
        public static string Version
        {
            get
            {
                var informational = Read<AssemblyInformationalVersionAttribute>(a => a.InformationalVersion);
                if (informational == null) return FileVersion;

                var plus = informational.IndexOf('+');
                return plus < 0 ? informational : informational.Substring(0, plus);
            }
        }

        /// <summary>The four-part assembly version, for the places that expect that shape.</summary>
        public static string FileVersion =>
            typeof(PluginInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        /// <summary>The information panel, in the order it is read.</summary>
        public static IReadOnlyList<Fact> Facts => new[]
        {
            new Fact("Namn", Name),
            new Fact("Version", Version),
            new Fact("Utvecklare", Developer),
            new Fact("Språk", Language),
            new Fact("Licens", string.IsNullOrWhiteSpace(License) ? "Ej angiven" : License)
        };

        private static string Read<T>(Func<T, string> value) where T : Attribute
        {
            try
            {
                var attribute = typeof(PluginInfo).Assembly.GetCustomAttribute<T>();
                var text = attribute == null ? null : value(attribute);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                // Nothing about describing yourself is worth throwing over.
                return null;
            }
        }
    }
}
