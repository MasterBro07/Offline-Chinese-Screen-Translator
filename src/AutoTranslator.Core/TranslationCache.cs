using System.Text.Json;

namespace AutoTranslator.Core;

/// <summary>Thread-safe LRU cache for translations with optional JSON file persistence.</summary>
public sealed class TranslationCache
{
    private sealed record Entry(string Key, string Source, string Target, string Text, string Translation);

    private sealed class FileEntry
    {
        public string? S { get; set; }
        public string? T { get; set; }
        public string? X { get; set; }
        public string? V { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private readonly int _capacity;
    private readonly string? _filePath;
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new(); // first = most recently used

    public TranslationCache(int capacity = 20000, string? filePath = null)
    {
        _capacity = Math.Max(1, capacity);
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public bool TryGet(string source, string target, string text, out string translation)
    {
        var key = MakeKey(source, target, text);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                translation = node.Value.Translation;
                return true;
            }
        }
        translation = string.Empty;
        return false;
    }

    public void Set(string source, string target, string text, string translation)
    {
        var key = MakeKey(source, target, text);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            _map[key] = _order.AddFirst(new Entry(key, source, target, text, translation));
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>Reads the cache file, if any. No-op without a file path; never throws.</summary>
    public void Load()
    {
        if (_filePath is null) return;
        try
        {
            List<FileEntry>? entries;
            lock (_fileLock)
            {
                if (!File.Exists(_filePath)) return;
                using var stream = File.OpenRead(_filePath);
                entries = JsonSerializer.Deserialize<List<FileEntry>>(stream, JsonOptions);
            }
            if (entries is null) return;
            // The file is ordered oldest first, so replaying it restores the LRU order.
            foreach (var e in entries)
            {
                if (e?.S is null || e.T is null || e.X is null || e.V is null) continue;
                Set(e.S, e.T, e.X, e.V);
            }
        }
        catch
        {
            // A missing or corrupt cache file only costs re-translation.
        }
    }

    /// <summary>Writes the cache file atomically. No-op without a file path; never throws.</summary>
    public void Save()
    {
        if (_filePath is null) return;
        try
        {
            List<FileEntry> entries;
            lock (_lock)
            {
                entries = new List<FileEntry>(_map.Count);
                for (var node = _order.Last; node is not null; node = node.Previous)
                {
                    var e = node.Value;
                    entries.Add(new FileEntry { S = e.Source, T = e.Target, X = e.Text, V = e.Translation });
                }
            }
            lock (_fileLock)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = _filePath + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, entries, JsonOptions);
                }
                File.Move(temp, _filePath, overwrite: true);
            }
        }
        catch
        {
            // Persistence is best effort.
        }
    }

    private static string MakeKey(string source, string target, string text) =>
        string.Concat(source, "\u001f", target, "\u001f", text);
}
