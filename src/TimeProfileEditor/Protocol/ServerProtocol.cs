using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using TimeProfileEditor.Model;

namespace TimeProfileEditor.Protocol
{
    /// <summary>
    /// What the Smart Client and the Event Server component say to each other.
    ///
    /// Everything crosses as a JSON string rather than as an object. MIP will carry a custom type
    /// happily enough, but it resolves that type by name on the far side, and the two ends live in
    /// differently named assemblies - so a shared payload class is a version trap waiting for the
    /// first release where only one side is updated. A string has no such problem, and it stays
    /// readable when it ends up in a log.
    ///
    /// The file is compiled into both the client and the server plugin. Changing a field name here
    /// changes the wire format, so a mismatched pair of versions must be able to say so: that is
    /// what <see cref="Version"/> is for.
    /// </summary>
    internal static class ServerProtocol
    {
        /// <summary>
        /// Bumped whenever the shape below changes in a way an older counterpart cannot read. The
        /// server refuses a request it does not recognise rather than guessing at it.
        /// </summary>
        public const int Version = 1;

        public const string PingRequest = "TimeProfileEditor.Ping.Request";
        public const string PingResponse = "TimeProfileEditor.Ping.Response";
        public const string LoadRequest = "TimeProfileEditor.Load.Request";
        public const string LoadResponse = "TimeProfileEditor.Load.Response";
        public const string SaveRequest = "TimeProfileEditor.Save.Request";
        public const string SaveResponse = "TimeProfileEditor.Save.Response";

        public static string ToJson<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return serializer.ReadObject(stream) as T;
        }
    }

    /// <summary>
    /// Every request the client makes.
    ///
    /// <see cref="Token"/> is the whole of the client's claim to be someone. It is never trusted as
    /// a statement - the server establishes both that it is genuine and who it belongs to before
    /// acting on anything else in the message. Nothing else in here identifies the caller, on
    /// purpose: a user name in a request is a suggestion, not evidence.
    /// </summary>
    [DataContract]
    internal sealed class ClientRequest
    {
        [DataMember(Name = "protocol")] public int Protocol { get; set; } = ServerProtocol.Version;

        [DataMember(Name = "token")] public string Token { get; set; }

        /// <summary>
        /// The caller's XProtect identity - LoginSettings.UserIdentity, the same string the client
        /// passes to its own permission check.
        ///
        /// It is a claim, and it is treated as one: the server accepts it only after the token above
        /// has been proven genuine and found to name this same identity. It travels separately
        /// rather than being taken from the token alone because this is the spelling the Management
        /// Server's permission API expects, and a token's subject claim is whatever the identity
        /// provider chose to put there.
        /// </summary>
        [DataMember(Name = "identity")] public string Identity { get; set; }

        /// <summary>Ties a response to its request; the message channel is a broadcast bus.</summary>
        [DataMember(Name = "correlationId")] public string CorrelationId { get; set; }

        [DataMember(Name = "profileId")] public string ProfileId { get; set; }

        /// <summary>What the operator wants the profile to look like.</summary>
        [DataMember(Name = "entries")] public List<WireEntry> Entries { get; set; }

        /// <summary>
        /// What it looked like when they started editing, and when the server last said it changed.
        ///
        /// Both travel with the request because the repository compares against them before
        /// writing: two operators editing the same profile must not silently overwrite each other,
        /// and the component is the only place that can tell. Leaving them out would turn every
        /// save into a blind overwrite.
        /// </summary>
        [DataMember(Name = "baseline")] public List<WireEntry> Baseline { get; set; }

        [DataMember(Name = "expectedLastModified")] public string ExpectedLastModified { get; set; }
    }

    [DataContract]
    internal sealed class ServerResponse
    {
        [DataMember(Name = "protocol")] public int Protocol { get; set; } = ServerProtocol.Version;

        [DataMember(Name = "correlationId")] public string CorrelationId { get; set; }

        /// <summary>One of <see cref="ResponseStatus"/>.</summary>
        [DataMember(Name = "status")] public string Status { get; set; }

        /// <summary>Shown to the operator. Swedish, and safe to display as-is.</summary>
        [DataMember(Name = "message")] public string Message { get; set; }

        [DataMember(Name = "profiles")] public List<WireProfile> Profiles { get; set; }

        [DataMember(Name = "entries")] public List<WireEntry> Entries { get; set; }

        /// <summary>What the save actually changed, for the client to log and display.</summary>
        [DataMember(Name = "changes")] public List<string> Changes { get; set; }

        /// <summary>
        /// When the server last saw the profile change. The client hands it back on the next save
        /// so the component can tell a stale edit from a current one.
        /// </summary>
        [DataMember(Name = "lastModified")] public string LastModified { get; set; }
    }

    internal static class ResponseStatus
    {
        public const string Ok = "ok";
        public const string Denied = "denied";
        public const string Failed = "failed";
        public const string NothingToDo = "nothing";
    }

    [DataContract]
    internal sealed class WireProfile
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "type")] public string ProfileType { get; set; }

        public static WireProfile From(TimeProfileInfo profile) => new WireProfile
        {
            Id = profile.Id.ToString(),
            Name = profile.Name,
            Description = profile.Description,
            ProfileType = profile.ProfileType
        };

        public TimeProfileInfo ToModel() => new TimeProfileInfo
        {
            Id = Guid.TryParse(Id, out var id) ? id : Guid.Empty,
            Name = Name,
            Description = Description,
            ProfileType = ProfileType
        };
    }

    /// <summary>
    /// One schedule entry on the wire.
    ///
    /// Deliberately not <see cref="ScheduleEntry"/> itself: that one carries change notification
    /// and a client-side key, and coupling the format the two processes agree on to the class the
    /// UI happens to bind to means every cosmetic change to the view model is a protocol change.
    ///
    /// Times are ticks and dates are round-trip strings rather than whatever the JSON serializer
    /// would make of TimeSpan and DateTime, so a message stays unambiguous when read by a human.
    /// </summary>
    [DataContract]
    internal sealed class WireEntry
    {
        [DataMember(Name = "rootId")] public string AppointmentRootId { get; set; }
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "days")] public int Days { get; set; }
        [DataMember(Name = "startTicks")] public long StartTicks { get; set; }
        [DataMember(Name = "durationTicks")] public long DurationTicks { get; set; }
        [DataMember(Name = "subject")] public string Subject { get; set; }
        [DataMember(Name = "serverDescription")] public string ServerDescription { get; set; }
        [DataMember(Name = "rangeStart")] public string RangeStart { get; set; }
        [DataMember(Name = "rangeEnd")] public string RangeEnd { get; set; }
        [DataMember(Name = "occurrenceStart")] public string OccurrenceStart { get; set; }
        [DataMember(Name = "occurrenceEnd")] public string OccurrenceEnd { get; set; }
        [DataMember(Name = "allDay")] public bool AllDayEvent { get; set; }

        public static WireEntry From(ScheduleEntry entry) => new WireEntry
        {
            AppointmentRootId = entry.AppointmentRootId,
            Kind = entry.Kind.ToString(),
            Days = (int)entry.Days,
            StartTicks = entry.Start.Ticks,
            DurationTicks = entry.Duration.Ticks,
            Subject = entry.Subject,
            ServerDescription = entry.ServerDescription,
            RangeStart = Write(entry.RangeStart),
            RangeEnd = Write(entry.RangeEnd),
            OccurrenceStart = Write(entry.OccurrenceStart),
            OccurrenceEnd = Write(entry.OccurrenceEnd),
            AllDayEvent = entry.AllDayEvent
        };

        public ScheduleEntry ToModel()
        {
            var entry = new ScheduleEntry
            {
                AppointmentRootId = AppointmentRootId,
                Kind = Enum.TryParse<ScheduleEntryKind>(Kind, out var kind) ? kind : ScheduleEntryKind.Weekly,
                Days = (DayFlags)Days,
                Start = TimeSpan.FromTicks(StartTicks),
                Duration = TimeSpan.FromTicks(DurationTicks),
                Subject = Subject,
                ServerDescription = ServerDescription,
                AllDayEvent = AllDayEvent
            };

            var rangeStart = Read(RangeStart);
            if (rangeStart.HasValue) entry.RangeStart = rangeStart.Value;
            entry.RangeEnd = Read(RangeEnd);
            entry.OccurrenceStart = Read(OccurrenceStart);
            entry.OccurrenceEnd = Read(OccurrenceEnd);
            return entry;
        }

        public static List<WireEntry> From(IEnumerable<ScheduleEntry> entries) =>
            entries?.Select(From).ToList() ?? new List<WireEntry>();

        public static List<ScheduleEntry> ToModel(IEnumerable<WireEntry> entries) =>
            entries?.Select(e => e.ToModel()).ToList() ?? new List<ScheduleEntry>();

        private static string Write(DateTime value) =>
            value.ToString("o", CultureInfo.InvariantCulture);

        private static string Write(DateTime? value) => value.HasValue ? Write(value.Value) : null;

        private static DateTime? Read(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTime?)null;
    }
}
