using System;
using System.Collections.Generic;

namespace TimeProfileEditor.Model
{
    internal sealed class TimeProfileInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>"Calendar" or "Sunclock" as reported by the Management Server.</summary>
        public string ProfileType { get; set; }

        /// <summary>
        /// Sunclock profiles follow sunrise/sunset and carry no appointments at all - there is
        /// no week for this plugin to show, let alone edit. They stay visible but read-only.
        /// </summary>
        public bool IsSunclock =>
            string.Equals(ProfileType, "Sunclock", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Name;
    }

    internal sealed class ProfileSchedule
    {
        public TimeProfileInfo Profile { get; set; }
        public List<ScheduleEntry> Entries { get; set; } = new List<ScheduleEntry>();

        /// <summary>
        /// Server timestamp captured when the profile was read, used to refuse a save that
        /// would silently overwrite somebody else's change made in the meantime.
        /// </summary>
        public DateTime LastModified { get; set; }
    }
}
