namespace BlazorBlueprint.Components;

/// <summary>
/// Describes a change to a dock panel's <see cref="DockPanelState"/>.
/// </summary>
public sealed class DockPanelStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DockPanelStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="panelId">The identifier of the panel whose state changed.</param>
    /// <param name="oldState">The state the panel was in before the change.</param>
    /// <param name="newState">The state the panel is in now.</param>
    public DockPanelStateChangedEventArgs(string panelId, DockPanelState oldState, DockPanelState newState)
    {
        PanelId = panelId;
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>The identifier of the panel whose state changed.</summary>
    public string PanelId { get; }

    /// <summary>The state the panel was in before the change.</summary>
    public DockPanelState OldState { get; }

    /// <summary>The state the panel is in now.</summary>
    public DockPanelState NewState { get; }
}
