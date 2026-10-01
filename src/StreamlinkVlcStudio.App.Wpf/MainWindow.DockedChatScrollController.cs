using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private sealed class DockedChatScrollController : IDisposable
    {
        private readonly MainWindow window;
        private bool disposed;
        internal DockedChatScrollController(MainWindow window) => this.window = window;

        internal void Attach()
        {
            ((INotifyCollectionChanged)DockedChatListBox.Items).CollectionChanged += DockedChatItemsOnCollectionChanged;
            DockedChatListBox.Loaded += OnLoaded;
            DockedChatPanel.IsVisibleChanged += OnPanelVisibilityChanged;
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureDockedChatScrollViewer();
            QueueDockedChatScrollToBottom(force: true);
        }
        private void OnPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!DockedChatPanel.IsVisible) return;
            LockDockedChatToBottom();
            QueueDockedChatScrollToBottom(force: true);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ((INotifyCollectionChanged)DockedChatListBox.Items).CollectionChanged -= DockedChatItemsOnCollectionChanged;
            DockedChatListBox.Loaded -= OnLoaded;
            DockedChatPanel.IsVisibleChanged -= OnPanelVisibilityChanged;
            if (dockedChatScrollViewer is not null) dockedChatScrollViewer.ScrollChanged -= DockedChatScrollViewerOnScrollChanged;
            dockedChatScrollViewer = null;
            dockedChatAnchorItem = null;
        }
        private MainViewModel? viewModel => window.viewModel;
        private FrameworkElement DockedChatPanel => window.DockedChatPanel;
        private ListBox DockedChatListBox => window.DockedChatListBox;
        private Dispatcher Dispatcher { get => window.Dispatcher; }
        internal ScrollViewer? dockedChatScrollViewer;
        internal bool dockedChatScrollPending;
        internal bool dockedChatForceScrollPending;
        internal bool dockedChatShouldFollowBottom = true;
        internal bool dockedChatManualScrollOverride;
        internal bool dockedChatScrollThumbDragging;
        internal bool dockedChatAnchorRestorePending;
        internal object? dockedChatAnchorItem;
        internal double dockedChatAnchorTop;

        internal void DockedChatPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0)
            {
                return;
            }

            if (!IsPointOverElement(DockedChatListBox, e.GetPosition(DockedChatListBox)))
            {
                return;
            }

            ScrollDockedChat(e.Delta);
            e.Handled = true;
        }

        internal void DockedChatResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (viewModel is not null)
            {
                var currentWidth = viewModel.Settings.Chat.DockWidth;
                viewModel.Settings.Chat.DockWidth = ChatSettings.NormalizeDockWidth(currentWidth - e.HorizontalChange);
            }

            e.Handled = true;
        }

        internal void ChatListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            DockedChatPanel_PreviewMouseWheel(sender, e);
        }

        internal void DockedChatScrollThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            dockedChatScrollThumbDragging = true;
            dockedChatForceScrollPending = false;
            dockedChatManualScrollOverride = true;
            dockedChatShouldFollowBottom = false;
            CaptureDockedChatScrollAnchor();
        }

        internal void DockedChatScrollThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            dockedChatScrollThumbDragging = false;
            EnsureDockedChatScrollViewer();
            var scrollViewer = dockedChatScrollViewer;
            if (scrollViewer is null)
            {
                return;
            }

            UpdateDockedChatManualScrollState(scrollViewer, scrollViewer.VerticalOffset);
            if (dockedChatShouldFollowBottom)
            {
                QueueDockedChatScrollToBottom(force: true);
            }
        }

        internal void EnsureDockedChatScrollViewer()
        {
            if (disposed) return;
            var scrollViewer = FindVisualChild<ScrollViewer>(DockedChatListBox);
            if (scrollViewer is null || ReferenceEquals(scrollViewer, dockedChatScrollViewer))
            {
                return;
            }

            if (dockedChatScrollViewer is not null)
            {
                dockedChatScrollViewer.ScrollChanged -= DockedChatScrollViewerOnScrollChanged;
            }

            dockedChatScrollViewer = scrollViewer;
            dockedChatScrollViewer.ScrollChanged += DockedChatScrollViewerOnScrollChanged;
        }

        internal void DockedChatScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            if (dockedChatManualScrollOverride || dockedChatScrollThumbDragging)
            {
                UpdateDockedChatManualScrollState(scrollViewer, scrollViewer.VerticalOffset);
                return;
            }

            dockedChatShouldFollowBottom = true;
            dockedChatAnchorItem = null;
            if (!IsDockedChatAtBottom(scrollViewer, scrollViewer.VerticalOffset))
            {
                QueueDockedChatScrollToBottom(force: true);
            }
        }

        internal void DockedChatItemsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!DockedChatPanel.IsVisible) return;
            if (e.Action is NotifyCollectionChangedAction.Add or
                NotifyCollectionChangedAction.Remove or
                NotifyCollectionChangedAction.Reset or
                NotifyCollectionChangedAction.Replace)
            {
                if (dockedChatManualScrollOverride || !dockedChatShouldFollowBottom)
                {
                    QueueDockedChatAnchorRestore();
                    return;
                }

                QueueDockedChatScrollToBottom(force: false);
            }
        }

        internal void LockDockedChatToBottom()
        {
            dockedChatManualScrollOverride = false;
            dockedChatShouldFollowBottom = true;
            dockedChatAnchorItem = null;
        }

        internal void UpdateDockedChatManualScrollState(ScrollViewer scrollViewer, double verticalOffset)
        {
            if (IsDockedChatAtBottom(scrollViewer, verticalOffset) && !dockedChatScrollThumbDragging)
            {
                LockDockedChatToBottom();
                return;
            }

            dockedChatManualScrollOverride = true;
            dockedChatShouldFollowBottom = false;
            if (IsDockedChatAtBottom(scrollViewer, verticalOffset))
            {
                dockedChatAnchorItem = null;
            }
            else
            {
                CaptureDockedChatScrollAnchor();
            }
        }

        internal void QueueDockedChatScrollToBottom(bool force)
        {
            if (disposed || !DockedChatPanel.IsVisible) return;
            dockedChatForceScrollPending |= force;
            if (dockedChatScrollPending)
            {
                return;
            }

            dockedChatScrollPending = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (disposed) return;
                var shouldScroll = dockedChatForceScrollPending || dockedChatShouldFollowBottom;
                dockedChatScrollPending = false;
                dockedChatForceScrollPending = false;

                if (shouldScroll && DockedChatPanel.IsVisible)
                {
                    ScrollDockedChatToBottom();
                }
            }));
        }

        internal void QueueDockedChatAnchorRestore()
        {
            if (disposed || !DockedChatPanel.IsVisible) return;
            if (dockedChatAnchorItem is null && !CaptureDockedChatScrollAnchor())
            {
                return;
            }

            if (dockedChatAnchorRestorePending)
            {
                return;
            }

            dockedChatAnchorRestorePending = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (disposed) return;
                dockedChatAnchorRestorePending = false;
                RestoreDockedChatScrollAnchor();
            }));
        }

        internal bool CaptureDockedChatScrollAnchor()
        {
            if (!DockedChatPanel.IsVisible) return false;
            EnsureDockedChatScrollViewer();
            var scrollViewer = dockedChatScrollViewer;
            if (scrollViewer is null)
            {
                return false;
            }

            foreach (var item in DockedChatListBox.Items)
            {
                if (DockedChatListBox.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                    container.RenderSize.Height <= 0)
                {
                    continue;
                }

                var top = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
                var bottom = top + container.RenderSize.Height;
                if (bottom <= 0 || top >= scrollViewer.ViewportHeight)
                {
                    continue;
                }

                dockedChatAnchorItem = item;
                dockedChatAnchorTop = top;
                return true;
            }

            dockedChatAnchorItem = null;
            return false;
        }

        internal void RestoreDockedChatScrollAnchor()
        {
            if (!DockedChatPanel.IsVisible) return;
            EnsureDockedChatScrollViewer();
            var scrollViewer = dockedChatScrollViewer;
            if (scrollViewer is null || dockedChatAnchorItem is null)
            {
                return;
            }

            DockedChatListBox.UpdateLayout();
            if (DockedChatListBox.ItemContainerGenerator.ContainerFromItem(dockedChatAnchorItem) is not FrameworkElement container)
            {
                dockedChatAnchorItem = null;
                return;
            }

            var currentTop = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
            var targetOffset = Math.Clamp(
                scrollViewer.VerticalOffset + currentTop - dockedChatAnchorTop,
                0,
                scrollViewer.ScrollableHeight);

            if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) > double.Epsilon)
            {
                scrollViewer.ScrollToVerticalOffset(targetOffset);
            }

            dockedChatShouldFollowBottom = IsDockedChatAtBottom(scrollViewer, targetOffset);
            if (dockedChatShouldFollowBottom)
            {
                LockDockedChatToBottom();
            }
            else
            {
                dockedChatManualScrollOverride = true;
                DockedChatListBox.UpdateLayout();
                CaptureDockedChatScrollAnchor();
            }
        }

        internal void ScrollDockedChat(int delta)
        {
            EnsureDockedChatScrollViewer();
            var scrollViewer = dockedChatScrollViewer;
            if (scrollViewer is null)
            {
                return;
            }

            var notches = delta / (double)Mouse.MouseWheelDeltaForOneLine;
            var targetOffset = Math.Clamp(
                scrollViewer.VerticalOffset - notches * ChatPixelsPerWheelNotch,
                0,
                scrollViewer.ScrollableHeight);

            if (IsDockedChatAtBottom(scrollViewer, targetOffset) && !dockedChatScrollThumbDragging)
            {
                LockDockedChatToBottom();
            }
            else
            {
                dockedChatForceScrollPending = false;
                dockedChatManualScrollOverride = true;
                dockedChatShouldFollowBottom = false;
            }

            scrollViewer.ScrollToVerticalOffset(targetOffset);
            UpdateDockedChatManualScrollState(scrollViewer, targetOffset);
        }

        internal void ScrollDockedChatToBottom()
        {
            if (!DockedChatListBox.IsLoaded || !DockedChatPanel.IsVisible)
            {
                return;
            }

            DockedChatListBox.UpdateLayout();
            EnsureDockedChatScrollViewer();
            var scrollViewer = dockedChatScrollViewer;
            if (scrollViewer is null)
            {
                return;
            }

            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight);
            LockDockedChatToBottom();
        }

        internal static bool IsDockedChatAtBottom(ScrollViewer scrollViewer, double verticalOffset)
        {
            return scrollViewer.ScrollableHeight - verticalOffset <= ChatBottomFollowTolerance;
        }
    }
}
