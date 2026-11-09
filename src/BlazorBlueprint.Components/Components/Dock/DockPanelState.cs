namespace BlazorBlueprint.Components;

/// <summary>
/// The visibility state of a <see cref="BbDockPanel"/> within its <see cref="BbDock"/>.
/// </summary>
public enum DockPanelState
{
    /// <summary>
    /// The panel is part of the layout, shown as a tab in a docked group or a floating window.
    /// </summary>
    Docked,

    /// <summary>
    /// The panel is collapsed to a tab in the auto-hide strip along an edge of the dock. Opening
    /// the tab slides the panel out over the layout; it hides again when focus moves elsewhere.
    /// </summary>
    AutoHidden,

    /// <summary>
    /// The panel is not shown. It can be brought back with <see cref="BbDock.OpenPanelAsync(string)"/>
    /// or <see cref="BbDock.SetPanelStateAsync(string, DockPanelState)"/>.
    /// </summary>
    Closed
}
