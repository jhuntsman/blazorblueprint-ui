using BlazorBlueprint.Components;
using BlazorBlueprint.Tests.Performance;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorBlueprint.Tests.Rendering;

/// <summary>
/// Panel state on <see cref="BbDock"/>: docked, auto-hidden to an edge strip, or closed.
/// </summary>
/// <remarks>
/// <para>
/// Most of what can go wrong here is bookkeeping rather than drawing: a panel that ends up in two
/// places, or in none, or comes back somewhere other than where it left. So the tests read the
/// state through the dock's own API and check the markup only for where the panel landed.
/// </para>
/// <para>
/// Dismissing the flyout on an outside click or Escape, and resizing it, are driven by the
/// JavaScript module and covered by <c>tests/js/dock-autohide.test.mjs</c> instead.
/// </para>
/// </remarks>
public class DockAutoHideTests
{
    [Fact]
    public async Task AnAutoHiddenStartPutsThePanelInItsRegionStripAndNotInTheLayout()
    {
        await RunAsync(
            (renderer, dock, _) =>
            {
                var markup = renderer.Markup();

                Assert.Equal(DockPanelState.AutoHidden, dock.GetPanelState("output"));
                Assert.Contains("data-dock-strip=\"bottom\"", markup, StringComparison.Ordinal);
                Assert.Contains("data-dock-autohide-tab=\"output\"", markup, StringComparison.Ordinal);
                Assert.DoesNotContain("data-dock-tab=\"output\"", markup, StringComparison.Ordinal);
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("output", DockZone.Bottom, DockPanelState.AutoHidden));
    }

    [Fact]
    public async Task ACentrePanelStartingAutoHiddenGoesToTheLeftStrip()
    {
        // The centre has no edge of its own. Left is where an IDE keeps its tool windows.
        await RunAsync(
            (renderer, _, _) =>
            {
                Assert.Contains("data-dock-strip=\"left\"", renderer.Markup(), StringComparison.Ordinal);
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("notes", DockZone.Center, DockPanelState.AutoHidden));
    }

    [Fact]
    public async Task AClosedStartLeavesThePanelOutUntilItIsOpened()
    {
        await RunAsync(
            async (renderer, dock, changes) =>
            {
                Assert.Equal(DockPanelState.Closed, dock.GetPanelState("search"));
                Assert.Contains("search", dock.GetClosedPanelIds());
                Assert.DoesNotContain("data-dock-tab=\"search\"", renderer.Markup(), StringComparison.Ordinal);

                await dock.OpenPanelAsync("search");

                Assert.Equal(DockPanelState.Docked, dock.GetPanelState("search"));
                Assert.Contains("data-dock-tab=\"search\"", renderer.Markup(), StringComparison.Ordinal);
                Assert.Equal([("search", DockPanelState.Closed, DockPanelState.Docked)], changes);
            },
            Panel("editor", DockZone.Center),
            Panel("search", DockZone.Left, DockPanelState.Closed));
    }

    [Fact]
    public async Task TheInitialStateIsNotReportedAsAChange()
    {
        // Nothing changed: the page asked for this layout. A View menu listening for changes
        // should not see one on load.
        await RunAsync(
            (_, _, changes) =>
            {
                Assert.Empty(changes);
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("output", DockZone.Bottom, DockPanelState.AutoHidden),
            Panel("search", DockZone.Left, DockPanelState.Closed));
    }

    [Fact]
    public async Task AutoHidingAGroupSendsItToTheNearestEdge()
    {
        await RunAsync(
            async (renderer, dock, changes) =>
            {
                await dock.AutoHideGroupAsync(GroupOf(dock, "explorer"));

                var markup = renderer.Markup();
                Assert.Contains("data-dock-strip=\"left\"", markup, StringComparison.Ordinal);
                Assert.DoesNotContain("data-dock-tab=\"explorer\"", markup, StringComparison.Ordinal);
                Assert.Equal([("explorer", DockPanelState.Docked, DockPanelState.AutoHidden)], changes);
            },
            Panel("editor", DockZone.Center),
            Panel("explorer", DockZone.Left),
            Panel("output", DockZone.Bottom));
    }

    [Fact]
    public async Task AGroupInTheMiddleOfTheRowFindsTheEdgeOfTheColumnItSitsIn()
    {
        // Explorer | (Editor / Output) | Properties: Output is neither first nor last in the row,
        // but it is last in its column, so it belongs to the bottom edge.
        await RunAsync(
            (_, dock, _) =>
            {
                Assert.Equal(DockZone.Bottom, dock.ResolveEdge(GroupOf(dock, "output")));
                Assert.Equal(DockZone.Right, dock.ResolveEdge(GroupOf(dock, "properties")));
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("explorer", DockZone.Left),
            Panel("output", DockZone.Bottom),
            Panel("properties", DockZone.Right));
    }

    [Fact]
    public async Task DockingReturnsAPanelToItsGroupAtItsOldTabPosition()
    {
        await RunAsync(
            async (_, dock, _) =>
            {
                await dock.SetPanelStateAsync("b", DockPanelState.AutoHidden);
                Assert.Equal(["a", "c"], GroupOf(dock, "a").PanelIds);

                await dock.SetPanelStateAsync("b", DockPanelState.Docked);

                var group = GroupOf(dock, "a");
                Assert.Equal(["a", "b", "c"], group.PanelIds);
                Assert.Equal("b", group.ActivePanelId);
            },
            Panel("a", DockZone.Center),
            Panel("b", DockZone.Center, order: 1),
            Panel("c", DockZone.Center, order: 2));
    }

    [Fact]
    public async Task PanelsHiddenTogetherComeBackTogetherInTheirOrder()
    {
        // Auto-hiding the whole group removes it from the layout. Docking one of its panels from
        // the flyout must not scatter the rest into groups of one.
        await RunAsync(
            async (_, dock, changes) =>
            {
                await dock.AutoHideGroupAsync(GroupOf(dock, "explorer"));
                Assert.Null(ComponentProbe.Call(dock, "FindGroupContaining", "explorer"));

                await dock.DockAutoHiddenGroupAsync("outline");

                var group = GroupOf(dock, "explorer");
                Assert.Equal(["explorer", "outline"], group.PanelIds);
                Assert.Equal("outline", group.ActivePanelId);
                Assert.Equal(DockPanelState.Docked, dock.GetPanelState("explorer"));
                Assert.Equal(4, changes.Count);
            },
            Panel("editor", DockZone.Center),
            Panel("explorer", DockZone.Left),
            Panel("outline", DockZone.Left, order: 1));
    }

    [Fact]
    public async Task APanelStartingAutoHiddenDocksIntoItsRegionsGroup()
    {
        await RunAsync(
            async (_, dock, _) =>
            {
                await dock.SetPanelStateAsync("problems", DockPanelState.Docked);

                Assert.Same(GroupOf(dock, "output"), GroupOf(dock, "problems"));
            },
            Panel("editor", DockZone.Center),
            Panel("output", DockZone.Bottom),
            Panel("problems", DockZone.Bottom, DockPanelState.AutoHidden, order: 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TheUserCannotAutoHideALockedPanelOrOneThatOptsOut(bool canAutoHide, bool locked)
    {
        await RunAsync(
            async (renderer, dock, _) =>
            {
                Assert.DoesNotContain("data-dock-autohide-group", renderer.Markup(), StringComparison.Ordinal);

                await dock.AutoHidePanelAsync("toolbox");
                Assert.Equal(DockPanelState.Docked, dock.GetPanelState("toolbox"));

                // The page itself is not bound by the user's permissions.
                await dock.SetPanelStateAsync("toolbox", DockPanelState.AutoHidden);
                Assert.Equal(DockPanelState.AutoHidden, dock.GetPanelState("toolbox"));
            },
            Panel("toolbox", DockZone.Left, extra: p =>
            {
                p[nameof(BbDockPanel.CanAutoHide)] = canAutoHide;
                p[nameof(BbDockPanel.Locked)] = locked;
            }));
    }

    [Fact]
    public async Task ClosingAnAutoHiddenPanelTakesItOutOfTheStrip()
    {
        var closed = new List<string>();

        await RunAsync(
            async (renderer, dock, changes) =>
            {
                await dock.ClosePanelAsync("output");

                Assert.Equal(DockPanelState.Closed, dock.GetPanelState("output"));
                Assert.DoesNotContain("data-dock-autohide-tab=\"output\"", renderer.Markup(), StringComparison.Ordinal);
                Assert.Equal(["output"], closed);
                Assert.Equal([("output", DockPanelState.AutoHidden, DockPanelState.Closed)], changes);
            },
            [Panel("editor", DockZone.Center), Panel("output", DockZone.Bottom, DockPanelState.AutoHidden)],
            p => p[nameof(BbDock.OnPanelClosed)] = EventCallback.Factory.Create<string>(closed, closed.Add));
    }

    [Fact]
    public async Task TheFlyoutOpensFromItsStripTabAndClosesWhenDismissed()
    {
        await RunAsync(
            (renderer, dock, _) =>
            {
                Assert.Contains("aria-expanded=\"false\"", renderer.Markup(), StringComparison.Ordinal);
                Assert.Null(ComponentProbe.Field<string?>(dock, "activeFlyoutPanelId"));

                dock.ToggleFlyout("output");

                var open = renderer.Markup();
                Assert.Contains("aria-expanded=\"true\"", open, StringComparison.Ordinal);
                Assert.Contains("Build output", open, StringComparison.Ordinal);
                Assert.Contains("aria-label=\"Output\"", open, StringComparison.Ordinal);

                dock.OnFlyoutDismissed("output");

                Assert.DoesNotContain("aria-expanded=\"true\"", renderer.Markup(), StringComparison.Ordinal);
                Assert.Equal(DockPanelState.AutoHidden, dock.GetPanelState("output"));
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("output", DockZone.Bottom, DockPanelState.AutoHidden, title: "Output", content: "Build output"));
    }

    [Fact]
    public async Task OpeningAnAutoHiddenPanelShowsItsFlyoutAndLeavesItAutoHidden()
    {
        await RunAsync(
            async (_, dock, changes) =>
            {
                await dock.OpenPanelAsync("output");

                Assert.Equal("output", ComponentProbe.Field<string?>(dock, "activeFlyoutPanelId"));
                Assert.Equal(DockPanelState.AutoHidden, dock.GetPanelState("output"));
                Assert.Empty(changes);
            },
            Panel("editor", DockZone.Center),
            Panel("output", DockZone.Bottom, DockPanelState.AutoHidden));
    }

    [Fact]
    public async Task TheFlyoutOpensAtItsAutoHideSizeAndAResizeIsHeldToItsMinimum()
    {
        await RunAsync(
            (renderer, dock, _) =>
            {
                dock.ToggleFlyout("explorer");
                Assert.Contains("width:320px;", renderer.Markup(), StringComparison.Ordinal);

                dock.OnFlyoutResized("explorer", 40);
                Assert.Contains("width:150px;", renderer.Markup(), StringComparison.Ordinal);
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center),
            Panel("explorer", DockZone.Left, DockPanelState.AutoHidden, extra: p =>
            {
                p[nameof(BbDockPanel.AutoHideSize)] = (int?)320;
                p[nameof(BbDockPanel.MinWidth)] = (int?)150;
            }));
    }

    [Fact]
    public async Task AskingForTheStateOfAnUnknownPanelIsAnError()
    {
        await RunAsync(
            (_, dock, _) =>
            {
                Assert.Throws<ArgumentException>(() => dock.GetPanelState("missing"));
                return Task.CompletedTask;
            },
            Panel("editor", DockZone.Center));
    }

    // -------------------------------------------------------------------------------------

    private sealed record PanelSpec(
        string Id,
        DockZone Region,
        DockPanelState State,
        int Order,
        string Title,
        string Content,
        Action<Dictionary<string, object?>>? Extra);

    private static PanelSpec Panel(
        string id,
        DockZone region,
        DockPanelState state = DockPanelState.Docked,
        int order = 0,
        string? title = null,
        string? content = null,
        Action<Dictionary<string, object?>>? extra = null) =>
        new(id, region, state, order, title ?? id, content ?? $"{id} content", extra);

    private static DockTabGroupNode GroupOf(BbDock dock, string panelId) =>
        (DockTabGroupNode)ComponentProbe.Call(dock, "FindGroupContaining", panelId)!;

    private static Task RunAsync(
        Func<ComponentTestRenderer, BbDock, List<(string, DockPanelState, DockPanelState)>, Task> body,
        params PanelSpec[] panels) =>
        RunAsync(body, panels, _ => { });

    private static async Task RunAsync(
        Func<ComponentTestRenderer, BbDock, List<(string, DockPanelState, DockPanelState)>, Task> body,
        PanelSpec[] panels,
        Action<Dictionary<string, object?>> configureDock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IJSRuntime, NoopJavaScript>();
        services.AddBlazorBlueprintComponents();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new ComponentTestRenderer(provider, NullLoggerFactory.Instance);

        var changes = new List<(string, DockPanelState, DockPanelState)>();
        var parameters = new Dictionary<string, object?>
        {
            [nameof(BbDock.ChildContent)] = (RenderFragment)(builder => BuildPanels(builder, panels)),
            [nameof(BbDock.OnPanelStateChanged)] = EventCallback.Factory.Create<DockPanelStateChangedEventArgs>(
                changes, e => changes.Add((e.PanelId, e.OldState, e.NewState))),
        };
        configureDock(parameters);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var dock = await renderer.MountAsync<BbDock>(parameters);

            // The layout is built after the first render, as it is in a browser.
            Assert.True(ComponentProbe.Field<bool>(dock, "layoutBuilt"));

            await body(renderer, dock, changes);
        });
    }

    private static void BuildPanels(RenderTreeBuilder builder, PanelSpec[] panels)
    {
        foreach (var spec in panels)
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(BbDockPanel.Id)] = spec.Id,
                [nameof(BbDockPanel.Title)] = spec.Title,
                [nameof(BbDockPanel.Region)] = spec.Region,
                [nameof(BbDockPanel.Order)] = spec.Order,
                [nameof(BbDockPanel.InitialState)] = spec.State,
            };
            spec.Extra?.Invoke(parameters);

            builder.OpenComponent<BbDockPanel>(0);
            builder.SetKey(spec.Id);

            var sequence = 1;
            foreach (var (name, value) in parameters)
            {
                builder.AddAttribute(sequence++, name, value);
            }

            var content = spec.Content;
            builder.AddAttribute(50, nameof(BbDockPanel.ChildContent), (RenderFragment)(inner => inner.AddContent(0, content)));
            builder.CloseComponent();
        }
    }
}
