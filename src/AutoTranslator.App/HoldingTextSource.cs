using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Wraps a text source and reports "unchanged" while <c>hold</c> returns true, so that the
/// pipeline keeps the current overlay. Used while the tray menu is open: the menu belongs to
/// this process, which the capture code otherwise treats as "nothing to translate".
/// </summary>
internal sealed class HoldingTextSource : ITextSource, IRefreshableTextSource
{
    private static readonly Task<IReadOnlyList<TextRegion>?> Unchanged =
        Task.FromResult<IReadOnlyList<TextRegion>?>(null);

    private readonly ITextSource _inner;
    private readonly Func<bool> _hold;

    public HoldingTextSource(ITextSource inner, Func<bool> hold)
    {
        _inner = inner;
        _hold = hold;
    }

    public string Name
    {
        get { return _inner.Name; }
    }

    public string? Warning
    {
        get { return (_inner as IRefreshableTextSource)?.Warning; }
    }

    public void Invalidate()
    {
        (_inner as IRefreshableTextSource)?.Invalidate();
    }

    public Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
    {
        bool hold;
        try
        {
            hold = _hold();
        }
        catch
        {
            hold = false;
        }

        return hold ? Unchanged : _inner.CaptureAsync(ct);
    }
}
