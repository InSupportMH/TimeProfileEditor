using System.Collections.Generic;

namespace TimeProfileEditor.Services
{
    internal enum SaveStatus
    {
        Success,
        NothingToDo,
        PermissionDenied,
        Conflict,
        Failed,

        /// <summary>Some changes were written before a later one failed. There is no transaction.</summary>
        PartiallyApplied,

        /// <summary>
        /// The profile is not in the configuration this caller was able to read - and that is all it
        /// means. It is not "deleted": XProtect answers a configuration read it disagrees with by
        /// handing back the items the caller may see rather than by refusing, so an operator on
        /// Professional+ reads an empty folder and every profile looks gone.
        ///
        /// Kept apart from <see cref="Failed"/> so the difference is decidable by the caller rather
        /// than guessed at from a message. Nothing has been written when this is returned, which is
        /// what makes it safe to retry the save by another route.
        /// </summary>
        NotVisible
    }

    internal sealed class SaveOutcome
    {
        public SaveStatus Status { get; set; }
        public string Message { get; set; }

        /// <summary>Human-readable diff, in the order it was applied. Also what gets logged.</summary>
        public List<string> AppliedChanges { get; } = new List<string>();

        public bool IsSuccess => Status == SaveStatus.Success || Status == SaveStatus.NothingToDo;

        public static SaveOutcome Denied(string message) =>
            new SaveOutcome { Status = SaveStatus.PermissionDenied, Message = message };

        public static SaveOutcome Fail(string message) =>
            new SaveOutcome { Status = SaveStatus.Failed, Message = message };

        public static SaveOutcome NotVisible(string message) =>
            new SaveOutcome { Status = SaveStatus.NotVisible, Message = message };
    }
}
