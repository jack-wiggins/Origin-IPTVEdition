using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Origin.Input;

namespace Origin.Navigation
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>What a page can ask of the main window.</summary>
    public interface IShell
    {
        AppServices Services { get; }

        bool CanGoBack { get; }

        void Navigate(OriginPage page);

        /// <summary>Navigates, removing the current page from the back stack (e.g. Guide → Live TV → Guide).</summary>
        void Replace(OriginPage page);

        void GoBack();

        void GoHome();

        void ShowToast(string message, ToastKind kind = ToastKind.Info);

        void PlayChannel(string channelKey);

        void ToggleFullScreen();

        void Minimize();

        void Exit();
    }

    /// <summary>A full screen of the 10-foot interface (Start, Guide, Settings ...).</summary>
    public abstract class OriginPage : UserControl
    {
        protected OriginPage(IShell shell)
        {
            Shell = shell;
            Focusable = false;
        }

        protected IShell Shell { get; }

        protected AppServices Services => Shell.Services;

        /// <summary>Shown in the top-left corner, Media Center style ("guide", "settings").</summary>
        public virtual string Title => string.Empty;

        /// <summary>Full-window pages (video) skip the scaled interface layer and the animated background.</summary>
        public virtual bool IsFullWindow => false;

        public virtual void OnNavigatedTo(bool isBack)
        {
        }

        public virtual void OnNavigatedFrom()
        {
        }

        /// <summary>Called once when the page leaves the back stack for good.</summary>
        public virtual void OnRemoved()
        {
        }

        /// <summary>Puts keyboard focus where the user would expect it.</summary>
        public virtual void FocusDefault()
        {
        }

        /// <summary>Return true if the page handled the command.</summary>
        public virtual bool HandleCommand(RemoteCommand command) => false;

        /// <summary>
        /// Lists keep the arrow keys to themselves. On settings pages, Left/Right (and Up at the
        /// top, Down at the bottom) should carry on to the neighbouring control instead.
        /// </summary>
        protected static bool LeaveListInDirection(RemoteCommand command)
        {
            if (!(Keyboard.FocusedElement is ListBoxItem item) || !(ItemsControl.ItemsControlFromItemContainer(item) is ListBox list))
            {
                return false;
            }

            var index = list.ItemContainerGenerator.IndexFromContainer(item);
            FocusNavigationDirection direction;
            switch (command)
            {
                case RemoteCommand.Left:
                    direction = FocusNavigationDirection.Left;
                    break;
                case RemoteCommand.Right:
                    direction = FocusNavigationDirection.Right;
                    break;
                case RemoteCommand.Up when index <= 0:
                    direction = FocusNavigationDirection.Up;
                    break;
                case RemoteCommand.Down when index >= list.Items.Count - 1:
                    direction = FocusNavigationDirection.Down;
                    break;
                default:
                    return false;
            }

            if (list.PredictFocus(direction) is UIElement target && !IsInside(target, list))
            {
                target.Focus();
                return true;
            }

            return false;
        }

        /// <summary>Focuses the selected item of a list (or its first item).</summary>
        protected static void FocusList(ListBox list)
        {
            if (list.Items.Count == 0)
            {
                list.Focus();
                return;
            }

            var index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
            list.ScrollIntoView(list.Items[index]);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            {
                item.Focus();
            }
        }

        private static bool IsInside(DependencyObject element, DependencyObject ancestor)
        {
            for (var current = element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current == ancestor)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
