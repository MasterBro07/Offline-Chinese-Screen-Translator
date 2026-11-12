using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Reads text from the foreground window through UI Automation (exact text, no OCR errors,
/// but only for programs that expose their controls).
/// </summary>
internal sealed class UiAutomationTextSource : ITextSource, IRefreshableTextSource
{
    private const int MaxElements = 3000;
    private const int MaxRegions = 400;
    private const int MaxTextLength = 500;
    private const int TimeBudgetMs = 400;

    private static readonly IReadOnlyList<TextRegion> Empty = Array.Empty<TextRegion>();
    private static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromSeconds(10);

    // Control types whose Name is the text drawn inside their bounding rectangle.
    // Containers (Window, Pane, Group, Document, ...) are skipped on purpose, and so is Edit:
    // the Name of an edit box is its label, not the text shown inside the box.
    private static readonly HashSet<int> TextControlTypeIds = new HashSet<int>
    {
        ControlType.Text.Id,
        ControlType.Button.Id,
        ControlType.MenuItem.Id,
        ControlType.TabItem.Id,
        ControlType.ListItem.Id,
        ControlType.TreeItem.Id,
        ControlType.Hyperlink.Id,
        ControlType.CheckBox.Id,
        ControlType.RadioButton.Id,
        ControlType.HeaderItem.Id,
        ControlType.DataItem.Id,
    };

    private IReadOnlyList<TextRegion>? _last;
    private DateTime _lastFullResultUtc = DateTime.MinValue;
    private volatile bool _invalidated;
    private int _busy;

    public string Name
    {
        get { return "UI-Automation"; }
    }

    public string? Warning
    {
        get { return null; }
    }

    public void Invalidate()
    {
        _invalidated = true;
    }

    public async Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return null;
        }

        try
        {
            // Thread-pool threads are MTA, which is what UI Automation clients should use.
            IReadOnlyList<TextRegion> regions = await Task.Run(() => Collect(ct), ct).ConfigureAwait(false);

            bool forced = _invalidated || DateTime.UtcNow - _lastFullResultUtc > ForcedRefreshInterval;
            _invalidated = false;

            IReadOnlyList<TextRegion>? previous = _last;
            _last = regions;
            if (!forced && previous != null && previous.SequenceEqual(regions))
            {
                return null;
            }

            _lastFullResultUtc = DateTime.UtcNow;
            return regions;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static IReadOnlyList<TextRegion> Collect(CancellationToken ct)
    {
        IntPtr hwnd;
        ScreenRect windowBounds;
        if (!ScreenCapture.TryGetForegroundWindow(out hwnd, out windowBounds))
        {
            return Empty;
        }

        var found = new List<TextRegion>();
        try
        {
            Walk(hwnd, windowBounds, found, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ElementNotAvailableException, COMException, timeouts: windows come and go while
            // we are walking them. Use whatever was collected so far.
            if (!(ex is ElementNotAvailableException))
            {
                Log.Error("UI Automation walk failed", ex);
            }
        }

        return Finish(found);
    }

    private static void Walk(IntPtr hwnd, ScreenRect clip, List<TextRegion> found, CancellationToken ct)
    {
        var request = new CacheRequest();
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.AutomationElementMode = AutomationElementMode.Full;
        request.TreeScope = TreeScope.Element;
        request.TreeFilter = Automation.ControlViewCondition;

        AutomationElement root = AutomationElement.FromHandle(hwnd).GetUpdatedCache(request);
        TreeWalker walker = TreeWalker.ControlViewWalker;
        Stopwatch watch = Stopwatch.StartNew();

        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        int visited = 0;
        bool isRoot = true;

        while (stack.Count > 0
               && visited < MaxElements
               && found.Count < MaxRegions
               && watch.ElapsedMilliseconds < TimeBudgetMs)
        {
            ct.ThrowIfCancellationRequested();
            AutomationElement element = stack.Pop();
            visited++;

            bool offscreen = false;
            try
            {
                offscreen = element.Cached.IsOffscreen;
                if (!offscreen)
                {
                    Examine(element, clip, found);
                }
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                // Property was not cached for this element.
            }

            // Children of an off-screen element (hidden tab page, scrolled-out list) are skipped.
            if (offscreen && !isRoot)
            {
                continue;
            }

            isRoot = false;

            try
            {
                AutomationElement? child = walker.GetFirstChild(element, request);
                while (child != null)
                {
                    stack.Push(child);
                    if (stack.Count > MaxElements || watch.ElapsedMilliseconds >= TimeBudgetMs)
                    {
                        break;
                    }

                    child = walker.GetNextSibling(child, request);
                }
            }
            catch (ElementNotAvailableException)
            {
                // The element disappeared; go on with the rest of the tree.
            }
        }
    }

    private static void Examine(AutomationElement element, ScreenRect clip, List<TextRegion> found)
    {
        string? name = element.Cached.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxTextLength || !ScriptDetector.ContainsHan(name))
        {
            return;
        }

        ControlType? type = element.Cached.ControlType;
        if (type == null || !TextControlTypeIds.Contains(type.Id))
        {
            return;
        }

        System.Windows.Rect rect = element.Cached.BoundingRectangle;
        if (rect.IsEmpty
            || double.IsNaN(rect.X) || double.IsNaN(rect.Y)
            || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y)
            || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
            || rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        var bounds = new ScreenRect(
            (int)Math.Floor(rect.X),
            (int)Math.Floor(rect.Y),
            (int)Math.Ceiling(rect.Width),
            (int)Math.Ceiling(rect.Height));

        ScreenRect? visible = ScreenCapture.Intersect(bounds, clip);
        if (visible == null || visible.Value.Width < 2 || visible.Value.Height < 2)
        {
            return;
        }

        found.Add(new TextRegion(name.Trim(), visible.Value));
    }

    /// <summary>Removes duplicates and containers that repeat the text of an element inside them.</summary>
    private static IReadOnlyList<TextRegion> Finish(List<TextRegion> found)
    {
        if (found.Count == 0)
        {
            return Empty;
        }

        List<TextRegion> distinct = found.Distinct().ToList();
        var result = new List<TextRegion>(distinct.Count);

        for (int i = 0; i < distinct.Count; i++)
        {
            TextRegion candidate = distinct[i];
            bool repeatsInnerElement = false;

            for (int j = 0; j < distinct.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                TextRegion other = distinct[j];
                if (other.Text == candidate.Text && Contains(candidate.Bounds, other.Bounds))
                {
                    // E.g. a Button named "确定" that contains a Text element "确定": keep the inner one.
                    repeatsInnerElement = true;
                    break;
                }
            }

            if (!repeatsInnerElement)
            {
                result.Add(candidate);
            }
        }

        // Stable order so that "nothing changed" can be detected by comparing lists.
        result.Sort((a, b) =>
        {
            int byY = a.Bounds.Y.CompareTo(b.Bounds.Y);
            if (byY != 0)
            {
                return byY;
            }

            int byX = a.Bounds.X.CompareTo(b.Bounds.X);
            if (byX != 0)
            {
                return byX;
            }

            return string.CompareOrdinal(a.Text, b.Text);
        });

        return result;
    }

    /// <summary>True when <paramref name="outer"/> fully contains <paramref name="inner"/> (and they differ).</summary>
    private static bool Contains(ScreenRect outer, ScreenRect inner)
    {
        if (outer == inner)
        {
            return false;
        }

        return inner.X >= outer.X
               && inner.Y >= outer.Y
               && inner.X + inner.Width <= outer.X + outer.Width
               && inner.Y + inner.Height <= outer.Y + outer.Height;
    }
}
