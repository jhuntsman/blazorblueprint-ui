using Microsoft.AspNetCore.Components;

namespace BlazorBlueprint.Components;

/// <summary>
/// Declares a panel that can be docked, tabbed, floated and closed inside a <see cref="BbDock"/>.
/// The panel renders no markup itself; its <see cref="ChildContent"/> is hoisted into the
/// active tab group by the parent dock.
/// </summary>
public partial class BbDockPanel : ComponentBase, IDisposable
{
    [CascadingParameter]
    private BbDock? Dock { get; set; }

    /// <summary>
    /// A stable, unique identifier for the panel. Required so the dock can track the panel
    /// across layout changes (docking, floating, closing and reopening).
    /// </summary>
    [Parameter, EditorRequired]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The title shown on the panel's tab.
    /// </summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// An optional icon rendered before the title on the tab.
    /// </summary>
    [Parameter]
    public RenderFragment? Icon { get; set; }

    /// <summary>
    /// The region the panel is placed in when the dock first builds its layout.
    /// Panels sharing a region are grouped into the same tab group.
    /// </summary>
    [Parameter]
    public DockZone Region { get; set; } = DockZone.Center;

    /// <summary>
    /// Controls the relative ordering of panels when the initial layout is built.
    /// Lower values appear first within a region.
    /// </summary>
    [Parameter]
    public int Order { get; set; }

    /// <summary>
    /// Whether the panel can be closed by the user. Defaults to <c>true</c>.
    /// </summary>
    [Parameter]
    public bool Closable { get; set; } = true;

    /// <summary>
    /// Whether the panel can be detached into a floating window. Defaults to <c>true</c>.
    /// Ignored when <see cref="Locked"/> is <c>true</c>.
    /// </summary>
    [Parameter]
    public bool CanFloat { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the panel is locked in place: it cannot be dragged to move, reorder or
    /// re-dock, detached into a floating window, or closed. Useful for a mandatory region such as
    /// a toolbox. Overrides <see cref="Closable"/> and <see cref="CanFloat"/>. Defaults to <c>false</c>.
    /// </summary>
    [Parameter]
    public bool Locked { get; set; }

    /// <summary>
    /// The state the panel starts in when it registers with the dock. <see cref="DockPanelState.AutoHidden"/>
    /// collapses it to the auto-hide strip on its <see cref="Region"/> edge (<see cref="DockZone.Center"/>
    /// uses the left edge); <see cref="DockPanelState.Closed"/> leaves it out until it is reopened.
    /// Read once, like <see cref="Region"/>: after that the user owns the layout, and the state is
    /// changed with <see cref="BbDock.SetPanelStateAsync(string, DockPanelState)"/>.
    /// Defaults to <see cref="DockPanelState.Docked"/>.
    /// </summary>
    [Parameter]
    public DockPanelState InitialState { get; set; } = DockPanelState.Docked;

    /// <summary>
    /// Whether the user can auto-hide the panel to an edge strip. Defaults to <c>true</c>.
    /// Ignored when <see cref="Locked"/> is <c>true</c>.
    /// </summary>
    [Parameter]
    public bool CanAutoHide { get; set; } = true;

    /// <summary>
    /// The size in pixels the panel's flyout opens at while auto-hidden: its width on the left and
    /// right edges, its height on the top and bottom edges. <c>null</c> (the default) uses 280px
    /// for the sides and 200px for the top and bottom. The flyout can still be resized by the user.
    /// </summary>
    [Parameter]
    public int? AutoHideSize { get; set; }

    /// <summary>
    /// The minimum width of the panel in pixels. Enforced while resizing a floating window and
    /// applied as a CSS constraint when docked. <c>null</c> (the default) means no minimum.
    /// </summary>
    [Parameter]
    public int? MinWidth { get; set; }

    /// <summary>
    /// The minimum height of the panel in pixels. Enforced while resizing a floating window and
    /// applied as a CSS constraint when docked. <c>null</c> (the default) means no minimum.
    /// </summary>
    [Parameter]
    public int? MinHeight { get; set; }

    /// <summary>
    /// The maximum width of the panel in pixels. Enforced while resizing a floating window and
    /// applied as a CSS constraint when docked. <c>null</c> (the default) means no maximum.
    /// </summary>
    [Parameter]
    public int? MaxWidth { get; set; }

    /// <summary>
    /// The maximum height of the panel in pixels. Enforced while resizing a floating window and
    /// applied as a CSS constraint when docked. <c>null</c> (the default) means no maximum.
    /// </summary>
    [Parameter]
    public int? MaxHeight { get; set; }

    /// <summary>
    /// The content rendered inside the panel when it is the active tab.
    /// </summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Whether the user may close this panel. <c>false</c> when locked.</summary>
    internal bool CanClose => Closable && !Locked;

    /// <summary>Whether the user may detach this panel into a floating window. <c>false</c> when locked.</summary>
    internal bool CanDetach => CanFloat && !Locked;

    /// <summary>Whether the user may drag this panel to move, reorder or re-dock it. <c>false</c> when locked.</summary>
    internal bool CanMove => !Locked;

    /// <summary>Whether the user may auto-hide this panel to an edge strip. <c>false</c> when locked.</summary>
    internal bool CanUserAutoHide => CanAutoHide && !Locked;

    private bool registered;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Dock is null)
        {
            throw new InvalidOperationException($"{nameof(BbDockPanel)} must be placed inside a {nameof(BbDock)}.");
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException($"{nameof(BbDockPanel)} requires a non-empty {nameof(Id)}.");
        }

        Dock.RegisterPanel(this);
        registered = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (registered)
        {
            Dock?.UnregisterPanel(this);
            registered = false;
        }
    }
}
