using System.Diagnostics;

namespace ArcadeLibraryManager.Core;

public sealed record ThemeBatchItem(string Id, string Title, string Status, string Detail, double Progress, TimeSpan Elapsed);

/// <summary>Tracks a sequential theme queue independently of renderer stages and UI timer frequency.</summary>
public sealed class ThemeBatchProgress
{
    private sealed class Item(string id, string title)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Status { get; set; } = "Queued";
        public string Detail { get; set; } = "";
        public double Progress { get; set; }
        public TimeSpan Started { get; set; }
        public TimeSpan Elapsed { get; set; }
    }
    private readonly object sync = new();
    private readonly List<Item> items;
    private readonly Dictionary<string, Item> byId;
    private readonly Func<TimeSpan> clock;
    private readonly TimeSpan origin;
    private readonly List<TimeSpan> successfulDurations = [];
    private TimeSpan elapsed;
    private double percent;
    private string currentId = "";
    private bool running, paused;
    private TimeSpan observedClock, pausedAt, pausedDuration;

    public ThemeBatchProgress(IEnumerable<(string Id, string Title)> games, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        items = []; byId = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, title) in games)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Each theme needs a nonempty game ID.", nameof(games));
            var item = new Item(id, string.IsNullOrWhiteSpace(title) ? id : title);
            if (!byId.TryAdd(id, item)) throw new ArgumentException("A theme batch cannot contain duplicate game IDs: " + id, nameof(games));
            items.Add(item);
        }
        var stopwatch = Stopwatch.StartNew();
        clock = elapsed ?? (() => stopwatch.Elapsed);
        origin = clock(); observedClock = origin;
        running = items.Count > 0;
    }

    public int Total => items.Count;
    public int Finished { get { lock (sync) return FinishedCount(); } }
    public int Succeeded { get { lock (sync) return items.Count(i => i.Status == "Completed"); } }
    public int Skipped { get { lock (sync) return items.Count(i => i.Status == "Skipped"); } }
    public int Failed { get { lock (sync) return items.Count(i => i.Status == "Failed"); } }
    public bool IsRunning { get { lock (sync) return running; } }
    public bool IsPaused { get { lock (sync) return paused; } }
    public string CurrentId { get { lock (sync) return currentId; } }
    public TimeSpan Elapsed { get { lock (sync) return RefreshElapsed(); } }
    public double Percent { get { lock (sync) return UpdatePercent(); } }
    public IReadOnlyList<ThemeBatchItem> Items
    {
        get
        {
            lock (sync)
            {
                var now = RefreshElapsed();
                return items.Select(i => new ThemeBatchItem(i.Id, i.Title, i.Status, i.Detail, i.Progress,
                    i.Status == "Running" ? PositiveDifference(now, i.Started) : i.Elapsed)).ToArray();
            }
        }
    }
    public TimeSpan? Remaining
    {
        get
        {
            lock (sync)
            {
                var now = RefreshElapsed();
                if (Total > 0 && FinishedCount() == Total) return TimeSpan.Zero;
                if (!running || successfulDurations.Count == 0) return null;
                var averageTicks = successfulDurations.Average(d => (double)d.Ticks);
                var active = currentId.Length > 0 ? byId[currentId] : null;
                var futureCount = Total - FinishedCount() - (active is null ? 0 : 1);
                var activeTicks = active is null ? 0 : Math.Max(0, averageTicks - PositiveDifference(now, active.Started).Ticks);
                return BoundedTime(averageTicks * futureCount + activeTicks);
            }
        }
    }

    public void Start(string id)
    {
        lock (sync)
        {
            var item = Find(id);
            if (!running) throw new InvalidOperationException("This theme batch is stopped.");
            if (paused) throw new InvalidOperationException("Resume the theme batch before starting another game.");
            if (item.Status == "Running" && currentId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)) return;
            if (currentId.Length > 0) throw new InvalidOperationException("Complete the current theme before starting another.");
            if (item.Status != "Queued") throw new InvalidOperationException("This theme is no longer queued.");
            item.Started = RefreshElapsed(); item.Status = "Running"; item.Detail = "Preparing theme";
            currentId = item.Id;
        }
    }

    public void Report(string id, JobEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (sync)
        {
            if (!running || currentId.Length == 0 || !currentId.Equals(id, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(update.GameId) && !currentId.Equals(update.GameId, StringComparison.OrdinalIgnoreCase))) return;
            var item = byId[currentId];
            RefreshElapsed();
            item.Detail = update.Message ?? "";
            if (update.Percent is double value && double.IsFinite(value)) item.Progress = Math.Max(item.Progress, Math.Clamp(value, 0, 99));
            UpdatePercent();
        }
    }

    public void Complete(string id, string status, string detail = "")
    {
        lock (sync)
        {
            if (!running) return; // Late callbacks from a finished or cancelled batch are inert.
            if (paused) throw new InvalidOperationException("Resume the theme batch before completing another game.");
            if (status is not ("Completed" or "Skipped" or "Failed")) throw new ArgumentException("Use Completed, Skipped, or Failed for a finished theme.", nameof(status));
            var item = Find(id);
            if (FinishedItem(item)) return;
            if (item.Status != "Running" && !(item.Status == "Queued" && status == "Skipped"))
                throw new InvalidOperationException("Start this theme before completing it.");
            var now = RefreshElapsed();
            if (item.Status == "Running") { item.Elapsed = PositiveDifference(now, item.Started); currentId = ""; }
            item.Status = status; item.Detail = detail; item.Progress = 100;
            // Instant no-op completions cannot be useful estimates for a full generated theme.
            if (status == "Completed" && item.Elapsed >= TimeSpan.FromSeconds(1)) successfulDurations.Add(item.Elapsed);
            UpdatePercent();
            if (FinishedCount() == Total) running = false;
        }
    }

    /// <summary>Pauses between themes; the current render must finish before this is called.</summary>
    public void Pause()
    {
        lock (sync)
        {
            if (!running || paused) return;
            if (currentId.Length > 0) throw new InvalidOperationException("Finish the current theme before pausing the batch.");
            RefreshElapsed(); pausedAt = observedClock; paused = true;
        }
    }

    public void Resume()
    {
        lock (sync)
        {
            if (!running || !paused) return;
            pausedDuration = BoundedTime((double)pausedDuration.Ticks + PositiveDifference(ObserveClock(), pausedAt).Ticks);
            paused = false;
        }
    }

    public void Stop(bool cancelled = false, string? detail = null)
    {
        lock (sync)
        {
            if (!running) return;
            var now = RefreshElapsed(); UpdatePercent();
            if (currentId.Length > 0)
            {
                var active = byId[currentId];
                active.Elapsed = PositiveDifference(now, active.Started);
                active.Status = cancelled ? "Cancelled" : "Failed";
                active.Detail = detail ?? (cancelled ? "Cancelled before completion" : "Stopped before completion");
                if (!cancelled) active.Progress = 100;
            }
            foreach (var pending in items.Where(i => i.Status == "Queued"))
            {
                pending.Status = "Not started";
                pending.Detail = detail ?? (cancelled ? "Batch cancelled" : "Batch stopped");
            }
            currentId = "";
            UpdatePercent(); running = false; paused = false;
        }
    }

    private Item Find(string id) => byId.TryGetValue(id, out var item) ? item : throw new ArgumentException("Unknown theme game ID: " + id, nameof(id));
    private static bool FinishedItem(Item item) => item.Status is "Completed" or "Skipped" or "Failed";
    private int FinishedCount() => items.Count(FinishedItem);
    private TimeSpan RefreshElapsed()
    {
        if (running && !paused)
        {
            var current = PositiveDifference(PositiveDifference(ObserveClock(), origin), pausedDuration);
            if (current > elapsed) elapsed = current;
        }
        return elapsed;
    }
    private TimeSpan ObserveClock()
    {
        var current = clock();
        if (current > observedClock) observedClock = current;
        return observedClock;
    }
    private double UpdatePercent()
    {
        if (Total == 0) return 0;
        var active = currentId.Length == 0 ? 0 : byId[currentId].Progress / 100;
        percent = Math.Max(percent, Math.Clamp(100d * (FinishedCount() + active) / Total, 0, 100));
        return percent;
    }
    private static TimeSpan PositiveDifference(TimeSpan end, TimeSpan start) => end >= start ? BoundedTime((double)end.Ticks - start.Ticks) : TimeSpan.Zero;
    private static TimeSpan BoundedTime(double ticks) => ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)Math.Max(0, ticks));
}