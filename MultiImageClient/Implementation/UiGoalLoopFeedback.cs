#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient
{
    public sealed class UiGoalFeedback
    {
        public string RequestId { get; set; } = "";
        public string Scope { get; set; } = "";
        public int TargetEntryIndex { get; set; }
        public string TargetKey { get; set; } = "";
        public int Delta { get; set; }
        public int Total { get; set; }
        public string Actor { get; set; } = "";
    }

    public static class UiGoalLoopFeedback
    {
        public const string Kind = "feedback";
        private const int MaxFeedbackEntries = 10000;
        public static int Revision(IReadOnlyList<UiGoalLoopEntry> entries)
            => entries.LastOrDefault(e => e.Feedback != null)?.Index ?? -1;

        public static UiGoalFeedback Create(IReadOnlyList<UiGoalLoopEntry> entries, string requestId,
            string scope, int targetEntryIndex, int delta, string actor)
        {
            if (!Guid.TryParseExact(requestId, "D", out _)) throw new InvalidDataException("feedback requestId must be a UUID");
            if (scope is not ("image" or "prompt") || delta is not (-1 or 1))
                throw new InvalidDataException("feedback requires image or prompt scope and a +1 or -1 delta");
            var existing = entries.Select(e => e.Feedback).FirstOrDefault(f => f?.RequestId == requestId);
            if (existing != null)
            {
                if (existing.Scope != scope || existing.TargetEntryIndex != targetEntryIndex || existing.Delta != delta || existing.Actor != actor)
                    throw new InvalidDataException("feedback requestId already belongs to another operation");
                return existing;
            }
            if (entries.Count(e => e.Feedback != null) >= MaxFeedbackEntries)
                throw new InvalidDataException("this loop has reached its 10000 feedback-event limit");
            var target = entries.SingleOrDefault(e => e.Index == targetEntryIndex);
            if (target == null || (scope == "image" && (target.Kind != UiGoalLoopKinds.RenderResult || target.Render?.Ok != true))
                || (scope == "prompt" && (target.Kind != UiGoalLoopKinds.RenderRequest || string.IsNullOrEmpty(target.Text))))
                throw new InvalidDataException("feedback must identify an exact successful image or rendered prompt request");
            if (scope == "image" && (string.IsNullOrWhiteSpace(target.Render!.JobId) || string.IsNullOrWhiteSpace(target.Render.GeneratorKey)))
                throw new InvalidDataException("the image lacks its exact job and generator identity");
            // Identical prompt bytes deliberately share one preference target within this loop, across samples and turns.
            var key = scope == "prompt" ? "prompt:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Text)))
                : $"image:{target.Render!.JobId}/{target.Render.GeneratorKey}/0";
            var total = entries.Where(e => e.Feedback?.TargetKey == key).Sum(e => e.Feedback!.Delta);
            return new() { RequestId = requestId, Scope = scope, TargetEntryIndex = targetEntryIndex,
                TargetKey = key, Delta = delta, Total = checked(total + delta), Actor = actor };
        }

        public static string Snapshot(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            var feedback = entries.Where(e => e.Feedback != null).ToList();
            if (feedback.Count == 0) return "";
            var targets = feedback.GroupBy(e => e.Feedback!.TargetKey).Select(group =>
            {
                var f = group.Last().Feedback!;
                var target = entries.Single(e => e.Index == f.TargetEntryIndex);
                return new { scope = f.Scope, entryIndex = target.Index, turn = target.Turn, variant = target.Render!.Variant,
                    source = target.Render.Source, points = group.Sum(e => e.Feedback!.Delta),
                    prompt = f.Scope == "prompt" ? target.Text : null };
            }).ToList();
            return $"\n\nCURRENT OPERATOR FEEDBACK (through entry {Revision(entries)}):\n"
                + JsonSerializer.Serialize(targets, UiGoalLoopJson.Options)
                + "\nThese are cumulative owner preferences, separate from critic scores and required criteria. "
                + "Image points apply only to the exact image. Prompt points apply to identical prompt text throughout this loop. "
                + "Reconsider earlier directions when these preferences materially change. Explain their effect on your next decision. "
                + "Later clicks cannot change this snapshot. Do not claim to see archived pixels that are not attached.";
        }

        public static bool NeedsPlanning(IReadOnlyList<UiGoalLoopEntry> entries, UiGoalLoopStep step)
        {
            if (step.Kind is not (UiGoalLoopStepKind.Render or UiGoalLoopStepKind.Exhausted or UiGoalLoopStepKind.Done)) return false;
            var manager = entries.LastOrDefault(e => UiGoalLoopKinds.IsManagerReply(e.Kind) && e.Error == null && e.Manager?.Parsed != null);
            if (manager == null || Revision(entries) <= (manager.Manager!.FeedbackThroughEntry ?? -1)) return false;
            // Never replace work already started. The normal review will consume feedback after its images finish.
            var plannedTurn = manager.Kind == UiGoalLoopKinds.Design ? manager.Turn : manager.Turn + 1;
            return !entries.Any(e => e.Kind == UiGoalLoopKinds.RenderRequest && e.Turn == plannedTurn);
        }
    }

    internal sealed partial class UiGoalLoopRunner
    {
        public UiGoalFeedback AddFeedback(UiGoalLoopState state, string requestId, string scope, int targetEntryIndex, int delta, string actor)
        {
            lock (state.Lock)
            {
                var feedback = UiGoalLoopFeedback.Create(state.Entries, requestId, scope, targetEntryIndex, delta, actor);
                if (state.Entries.Any(e => e.Feedback?.RequestId == requestId)) return feedback;
                var target = state.Entries.Single(e => e.Index == targetEntryIndex);
                AppendEntryLocked(state, new UiGoalLoopEntry { Kind = UiGoalLoopFeedback.Kind, Turn = target.Turn,
                    From = UiGoalLoopParties.User, To = UiGoalLoopParties.Manager, Feedback = feedback,
                    Text = $"{actor}: {delta:+0;-0} {scope} points for entry {targetEntryIndex}; total {feedback.Total:+0;-0;0}." });
                return feedback;
            }
        }
    }
}
