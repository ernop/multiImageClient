#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Process-wide, work-conserving scheduler for remote image targets. It
    /// applies both the shared-host aggregate cap and provider/account caps,
    /// while rotating across ready targets and users instead of letting one
    /// large job fill a global semaphore's waiter list.
    internal sealed class UiTargetScheduler
    {
        public const string LaneOpenAi = "openai";
        public const string LaneXaiApi = "xai-api";
        public const string LaneGrokWebWs = "grok-web-ws";
        public const string LaneGrokWebBrowser = "grok-web-browser";
        public const string LaneMetaWeb = "meta-web";
        public const string LaneGoogle = "google";
        public const string LaneBfl = "bfl";
        public const string LaneKrea = "krea";
        public const string LaneIdeogram = "ideogram";
        public const string LaneRecraft = "recraft";
        public const string LaneComfyUi = "comfyui";
        // Describe targets ride their provider-account lanes (an OpenAI describe
        // competes with gpt-image jobs for the same account); this one exists
        // only because Anthropic has no image lane.
        public const string LaneAnthropic = "anthropic";

        private static readonly IReadOnlyDictionary<string, int> DefaultLimits =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [LaneOpenAi] = 2,
                [LaneXaiApi] = 2,
                [LaneGrokWebWs] = 1,
                [LaneGrokWebBrowser] = 1,
                [LaneMetaWeb] = 1,
                [LaneGoogle] = 2,
                [LaneBfl] = 2,
                [LaneKrea] = 2,
                [LaneIdeogram] = 1,
                [LaneRecraft] = 2,
                [LaneComfyUi] = 1,
                [LaneAnthropic] = 2,
            };

        private readonly object _gate = new();
        private readonly int _globalLimit;
        private readonly Dictionary<string, Lane> _lanes;
        private readonly List<string> _laneOrder;
        private int _nextLaneIndex;
        private int _globalRunning;
        private int _queued;

        public UiTargetScheduler(int globalLimit, IDictionary<string, int>? configuredLimits)
        {
            if (globalLimit < 1 || globalLimit > 32)
            {
                throw new InvalidOperationException(
                    $"UiMaxConcurrentGenerators must be between 1 and 32; got {globalLimit}.");
            }

            var limits = new Dictionary<string, int>(DefaultLimits, StringComparer.OrdinalIgnoreCase);
            if (configuredLimits != null)
            {
                foreach (var pair in configuredLimits)
                {
                    var name = (pair.Key ?? "").Trim();
                    if (!limits.ContainsKey(name))
                    {
                        throw new InvalidOperationException(
                            $"Unknown UiTargetConcurrency lane '{pair.Key}'. Expected one of: "
                            + string.Join(", ", DefaultLimits.Keys.OrderBy(x => x)));
                    }
                    if (pair.Value < 1 || pair.Value > 32)
                    {
                        throw new InvalidOperationException(
                            $"UiTargetConcurrency['{name}'] must be between 1 and 32; got {pair.Value}.");
                    }
                    limits[name] = pair.Value;
                }
            }

            _globalLimit = globalLimit;
            _lanes = limits.ToDictionary(
                pair => pair.Key,
                pair => new Lane(pair.Key, pair.Value),
                StringComparer.OrdinalIgnoreCase);
            _laneOrder = DefaultLimits.Keys.ToList();
        }

        public Task<TaskProcessResult> ScheduleAsync(
            string laneName,
            string user,
            Func<Task<TaskProcessResult>> executeAsync)
        {
            if (executeAsync == null) throw new ArgumentNullException(nameof(executeAsync));

            var item = new WorkItem(
                string.IsNullOrWhiteSpace(user) ? "(unknown)" : user,
                executeAsync);
            lock (_gate)
            {
                if (!_lanes.TryGetValue(laneName, out var lane))
                {
                    throw new InvalidOperationException($"Unknown UI target scheduler lane '{laneName}'.");
                }
                lane.Enqueue(item);
                _queued++;
            }
            Dispatch();
            return item.Completion.Task;
        }

        public UiTargetSchedulerSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new UiTargetSchedulerSnapshot(
                    _queued,
                    _globalRunning,
                    _globalLimit,
                    _lanes.Values
                        .OrderBy(lane => lane.Name, StringComparer.Ordinal)
                        .Select(lane => new UiTargetLaneSnapshot(
                            lane.Name,
                            lane.QueuedCount,
                            lane.Running,
                            lane.Limit))
                        .ToArray());
            }
        }

        public static string ResolveLane(string generatorKey, bool hasInputImage)
        {
            return generatorKey switch
            {
                UiJobRunner.KeyGpt2 or UiJobRunner.KeyGpt1 or UiJobRunner.KeyGpt1Mini => LaneOpenAi,
                UiJobRunner.KeyGrokApi or UiJobRunner.KeyGrokApiPro => LaneXaiApi,
                UiJobRunner.KeyGrokWebVideo => LaneGrokWebBrowser,
                UiJobRunner.KeyGrokWeb => LaneGrokWebWs,
                UiJobRunner.KeyGrokWebChat => LaneGrokWebWs,
                UiJobRunner.KeyMetaWeb => LaneMetaWeb,
                UiJobRunner.KeyGoogle or UiJobRunner.KeyGooglePro => LaneGoogle,
                UiJobRunner.KeyBfl or UiJobRunner.KeyBflFlux2Pro
                    or UiJobRunner.KeyBflFlux2Max or UiJobRunner.KeyBflFlux2Flex
                    or UiJobRunner.KeyBflFlux2Klein4b or UiJobRunner.KeyBflFlux2Klein9bPreview
                    or UiJobRunner.KeyBflFlux2Klein9b or UiJobRunner.KeyBflKontextPro
                    or UiJobRunner.KeyBflKontextMax or UiJobRunner.KeyBflFlux11Ultra
                    or UiJobRunner.KeyBflFlux11 or UiJobRunner.KeyBflFluxPro
                    or UiJobRunner.KeyBflFluxDev => LaneBfl,
                UiJobRunner.KeyKrea or UiJobRunner.KeyKreaTurbo
                    or UiJobRunner.KeyKreaLarge => LaneKrea,
                UiJobRunner.KeyIdeogram or UiJobRunner.KeyIdeogramV3
                    or UiJobRunner.KeyIdeogramV2 => LaneIdeogram,
                UiJobRunner.KeyRecraft or UiJobRunner.KeyRecraftV41Utility
                    or UiJobRunner.KeyRecraftV41Pro or UiJobRunner.KeyRecraftV41Vector
                    or UiJobRunner.KeyRecraftV3 or UiJobRunner.KeyRecraftV4
                    or UiJobRunner.KeyRecraftV4Pro => LaneRecraft,
                UiJobRunner.KeyLocalKlein or UiJobRunner.KeyLocalZImage => LaneComfyUi,
                UiJobRunner.KeyDescribeOpenAi => LaneOpenAi,
                UiJobRunner.KeyDescribeGrok => LaneXaiApi,
                UiJobRunner.KeyDescribeGemini => LaneGoogle,
                UiJobRunner.KeyDescribeIdeogram => LaneIdeogram,
                UiJobRunner.KeyDescribeClaude => LaneAnthropic,
                _ => throw new InvalidOperationException(
                    $"Generator '{generatorKey}' has no UI target scheduler lane."),
            };
        }

        private void Dispatch()
        {
            List<(Lane Lane, WorkItem Item)> started = new();
            lock (_gate)
            {
                while (_globalRunning < _globalLimit && TryTakeNextLocked(out var lane, out var item))
                {
                    lane.Running++;
                    _globalRunning++;
                    _queued--;
                    started.Add((lane, item));
                }
            }

            foreach (var entry in started)
            {
                _ = ExecuteAsync(entry.Lane, entry.Item);
            }
        }

        private bool TryTakeNextLocked(out Lane lane, out WorkItem item)
        {
            for (var offset = 0; offset < _laneOrder.Count; offset++)
            {
                var index = (_nextLaneIndex + offset) % _laneOrder.Count;
                var candidate = _lanes[_laneOrder[index]];
                if (candidate.Running >= candidate.Limit || !candidate.TryDequeue(out item))
                {
                    continue;
                }

                _nextLaneIndex = (index + 1) % _laneOrder.Count;
                lane = candidate;
                return true;
            }

            lane = null!;
            item = null!;
            return false;
        }

        private async Task ExecuteAsync(Lane lane, WorkItem item)
        {
            try
            {
                item.Completion.TrySetResult(await item.ExecuteAsync());
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
            finally
            {
                lock (_gate)
                {
                    lane.Running--;
                    _globalRunning--;
                }
                Dispatch();
            }
        }

        private sealed class WorkItem
        {
            public WorkItem(string user, Func<Task<TaskProcessResult>> executeAsync)
            {
                User = user;
                ExecuteAsync = executeAsync;
            }

            public string User { get; }
            public Func<Task<TaskProcessResult>> ExecuteAsync { get; }
            public TaskCompletionSource<TaskProcessResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Lane
        {
            private readonly Dictionary<string, Queue<WorkItem>> _queues =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _userOrder = new();
            private int _nextUserIndex;

            public Lane(string name, int limit)
            {
                Name = name;
                Limit = limit;
            }

            public string Name { get; }
            public int Limit { get; }
            public int Running { get; set; }
            public int QueuedCount { get; private set; }

            public void Enqueue(WorkItem item)
            {
                if (!_queues.TryGetValue(item.User, out var queue))
                {
                    queue = new Queue<WorkItem>();
                    _queues[item.User] = queue;
                    _userOrder.Add(item.User);
                }
                queue.Enqueue(item);
                QueuedCount++;
            }

            public bool TryDequeue(out WorkItem item)
            {
                if (_userOrder.Count == 0)
                {
                    item = null!;
                    return false;
                }

                if (_nextUserIndex >= _userOrder.Count) _nextUserIndex = 0;
                var user = _userOrder[_nextUserIndex];
                var queue = _queues[user];
                item = queue.Dequeue();
                QueuedCount--;

                if (queue.Count == 0)
                {
                    _queues.Remove(user);
                    _userOrder.RemoveAt(_nextUserIndex);
                    if (_nextUserIndex >= _userOrder.Count) _nextUserIndex = 0;
                }
                else
                {
                    _nextUserIndex = (_nextUserIndex + 1) % _userOrder.Count;
                }
                return true;
            }
        }
    }

    internal sealed record UiTargetSchedulerSnapshot(
        int Queued,
        int Running,
        int GlobalLimit,
        IReadOnlyList<UiTargetLaneSnapshot> Lanes);

    internal sealed record UiTargetLaneSnapshot(
        string Name,
        int Queued,
        int Running,
        int Limit);
}
