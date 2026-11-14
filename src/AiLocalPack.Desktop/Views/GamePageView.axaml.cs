using System.ComponentModel;
using AiLocalPack.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AiLocalPack.Desktop.Views;

public sealed partial class GamePageView : UserControl
{
    /// <summary>Closer than this to the end counts as "at the newest message": new output keeps the reader there.</summary>
    private const double FollowSlack = 80;

    /// <summary>Farther than this from the end shows <c>Latest</c>.</summary>
    private const double LatestDistance = 240;

    private GamePageViewModel? _page;
    private bool _following;

    public GamePageView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
        // ScrollToEnd from inside ScrollChanged (opening a conversation at its newest message) moves the offset without a
        // ScrollChanged of its own, so the sticky bar follows the offset itself.
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty)
            {
                UpdateStuck();
            }
        };
        TabBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateStuck();
            }
        };
        ComposerDock.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
            {
                UpdateDock();
            }
        };
        ComposerLayer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateDock();
                FollowConversation();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = DataContext as GamePageViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            FollowConversation();
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamePageViewModel.IsGenerationTab))
        {
            FollowConversation();
        }
    }

    /// <summary>The sticky tab bar shows once the in-flow one has scrolled under the top edge.</summary>
    private void UpdateStuck()
    {
        if (DataContext is GamePageViewModel page)
        {
            page.IsTabBarStuck = Scroller.Offset.Y > 0 && Scroller.Offset.Y >= TabBar.Bounds.Top;
        }
    }

    private bool IsDocked => ComposerDock.IsEffectivelyVisible;

    private double DistanceToEnd => Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;

    /// <summary>
    /// The Create with AI thread reads like a chat: while the reader is at the newest message, new output (a stage line,
    /// the agent's steps, a new draft) keeps them there; scrolled up, they stay put and <c>Latest</c> shows instead.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateStuck();
        if (IsDocked && _following && e.ExtentDelta.Y > 0)
        {
            Scroller.ScrollToEnd();
        }
        else
        {
            _following = DistanceToEnd <= FollowSlack;
        }

        UpdateLatest();
    }

    /// <summary>Opening Create with AI on a conversation starts at its newest message.</summary>
    private void FollowConversation()
    {
        if (_page is { IsGenerationTab: true, Generation.ShowFix: true, Generation.Fix.HasItems: true })
        {
            _following = true;
            UpdateLatest();
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    /// <summary>The page leaves room for the docked composer under the thread; <c>Latest</c> sits just above it.</summary>
    private void UpdateDock()
    {
        var height = IsDocked ? ComposerDock.Bounds.Height : 0;
        if (Math.Abs(ComposerSpacer.Height - height) > 0.5)
        {
            ComposerSpacer.Height = height;
        }

        LatestButton.Margin = new Thickness(0, 0, 32, height + 12);
        UpdateLatest();
    }

    private void UpdateLatest() => LatestButton.IsVisible = IsDocked && !_following && DistanceToEnd > LatestDistance;

    private void OnLatestClick(object? sender, RoutedEventArgs e)
    {
        _following = true;
        UpdateLatest();
        Scroller.ScrollToEnd();
    }
}
