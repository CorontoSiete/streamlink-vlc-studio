using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private enum LibraryScrollPage { Following, Broadcasts, Recent, Categories, Streams }

    private MainViewModel? libraryScrollViewModel;
    private LibraryScrollPage? libraryScrollPage;
    private BrowseCategoryViewModel? libraryScrollStreamCategory;
    private readonly Dictionary<LibraryScrollPage, double> libraryScrollOffsets = [];
    private DispatcherOperation? pendingHomeScroll;

    private void SetLibraryScrollViewModel(MainViewModel? model)
    {
        if (libraryScrollViewModel is not null)
        {
            libraryScrollViewModel.PropertyChanged -= LibraryScrollOnPropertyChanged;
            libraryScrollViewModel.BrowseCategories.CollectionChanged -= LibraryScrollCollectionChanged;
            libraryScrollViewModel.TwitchVods.CollectionChanged -= LibraryScrollCollectionChanged;
            libraryScrollViewModel.RecentStreams.CollectionChanged -= LibraryScrollCollectionChanged;
            libraryScrollViewModel.LiveFollowedChannels.CollectionChanged -= LibraryScrollCollectionChanged;
        }

        pendingHomeScroll?.Abort();
        pendingHomeScroll = null;
        libraryScrollViewModel = model;
        libraryScrollPage = null;
        libraryScrollStreamCategory = null;
        libraryScrollOffsets.Clear();
        if (model is not null)
        {
            model.PropertyChanged += LibraryScrollOnPropertyChanged;
            model.BrowseCategories.CollectionChanged += LibraryScrollCollectionChanged;
            model.TwitchVods.CollectionChanged += LibraryScrollCollectionChanged;
            model.RecentStreams.CollectionChanged += LibraryScrollCollectionChanged;
            model.LiveFollowedChannels.CollectionChanged += LibraryScrollCollectionChanged;
            UpdateLibraryScrollPage();
        }
    }

    private void LibraryScrollOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsFollowedHomePageVisible) or
            nameof(MainViewModel.IsTwitchVodsHomePageVisible) or nameof(MainViewModel.IsRecentHomePageVisible) or
            nameof(MainViewModel.IsBrowseCategoriesPageVisible) or
            nameof(MainViewModel.IsBrowseStreamsPageVisible) or nameof(MainViewModel.SelectedBrowseCategory))
        {
            UpdateLibraryScrollPage();
        }
    }

    private void UpdateLibraryScrollPage()
    {
        if (libraryScrollViewModel is not { } model) return;
        var page = model.IsBrowseStreamsPageVisible ? LibraryScrollPage.Streams
            : model.IsBrowseCategoriesPageVisible ? LibraryScrollPage.Categories
            : model.IsTwitchVodsHomePageVisible ? LibraryScrollPage.Broadcasts
            : model.IsRecentHomePageVisible ? LibraryScrollPage.Recent
            : LibraryScrollPage.Following;
        var streamCategory = page == LibraryScrollPage.Streams ? model.SelectedBrowseCategory : null;
        if (page == libraryScrollPage && ReferenceEquals(streamCategory, libraryScrollStreamCategory))
        {
            return;
        }

        // Navigation raises several notifications before layout. Only the first one can
        // capture the outgoing page; subsequent notifications describe intermediate states.
        if (libraryScrollPage is { } previousPage && pendingHomeScroll is null)
        {
            libraryScrollOffsets[previousPage] = HomeContentScrollViewer.VerticalOffset;
        }

        libraryScrollPage = page;
        libraryScrollStreamCategory = streamCategory;
        ClearHomeAutoScroll();
        QueueLibraryScrollPosition();
    }

    private void LibraryScrollCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset || libraryScrollViewModel is not { } model)
        {
            return;
        }

        // A replacement query/platform starts at the top. Refreshing retained cards and
        // appending pages do not reset the user's position.
        var page = ReferenceEquals(sender, model.BrowseCategories) ? LibraryScrollPage.Categories
            : ReferenceEquals(sender, model.TwitchVods) ? LibraryScrollPage.Broadcasts
            : ReferenceEquals(sender, model.RecentStreams) ? LibraryScrollPage.Recent
            : LibraryScrollPage.Following;
        libraryScrollOffsets[page] = 0;
        if (libraryScrollPage == page)
        {
            QueueLibraryScrollPosition();
        }
    }

    private void QueueLibraryScrollPosition()
    {
        pendingHomeScroll?.Abort();
        pendingHomeScroll = null;
        if (libraryScrollPage is null)
        {
            return;
        }

        // Run after the navigation notifications, before the destination's first render.
        // Waiting for idle lets an inherited scroll position reach the screen first.
        pendingHomeScroll = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            try
            {
                // Visibility bindings and layout must settle before WPF clamps the offset
                // to the destination's extent. Suppress pagination at the inherited offset.
                HomeContentScrollViewer.UpdateLayout();
                HomeContentScrollViewer.ScrollToVerticalOffset(libraryScrollPage is { } page && page != LibraryScrollPage.Streams
                    ? libraryScrollOffsets.GetValueOrDefault(page) : 0);
                HomeContentScrollViewer.UpdateLayout();
            }
            finally
            {
                pendingHomeScroll = null;
            }

            TryLoadMoreBrowseCategories();
        }));
    }
}
