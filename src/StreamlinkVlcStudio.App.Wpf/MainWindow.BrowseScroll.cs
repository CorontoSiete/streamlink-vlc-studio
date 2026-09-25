using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private MainViewModel? browseScrollViewModel;
    private bool browseScrollCategoriesVisible;
    private BrowseCategoryViewModel? browseScrollStreamCategory;
    private double browseCategoryVerticalOffset;
    private DispatcherOperation? pendingBrowseScroll;

    private void SetBrowseScrollViewModel(MainViewModel? model)
    {
        if (browseScrollViewModel is not null)
        {
            browseScrollViewModel.PropertyChanged -= BrowseScrollOnPropertyChanged;
            browseScrollViewModel.BrowseCategories.CollectionChanged -= BrowseScrollCategoriesChanged;
        }

        pendingBrowseScroll?.Abort();
        pendingBrowseScroll = null;
        browseScrollViewModel = model;
        browseScrollCategoriesVisible = false;
        browseScrollStreamCategory = null;
        browseCategoryVerticalOffset = 0;
        if (model is not null)
        {
            model.PropertyChanged += BrowseScrollOnPropertyChanged;
            model.BrowseCategories.CollectionChanged += BrowseScrollCategoriesChanged;
            UpdateBrowseScrollPage();
        }
    }

    private void BrowseScrollOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsBrowseCategoriesPageVisible) or
            nameof(MainViewModel.IsBrowseStreamsPageVisible) or nameof(MainViewModel.SelectedBrowseCategory))
        {
            UpdateBrowseScrollPage();
        }
    }

    private void UpdateBrowseScrollPage()
    {
        var categoriesVisible = browseScrollViewModel?.IsBrowseCategoriesPageVisible == true;
        var streamCategory = browseScrollViewModel?.IsBrowseStreamsPageVisible == true
            ? browseScrollViewModel.SelectedBrowseCategory
            : null;
        if (categoriesVisible == browseScrollCategoriesVisible &&
            ReferenceEquals(streamCategory, browseScrollStreamCategory))
        {
            return;
        }

        // Capture before layout replaces the category grid. If a restore has not run yet,
        // the shared viewer still belongs to the previous page, so keep the saved offset.
        if (browseScrollCategoriesVisible && pendingBrowseScroll is null)
        {
            browseCategoryVerticalOffset = HomeContentScrollViewer.VerticalOffset;
        }

        browseScrollCategoriesVisible = categoriesVisible;
        browseScrollStreamCategory = streamCategory;
        ClearHomeAutoScroll();
        QueueBrowseScrollPosition();
    }

    private void BrowseScrollCategoriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        // A new platform or query replaces the category list. Refreshing retained cards,
        // appending a page, and updating viewer counts leave the saved position intact.
        browseCategoryVerticalOffset = 0;
        if (browseScrollCategoriesVisible)
        {
            QueueBrowseScrollPosition();
        }
    }

    private void QueueBrowseScrollPosition()
    {
        pendingBrowseScroll?.Abort();
        pendingBrowseScroll = null;
        if (!browseScrollCategoriesVisible && browseScrollStreamCategory is null)
        {
            return;
        }

        // Run after the navigation notifications, before the destination's first render.
        // Waiting for idle lets an inherited scroll position reach the screen first.
        pendingBrowseScroll = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            try
            {
                // Visibility bindings and layout must settle before WPF clamps the offset
                // to the destination's extent. Suppress pagination at the inherited offset.
                HomeContentScrollViewer.UpdateLayout();
                HomeContentScrollViewer.ScrollToVerticalOffset(
                    browseScrollCategoriesVisible ? browseCategoryVerticalOffset : 0);
                HomeContentScrollViewer.UpdateLayout();
            }
            finally
            {
                pendingBrowseScroll = null;
            }

            TryLoadMoreBrowseCategories();
        }));
    }
}
