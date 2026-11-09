using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace BlazorBlueprint.Components;

// Panel state (docked / auto-hidden / closed) and the auto-hide strips and flyout.
public partial class BbDock
{
    /// <summary>
    /// Invoked when a panel's <see cref="DockPanelState"/> changes, whether the user closed,
    /// auto-hid or docked it, or the change came from <see cref="SetPanelStateAsync(string, DockPanelState)"/>
    /// or <see cref="OpenPanelAsync(string)"/>. Not raised for a panel's <see cref="BbDockPanel.InitialState"/>.
    /// </summary>
    [Parameter]
    public EventCallback<DockPanelStateChangedEventArgs> OnPanelStateChanged { get; set; }

    private readonly List<DockAutoHideEntry> autoHidden = new();

    // The auto-hidden panel whose flyout is open, if any.
    private string? activeFlyoutPanelId;

    // The panel the JS dismiss listeners are currently attached for; differs from
    // activeFlyoutPanelId between a state change and the render that follows it.
    private string? jsFlyoutPanelId;

    // Set when the flyout was opened by the user or OpenPanelAsync, so focus moves into it.
    private bool flyoutFocusPending;

    private DockAutoHideEntry? ActiveFlyout =>
        activeFlyoutPanelId is null ? null : FindAutoHide(activeFlyoutPanelId);

    private string FlyoutDomId => $"bb-dock-flyout-{dockId}";

    // ----------------------------------------------------------------- Public API

    /// <summary>
    /// Gets the current state of a panel.
    /// </summary>
    /// <param name="panelId">The identifier of a registered panel.</param>
    /// <returns>Whether the panel is docked, auto-hidden or closed.</returns>
    /// <exception cref="ArgumentException">No panel with <paramref name="panelId"/> is registered.</exception>
    public DockPanelState GetPanelState(string panelId)
    {
        if (!panels.ContainsKey(panelId))
        {
            throw new ArgumentException($"No dock panel with Id '{panelId}' is registered.", nameof(panelId));
        }

        return StateOf(panelId);
    }

    /// <summary>
    /// Moves a panel to the given state: <see cref="DockPanelState.Docked"/> returns an auto-hidden
    /// panel to the group it came from (or reopens a closed one), <see cref="DockPanelState.AutoHidden"/>
    /// collapses it to an edge strip, and <see cref="DockPanelState.Closed"/> closes it.
    /// Unlike the user's own gestures, this is not restricted by <see cref="BbDockPanel.Closable"/>,
    /// <see cref="BbDockPanel.CanAutoHide"/> or <see cref="BbDockPanel.Locked"/>. Unknown ids and
    /// a state the panel is already in are ignored.
    /// </summary>
    /// <param name="panelId">The identifier of the panel.</param>
    /// <param name="state">The state to move the panel to.</param>
    public async Task SetPanelStateAsync(string panelId, DockPanelState state)
    {
        if (!panels.TryGetValue(panelId, out var panel))
        {
            return;
        }

        var oldState = StateOf(panelId);
        if (oldState == state)
        {
            return;
        }

        switch (state)
        {
            case DockPanelState.Docked:
                if (oldState == DockPanelState.AutoHidden)
                {
                    DockAutoHidden(panelId);
                }
                else
                {
                    closedPanels.Remove(panelId);
                    if (FindGroupContaining(panelId) is null)
                    {
                        AddPanelToDefaultLocation(panelId);
                    }
                }

                maximizedGroupId = null;
                break;

            case DockPanelState.AutoHidden:
                if (oldState == DockPanelState.Closed)
                {
                    closedPanels.Remove(panelId);
                    autoHidden.Add(EntryForRegion(panel));
                }
                else if (FindGroupContaining(panelId) is { } group)
                {
                    AutoHideFromGroup(group, [panelId]);
                }

                break;

            case DockPanelState.Closed:
                ClosePanelCore(panelId);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        await AfterMutateAsync();
        await RaiseStateChangedAsync(panelId, oldState, StateOf(panelId));
    }

    // ----------------------------------------------------------------- State

    private DockPanelState StateOf(string panelId)
    {
        if (closedPanels.Contains(panelId))
        {
            return DockPanelState.Closed;
        }

        return FindAutoHide(panelId) is null ? DockPanelState.Docked : DockPanelState.AutoHidden;
    }

    internal bool IsAutoHidden(string panelId) => FindAutoHide(panelId) is not null;

    private DockAutoHideEntry? FindAutoHide(string panelId) =>
        autoHidden.Find(e => e.PanelId == panelId);

    private async Task RaiseStateChangedAsync(string panelId, DockPanelState oldState, DockPanelState newState)
    {
        if (oldState != newState && OnPanelStateChanged.HasDelegate)
        {
            await OnPanelStateChanged.InvokeAsync(new DockPanelStateChangedEventArgs(panelId, oldState, newState));
        }
    }

    /// <summary>
    /// Applies a panel's <see cref="BbDockPanel.InitialState"/> the first time it registers.
    /// </summary>
    private void ApplyInitialState(BbDockPanel panel)
    {
        switch (panel.InitialState)
        {
            case DockPanelState.Closed:
                closedPanels.Add(panel.Id);
                break;

            case DockPanelState.AutoHidden when FindAutoHide(panel.Id) is null && !closedPanels.Contains(panel.Id):
                autoHidden.Add(EntryForRegion(panel));
                break;
        }
    }

    /// <summary>
    /// An entry for a panel that is auto-hidden without coming from a group: it goes to the strip
    /// for its region and, when docked, back to that region's group.
    /// </summary>
    private DockAutoHideEntry EntryForRegion(BbDockPanel panel) => new()
    {
        PanelId = panel.Id,
        Edge = EdgeForRegion(panel.Region),
        RestoreGroupId = RegionGroupKey(panel.Region),
        RestoreIndex = int.MaxValue
    };

    private static DockZone EdgeForRegion(DockZone region) =>
        region == DockZone.Center ? DockZone.Left : region;

    // A stand-in group id shared by panels auto-hidden from the same region. BuildInitialLayout
    // swaps it for the real region group's id; otherwise docking creates a group with this id,
    // so the region's other auto-hidden panels join it.
    private string RegionGroupKey(DockZone region) => $"{dockId}-autohide-{region}";

    /// <summary>
    /// Points initially auto-hidden panels at the group built for their region, and orders the
    /// strips by <see cref="BbDockPanel.Order"/> the same way tabs are ordered.
    /// </summary>
    private void BindInitialAutoHide(Dictionary<DockZone, DockTabGroupNode> regionGroups)
    {
        foreach (var entry in autoHidden)
        {
            if (panels.TryGetValue(entry.PanelId, out var panel)
                && entry.RestoreGroupId == RegionGroupKey(panel.Region)
                && regionGroups.TryGetValue(panel.Region, out var group))
            {
                entry.RestoreGroupId = group.Id;
            }
        }

        var ordered = autoHidden
            .OrderBy(e => panels.TryGetValue(e.PanelId, out var p) ? p.Order : 0)
            .ToList();
        autoHidden.Clear();
        autoHidden.AddRange(ordered);
    }

    // ----------------------------------------------------------------- Auto-hide / dock

    private bool IsFloatingGroup(DockTabGroupNode group) =>
        floatingWindows.Exists(w => ReferenceEquals(w.Group, group));

    internal bool CanAutoHideGroup(DockTabGroupNode group) =>
        !IsFloatingGroup(group) && group.PanelIds.Exists(id => GetPanel(id)?.CanUserAutoHide == true);

    /// <summary>
    /// Auto-hides every panel in a docked group that allows it (the group header's button).
    /// </summary>
    internal async Task AutoHideGroupAsync(DockTabGroupNode group)
    {
        if (!CanAutoHideGroup(group))
        {
            return;
        }

        var ids = group.PanelIds.Where(id => GetPanel(id)?.CanUserAutoHide == true).ToList();
        AutoHideFromGroup(group, ids);
        await AfterMutateAsync();

        foreach (var id in ids)
        {
            await RaiseStateChangedAsync(id, DockPanelState.Docked, DockPanelState.AutoHidden);
        }
    }

    /// <summary>
    /// Auto-hides a single docked panel (the tab context menu's item).
    /// </summary>
    internal async Task AutoHidePanelAsync(string panelId)
    {
        var group = FindGroupContaining(panelId);
        if (group is null || IsFloatingGroup(group) || GetPanel(panelId)?.CanUserAutoHide != true)
        {
            return;
        }

        AutoHideFromGroup(group, [panelId]);
        await AfterMutateAsync();
        await RaiseStateChangedAsync(panelId, DockPanelState.Docked, DockPanelState.AutoHidden);
    }

    /// <summary>
    /// Moves the given panels of a group to the auto-hide strip on the group's nearest edge,
    /// remembering the group and tab position so they can be docked back.
    /// </summary>
    private void AutoHideFromGroup(DockTabGroupNode group, IReadOnlyList<string> panelIds)
    {
        var ids = panelIds.Where(group.PanelIds.Contains).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        // Resolve the edge before removing anything: removal can collapse the splits it reads.
        var edge = ResolveEdge(group);
        foreach (var id in ids)
        {
            autoHidden.Add(new DockAutoHideEntry
            {
                PanelId = id,
                Edge = edge,
                RestoreGroupId = group.Id,
                RestoreIndex = group.PanelIds.IndexOf(id)
            });
        }

        foreach (var id in ids)
        {
            RemovePanelFromLayout(id);
        }
    }

    /// <summary>
    /// Finds the dock edge nearest a group by walking the layout from the root: the first split in
    /// which the group's branch is the first or last child decides it. A group that touches no
    /// edge (or floats) falls back to its panels' <see cref="BbDockPanel.Region"/>.
    /// </summary>
    internal DockZone ResolveEdge(DockTabGroupNode group)
    {
        var path = new List<DockNode>();
        if (layout is not null && TryBuildPath(layout, group, path))
        {
            for (var i = 0; i < path.Count - 1; i++)
            {
                if (path[i] is not DockSplitNode split)
                {
                    continue;
                }

                var index = split.Children.IndexOf(path[i + 1]);
                var horizontal = split.Orientation == DockOrientation.Horizontal;

                if (index == 0)
                {
                    return horizontal ? DockZone.Left : DockZone.Top;
                }

                if (index == split.Children.Count - 1)
                {
                    return horizontal ? DockZone.Right : DockZone.Bottom;
                }
            }
        }

        var region = group.PanelIds
            .Select(GetPanel)
            .FirstOrDefault(p => p is not null)?.Region ?? DockZone.Center;
        return EdgeForRegion(region);
    }

    private static bool TryBuildPath(DockNode current, DockNode target, List<DockNode> path)
    {
        path.Add(current);
        if (ReferenceEquals(current, target))
        {
            return true;
        }

        if (current is DockSplitNode split)
        {
            foreach (var child in split.Children)
            {
                if (TryBuildPath(child, target, path))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>
    /// Returns an auto-hidden panel to its group at its old tab position. When that group is gone
    /// (it was emptied by auto-hiding), a group with the same id is created at the root on the
    /// panel's edge, so panels hidden together come back together.
    /// </summary>
    private void DockAutoHidden(string panelId)
    {
        var entry = FindAutoHide(panelId);
        if (entry is null)
        {
            return;
        }

        RemoveAutoHide(panelId);

        var target = entry.RestoreGroupId is null ? null : FindGroup(entry.RestoreGroupId);
        if (target is not null)
        {
            target.PanelIds.Insert(Math.Clamp(entry.RestoreIndex, 0, target.PanelIds.Count), panelId);
            EnforcePinOrder(target);
            target.ActivePanelId = panelId;
            return;
        }

        var group = entry.RestoreGroupId is { } id
            ? new DockTabGroupNode { Id = id }
            : new DockTabGroupNode();
        group.PanelIds.Add(panelId);
        group.ActivePanelId = panelId;
        SplitAtRoot(group, entry.Edge);
    }

    /// <summary>
    /// Docks an auto-hidden panel together with the panels that were auto-hidden from the same
    /// group (the flyout's Dock button), keeping their tab order and activating the given panel.
    /// </summary>
    internal async Task DockAutoHiddenGroupAsync(string panelId)
    {
        var entry = FindAutoHide(panelId);
        if (entry is null)
        {
            return;
        }

        var batch = entry.RestoreGroupId is null
            ? new List<DockAutoHideEntry> { entry }
            : autoHidden
                .Where(e => e.RestoreGroupId == entry.RestoreGroupId && e.Edge == entry.Edge)
                .OrderBy(e => e.RestoreIndex)
                .ToList();

        foreach (var e in batch)
        {
            DockAutoHidden(e.PanelId);
        }

        if (FindGroupContaining(panelId) is { } group)
        {
            group.ActivePanelId = panelId;
        }

        maximizedGroupId = null;
        await AfterMutateAsync();

        foreach (var e in batch)
        {
            await RaiseStateChangedAsync(e.PanelId, DockPanelState.AutoHidden, DockPanelState.Docked);
        }
    }

    private void RemoveAutoHide(string panelId)
    {
        autoHidden.RemoveAll(e => e.PanelId == panelId);
        if (activeFlyoutPanelId == panelId)
        {
            activeFlyoutPanelId = null;
        }
    }

    /// <summary>
    /// Takes a panel out of the layout or its strip and marks it closed. Returns whether it was
    /// open before.
    /// </summary>
    private bool ClosePanelCore(string panelId)
    {
        RemovePanelFromLayout(panelId);
        RemoveAutoHide(panelId);
        pinnedPanels.Remove(panelId);
        return closedPanels.Add(panelId);
    }

    // ----------------------------------------------------------------- Flyout

    /// <summary>
    /// Opens the flyout for an auto-hidden panel, or closes it when it is already open.
    /// </summary>
    internal void ToggleFlyout(string panelId)
    {
        if (!IsAutoHidden(panelId))
        {
            return;
        }

        activeFlyoutPanelId = activeFlyoutPanelId == panelId ? null : panelId;
        flyoutFocusPending = activeFlyoutPanelId is not null;
        StateHasChanged();
    }

    private void ShowFlyout(string panelId)
    {
        activeFlyoutPanelId = panelId;
        flyoutFocusPending = true;
        StateHasChanged();
    }

    /// <summary>
    /// Invoked from JS when the user clicks outside the open flyout or presses Escape.
    /// </summary>
    /// <param name="panelId">The panel whose flyout was dismissed.</param>
    [JSInvokable]
    public void OnFlyoutDismissed(string panelId)
    {
        if (disposed)
        {
            return;
        }

        if (jsFlyoutPanelId == panelId)
        {
            // JS detached its listeners itself before calling back.
            jsFlyoutPanelId = null;
        }

        if (activeFlyoutPanelId == panelId)
        {
            activeFlyoutPanelId = null;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Invoked from JS when the user finishes resizing a flyout.
    /// </summary>
    /// <param name="panelId">The panel whose flyout was resized.</param>
    /// <param name="size">The new width (left/right edges) or height (top/bottom edges) in pixels.</param>
    [JSInvokable]
    public void OnFlyoutResized(string panelId, double size)
    {
        if (disposed)
        {
            return;
        }

        var entry = FindAutoHide(panelId);
        if (entry is null)
        {
            return;
        }

        var (min, max) = FlyoutBounds(entry);
        entry.Size = Math.Clamp(size, min, max);
        StateHasChanged();
    }

    internal async Task StartFlyoutResizeAsync(PointerEventArgs e)
    {
        var entry = ActiveFlyout;
        if (entry is null || jsModule is null || !jsInitialized || e.Button != 0)
        {
            return;
        }

        var (min, max) = FlyoutBounds(entry);
        var maxArg = double.IsPositiveInfinity(max) ? 0 : max;

        await jsModule.InvokeVoidAsync(
            "startFlyoutResize", dockId, entry.PanelId, EdgeName(entry.Edge), e.ClientX, e.ClientY, e.PointerId, min, maxArg);
    }

    /// <summary>
    /// Attaches or detaches the flyout's outside-click and Escape listeners to match the
    /// rendered state, and moves focus into a flyout the user just opened.
    /// </summary>
    private async Task SyncFlyoutJsAsync()
    {
        if (jsModule is null || !jsInitialized)
        {
            return;
        }

        if (jsFlyoutPanelId == activeFlyoutPanelId && !flyoutFocusPending)
        {
            return;
        }

        var focus = flyoutFocusPending;
        flyoutFocusPending = false;

        try
        {
            if (activeFlyoutPanelId is null)
            {
                await jsModule.InvokeVoidAsync("closeFlyout", dockId);
            }
            else
            {
                await jsModule.InvokeVoidAsync("openFlyout", dockId, activeFlyoutPanelId, focus);
            }

            jsFlyoutPanelId = activeFlyoutPanelId;
        }
        catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
        {
            // Expected during circuit disconnect.
        }
    }

    private (double Min, double Max) FlyoutBounds(DockAutoHideEntry entry)
    {
        var panel = GetPanel(entry.PanelId);
        var min = DockAutoHideEntry.MinSize;
        var max = double.PositiveInfinity;

        if (entry.IsSideEdge)
        {
            if (panel?.MinWidth is int minW)
            {
                min = Math.Max(min, minW);
            }

            if (panel?.MaxWidth is int maxW)
            {
                max = maxW;
            }
        }
        else
        {
            if (panel?.MinHeight is int minH)
            {
                min = Math.Max(min, minH);
            }

            if (panel?.MaxHeight is int maxH)
            {
                max = maxH;
            }
        }

        return (min, Math.Max(max, min));
    }

    private double FlyoutSize(DockAutoHideEntry entry)
    {
        var (min, max) = FlyoutBounds(entry);
        var fallback = entry.IsSideEdge ? DockAutoHideEntry.DefaultSideSize : DockAutoHideEntry.DefaultEndSize;
        return Math.Clamp(entry.Size ?? GetPanel(entry.PanelId)?.AutoHideSize ?? fallback, min, max);
    }

    private static string EdgeName(DockZone edge) => edge switch
    {
        DockZone.Right => "right",
        DockZone.Top => "top",
        DockZone.Bottom => "bottom",
        _ => "left"
    };

    // ----------------------------------------------------------------- Rendering helpers

    private bool HasStrip(DockZone edge) => autoHidden.Exists(e => e.Edge == edge);

    // Panels auto-hidden together are drawn as one cluster, separated from other clusters.
    private IEnumerable<IGrouping<string, DockAutoHideEntry>> StripClusters(DockZone edge) =>
        autoHidden.Where(e => e.Edge == edge).GroupBy(e => e.RestoreGroupId ?? e.PanelId);

    private static string StripClass(DockZone edge) => ClassNames.cn(
        "bb:flex bb:shrink-0 bb:gap-3 bb:overflow-hidden bb:border-border/60 bb:bg-muted/50 bb:text-xs",
        edge switch
        {
            DockZone.Left => "bb:w-7 bb:flex-col bb:border-e bb:py-1",
            DockZone.Right => "bb:w-7 bb:flex-col bb:border-s bb:py-1",
            DockZone.Top => "bb:h-7 bb:flex-row bb:border-b bb:px-1",
            _ => "bb:h-7 bb:flex-row bb:border-t bb:px-1"
        });

    private static string StripClusterClass(DockZone edge) =>
        edge is DockZone.Left or DockZone.Right
            ? "bb:flex bb:flex-col bb:items-center bb:gap-0.5"
            : "bb:flex bb:flex-row bb:items-center bb:gap-0.5";

    private string StripTabClass(DockAutoHideEntry entry) => ClassNames.cn(
        // A soft outline so each tab reads as its own control against the strip, firming up on
        // hover and while its flyout is open.
        "bb:flex bb:max-h-48 bb:max-w-48 bb:shrink-0 bb:cursor-default bb:items-center bb:gap-1.5 bb:rounded-sm bb:border bb:border-border/60 bb:bg-background/50 bb:text-muted-foreground bb:transition-colors bb:hover:border-border bb:hover:bg-background bb:hover:text-foreground bb:focus-visible:outline-none bb:focus-visible:ring-2 bb:focus-visible:ring-ring",
        // Side strips run their labels top-to-bottom, as Visual Studio does.
        entry.IsSideEdge ? "bb:[writing-mode:vertical-rl] bb:px-1 bb:py-2" : "bb:h-6 bb:px-2",
        activeFlyoutPanelId == entry.PanelId ? "bb:border-border bb:bg-background bb:text-foreground bb:shadow-sm" : null);

    private static string FlyoutClass(DockAutoHideEntry? entry) => ClassNames.cn(
        "bb:absolute bb:z-20 bb:flex bb:flex-col bb:overflow-hidden bb:bg-background bb:text-foreground bb:shadow-2xl bb:outline-none bb:ring-1 bb:ring-black/5 bb:animate-in bb:fade-in-0 bb:duration-150 bb:motion-reduce:animate-none",
        entry is null
            ? "bb:hidden"
            : entry.Edge switch
            {
                DockZone.Left => "bb:inset-y-0 bb:start-0 bb:border-e bb:slide-in-from-start-4",
                DockZone.Right => "bb:inset-y-0 bb:end-0 bb:border-s bb:slide-in-from-end-4",
                DockZone.Top => "bb:inset-x-0 bb:top-0 bb:border-b bb:slide-in-from-top-4",
                _ => "bb:inset-x-0 bb:bottom-0 bb:border-t bb:slide-in-from-bottom-4"
            });

    private string? FlyoutStyle(DockAutoHideEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var size = FlyoutSize(entry).ToString("F0", CultureInfo.InvariantCulture);
        return entry.IsSideEdge ? $"width:{size}px;max-width:100%;" : $"height:{size}px;max-height:100%;";
    }

    // The grip sits on the flyout's inner edge, facing the layout.
    private static string FlyoutGripClass(DockZone edge) => ClassNames.cn(
        "bb:absolute bb:z-10 bb:bg-transparent bb:transition-colors bb:hover:bg-primary/50 bb:active:bg-primary",
        edge switch
        {
            DockZone.Left => "bb:inset-y-0 bb:end-0 bb:w-1.5 bb:cursor-ew-resize",
            DockZone.Right => "bb:inset-y-0 bb:start-0 bb:w-1.5 bb:cursor-ew-resize",
            DockZone.Top => "bb:inset-x-0 bb:bottom-0 bb:h-1.5 bb:cursor-ns-resize",
            _ => "bb:inset-x-0 bb:top-0 bb:h-1.5 bb:cursor-ns-resize"
        });

    // Edge-specific icons: "close" for the button that collapses a group to its edge,
    // "open" for the flyout button that docks it back.
    internal static string AutoHideIcon(DockZone edge) => $"panel-{EdgeName(edge)}-close";

    private static string DockIcon(DockZone edge) => $"panel-{EdgeName(edge)}-open";

    private static string FlyoutButtonClass =>
        "bb:inline-flex bb:h-6 bb:w-6 bb:shrink-0 bb:items-center bb:justify-center bb:rounded-sm bb:text-muted-foreground bb:transition-colors bb:hover:bg-foreground/10 bb:hover:text-foreground";
}
