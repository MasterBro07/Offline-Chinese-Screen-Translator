using System.Globalization;
using System.Text.Json;

namespace AutoTranslator.Core;

/// <summary>
/// Thread-safe counter of the source characters sent to a paid engine today, persisted as
/// a small JSON file (<c>{"date":"2026-01-31","chars":1234}</c>). Resets when the local date changes.
/// </summary>
public sealed class UsageTracker
{
    private sealed class FileState
    {
        public string? Date { get; set; }
        public long Chars { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private readonly string? _filePath;
    private readonly Func<DateTime> _clock;
    private long _dailyCharLimit;
    private DateOnly _date;
    private long _chars;
    private long _lastSaveTicks = long.MinValue;

    public UsageTracker(string? filePath = null, long dailyCharLimit = 0)
        : this(filePath, dailyCharLimit, () => DateTime.Now)
    {
    }

    /// <summary>For tests: <paramref name="localClock"/> supplies the current local time.</summary>
    public UsageTracker(string? filePath, long dailyCharLimit, Func<DateTime> localClock)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _dailyCharLimit = Math.Max(0, dailyCharLimit);
        _clock = localClock ?? throw new ArgumentNullException(nameof(localClock));
        _date = Today();
        Load();
    }

    /// <summary>Characters allowed per local day; 0 = unlimited.</summary>
    public long DailyCharLimit
    {
        get { lock (_lock) return _dailyCharLimit; }
        set
        {
            lock (_lock)
            {
                var limit = Math.Max(0, value);
                if (limit == _dailyCharLimit) return;
                _dailyCharLimit = limit;
            }
            RaiseChanged();
        }
    }

    /// <summary>Characters consumed today; resets when the local date changes.</summary>
    public long CharsToday
    {
        get
        {
            lock (_lock)
            {
                Rollover();
                return _chars;
            }
        }
    }

    /// <summary>
    /// Books <paramref name="chars"/> characters. Returns false, consuming nothing, when that would
    /// exceed the daily limit. The file is written at most every few seconds; <see cref="Save"/> flushes.
    /// </summary>
    public bool TryConsume(long chars)
    {
        if (chars <= 0) return true;
        bool save;
        lock (_lock)
        {
            var rolled = Rollover();
            if (_dailyCharLimit > 0 && (_chars >= _dailyCharLimit || chars > _dailyCharLimit - _chars)) return false;
            _chars = chars > long.MaxValue - _chars ? long.MaxValue : _chars + chars;
            save = _filePath is not null
                   && (rolled || _lastSaveTicks == long.MinValue
                       || Environment.TickCount64 - _lastSaveTicks >= (long)AutoSaveInterval.TotalMilliseconds);
        }
        if (save) Save();
        RaiseChanged();
        return true;
    }

    /// <summary>Gives back characters booked for a request that failed without being served.</summary>
    public void Refund(long chars)
    {
        if (chars <= 0) return;
        lock (_lock)
        {
            // A refund arriving after midnight belongs to yesterday's counter.
            if (Rollover()) return;
            _chars = Math.Max(0, _chars - chars);
        }
        RaiseChanged();
    }

    public event EventHandler? Changed;

    /// <summary>Writes the counter file atomically. No-op without a file path; never throws.</summary>
    public void Save()
    {
        if (_filePath is null) return;
        try
        {
            lock (_fileLock)
            {
                FileState state;
                lock (_lock)
                {
                    Rollover();
                    state = new FileState { Date = Format(_date), Chars = _chars };
                    _lastSaveTicks = Environment.TickCount64;
                }
                var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temp, _filePath, overwrite: true);
            }
        }
        catch
        {
            // Persistence is best effort; the in-memory counter keeps working.
        }
    }

    private void Load()
    {
        if (_filePath is null) return;
        try
        {
            if (!File.Exists(_filePath)) return;
            var state = JsonSerializer.Deserialize<FileState>(File.ReadAllText(_filePath), JsonOptions);
            if (state?.Date is null || state.Chars < 0) return;
            if (!DateOnly.TryParseExact(state.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return;
            if (date == _date) _chars = state.Chars;
        }
        catch
        {
            // A missing or corrupt file just starts the day at zero.
        }
    }

    // Callers hold _lock. Returns true when a new day started.
    private bool Rollover()
    {
        var today = Today();
        if (today == _date) return false;
        _date = today;
        _chars = 0;
        return true;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock());

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* listener errors are not ours */ }
    }
}
