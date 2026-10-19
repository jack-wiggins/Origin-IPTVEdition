using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>
    /// The start menu, after Windows Media Center: categories stacked vertically, the focused
    /// category's items in a row to its right. Up/Down glide between categories, Left/Right
    /// between items.
    /// </summary>
    public partial class StartPage : OriginPage
    {
        private const double RowHeight = 172;
        private const double TitleColumnWidth = 520;
        private const double TileWidth = 252;
        private const double TileSpacing = 24;

        private static int _savedCategory = -1;
        private static int _savedItem;

        private readonly List<MenuCategory> _categories;
        private readonly List<CategoryRow> _rows = new List<CategoryRow>();
        private int _category;
        private int _item;

        public StartPage(IShell shell) : base(shell)
        {
            InitializeComponent();
            Focusable = true;
            FocusVisualStyle = null;

            _categories = BuildMenu();
            foreach (var category in _categories)
            {
                _rows.Add(BuildRow(category, _rows.Count));
            }

            if (_savedCategory >= 0)
            {
                _category = _savedCategory;
                _item = _savedItem;
            }
            else if (!Services.Settings.Current.Xtream.IsConfigured)
            {
                _category = 1; // settings › iptv provider
            }

            MenuCanvas.SizeChanged += (s, e) => Layout(animate: false);
            MouseWheel += (s, e) => MoveCategory(e.Delta > 0 ? -1 : 1);
            Services.Lineups.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        public override void OnNavigatedTo(bool isBack)
        {
            UpdateStatus();
            Layout(animate: false);
        }

        public override void FocusDefault()
        {
            Keyboard.Focus(this);
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            switch (command)
            {
                case RemoteCommand.Up:
                case RemoteCommand.ChannelUp:
                    MoveCategory(-1);
                    return true;
                case RemoteCommand.Down:
                case RemoteCommand.ChannelDown:
                    MoveCategory(1);
                    return true;
                case RemoteCommand.Left:
                    MoveItem(-1);
                    return true;
                case RemoteCommand.Right:
                    MoveItem(1);
                    return true;
                case RemoteCommand.Ok:
                    Invoke(_category, _item);
                    return true;
                case RemoteCommand.Back:
                    return true; // Start is the root; Back does nothing here.
                default:
                    return false;
            }
        }

        private List<MenuCategory> BuildMenu()
        {
            return new List<MenuCategory>
            {
                new MenuCategory("tv",
                    new MenuEntry("live tv", "Icon.Tv", () => Shell.PlayChannel(Services.Settings.Current.Playback.LastChannelKey)),
                    new MenuEntry("guide", "Icon.Guide", () => Shell.Navigate(new GuidePage(Shell)))),
                new MenuCategory("settings",
                    new MenuEntry("iptv provider", "Icon.Server", () => Shell.Navigate(new ProviderSetupPage(Shell))),
                    new MenuEntry("schedules direct", "Icon.Calendar", () => Shell.Navigate(new SchedulesDirectPage(Shell))),
                    new MenuEntry("channel mapping", "Icon.Mapping", () => Shell.Navigate(new ChannelMappingPage(Shell))),
                    new MenuEntry("channel groups", "Icon.List", () => Shell.Navigate(new ChannelGroupsPage(Shell)))),
                new MenuCategory("tasks",
                    new MenuEntry("update lineup", "Icon.Refresh", () => Shell.Navigate(new LineupUpdatePage(Shell, new LineupUpdateOptions()))),
                    new MenuEntry("minimize", "Icon.Minimize", () => Shell.Minimize()),
                    new MenuEntry("close origin", "Icon.Power", () => Shell.Exit())),
            };
        }

        private void MoveCategory(int delta)
        {
            var next = Math.Max(0, Math.Min(_categories.Count - 1, _category + delta));
            if (next == _category)
            {
                return;
            }

            _category = next;
            _item = 0;
            Layout(animate: true);
        }

        private void MoveItem(int delta)
        {
            var count = _categories[_category].Entries.Count;
            var next = Math.Max(0, Math.Min(count - 1, _item + delta));
            if (next == _item)
            {
                return;
            }

            _item = next;
            Layout(animate: true);
        }

        private void Invoke(int category, int item)
        {
            _savedCategory = category;
            _savedItem = item;
            _categories[category].Entries[item].Invoke();
        }

        private void Layout(bool animate)
        {
            var available = MenuCanvas.ActualHeight;
            if (available <= 0)
            {
                return;
            }

            RowsPanel.Width = MenuCanvas.ActualWidth;

            // The focused row sits a little above centre, as in Media Center.
            var anchor = (available - RowHeight) / 2 - RowHeight * 0.12;
            var offset = anchor - _category * RowHeight;
            Apply(RowsOffset, TranslateTransform.YProperty, offset, animate, 480);

            var itemsWidth = Math.Max(0, MenuCanvas.ActualWidth - TitleColumnWidth - 90);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var focused = i == _category;
                var distance = Math.Abs(i - _category);

                Apply(row.Title, OpacityProperty, focused ? 1 : distance == 1 ? 0.45 : 0.2, animate, 380);
                Apply(row.TitleScale, ScaleTransform.ScaleXProperty, focused ? 1 : 0.7, animate, 420);
                Apply(row.TitleScale, ScaleTransform.ScaleYProperty, focused ? 1 : 0.7, animate, 420);
                Apply(row.Items, OpacityProperty, focused ? 1 : 0, animate, focused ? 420 : 200);
                row.Items.IsHitTestVisible = focused;

                // Keep the focused tile on screen when a category has more items than fit.
                var scroll = 0.0;
                if (focused)
                {
                    var right = (_item + 1) * (TileWidth + TileSpacing);
                    scroll = Math.Min(0, itemsWidth - right);
                }

                Apply(row.ItemsOffset, TranslateTransform.XProperty, focused ? scroll : 48, animate, 460);

                for (var j = 0; j < row.Tiles.Count; j++)
                {
                    var tile = row.Tiles[j];
                    var selected = focused && j == _item;
                    Apply(tile.Highlight, OpacityProperty, selected ? 1 : 0, animate, selected ? 180 : 240);
                    Apply(tile.Scale, ScaleTransform.ScaleXProperty, selected ? 1.12 : 1, animate, 320);
                    Apply(tile.Scale, ScaleTransform.ScaleYProperty, selected ? 1.12 : 1, animate, 320);
                    Apply(tile.Label, OpacityProperty, selected ? 1 : 0.78, animate, 240);
                    tile.Highlight.Effect = selected
                        ? new DropShadowEffect { Color = (Color)FindResource("Color.Glow"), BlurRadius = 34, ShadowDepth = 0, Opacity = 0.85 }
                        : null;
                    Panel.SetZIndex(tile.Root, selected ? 1 : 0);
                }
            }
        }

        private static void Apply(IAnimatable target, DependencyProperty property, double value, bool animate, int milliseconds)
        {
            if (animate)
            {
                Anim.To(target, property, value, milliseconds);
            }
            else
            {
                Anim.Set(target, property, value);
            }
        }

        private CategoryRow BuildRow(MenuCategory category, int index)
        {
            var grid = new Grid { Height = RowHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TitleColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var titleScale = new ScaleTransform();
            var title = new TextBlock
            {
                Text = category.Title,
                FontFamily = (FontFamily)FindResource("Font.Light"),
                FontSize = 76,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 46, 10),
                RenderTransformOrigin = new Point(1, 0.5),
                RenderTransform = titleScale,
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 1, Opacity = 0.45 },
            };
            title.MouseLeftButtonUp += (s, e) =>
            {
                if (_category != index)
                {
                    _category = index;
                    _item = 0;
                    Layout(animate: true);
                }
            };
            grid.Children.Add(title);

            var itemsOffset = new TranslateTransform();
            var items = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = itemsOffset,
            };
            Grid.SetColumn(items, 1);
            grid.Children.Add(items);

            var row = new CategoryRow(grid, title, titleScale, items, itemsOffset);
            for (var j = 0; j < category.Entries.Count; j++)
            {
                var tile = BuildTile(category.Entries[j], index, j);
                row.Tiles.Add(tile);
                items.Children.Add(tile.Root);
            }

            RowsPanel.Children.Add(grid);
            return row;
        }

        private Tile BuildTile(MenuEntry entry, int categoryIndex, int itemIndex)
        {
            var scale = new ScaleTransform();
            var root = new Grid
            {
                Width = TileWidth,
                Height = 150,
                Margin = new Thickness(0, 0, TileSpacing, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };

            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x04, 0x18, 0x38)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x81, 0xC8, 0xFF)),
                BorderThickness = new Thickness(1),
            });

            var highlight = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("Brush.Focus"),
                BorderBrush = (Brush)FindResource("Brush.AccentBright"),
                BorderThickness = new Thickness(2),
                Opacity = 0,
                Child = new Border { CornerRadius = new CornerRadius(5), Background = (Brush)FindResource("Brush.FocusSheen") },
            };
            root.Children.Add(highlight);

            var label = new TextBlock
            {
                Text = entry.Title,
                FontSize = 25,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
            };

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Path
            {
                Data = (Geometry)FindResource(entry.IconKey),
                Fill = Brushes.White,
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
            });
            content.Children.Add(label);
            root.Children.Add(content);

            root.MouseEnter += (s, e) =>
            {
                if (MouseActivity.MovedRecently && (_category != categoryIndex || _item != itemIndex))
                {
                    _category = categoryIndex;
                    _item = itemIndex;
                    Layout(animate: true);
                }
            };
            root.MouseLeftButtonUp += (s, e) => Invoke(categoryIndex, itemIndex);

            return new Tile(root, highlight, scale, label);
        }

        private void UpdateStatus()
        {
            var settings = Services.Settings.Current;
            var lineup = Services.Lineups.Current;

            if (!settings.Xtream.IsConfigured)
            {
                StatusText.Text = "Welcome to Origin. Start with settings › iptv provider to add your Xtream Codes account.";
                return;
            }

            if (lineup.IsEmpty)
            {
                StatusText.Text = "Your IPTV provider is set up. Choose tasks › update lineup to bring in your channels.";
                return;
            }

            var text = lineup.Channels.Count.ToString("N0") + " channels";
            text += lineup.SchedulesDirectLineupId != null
                ? "   ·   " + lineup.MappedCount.ToString("N0") + " matched to Schedules Direct"
                : "   ·   Schedules Direct not set up";
            text += "   ·   updated " + lineup.UpdatedUtc.ToLocalTime().ToString("g");
            StatusText.Text = text;
        }

        private sealed class MenuCategory
        {
            public MenuCategory(string title, params MenuEntry[] entries)
            {
                Title = title;
                Entries = entries;
            }

            public string Title { get; }

            public IReadOnlyList<MenuEntry> Entries { get; }
        }

        private sealed class MenuEntry
        {
            public MenuEntry(string title, string iconKey, Action invoke)
            {
                Title = title;
                IconKey = iconKey;
                Invoke = invoke;
            }

            public string Title { get; }

            public string IconKey { get; }

            public Action Invoke { get; }
        }

        private sealed class CategoryRow
        {
            public CategoryRow(Grid root, TextBlock title, ScaleTransform titleScale, StackPanel items, TranslateTransform itemsOffset)
            {
                Root = root;
                Title = title;
                TitleScale = titleScale;
                Items = items;
                ItemsOffset = itemsOffset;
            }

            public Grid Root { get; }

            public TextBlock Title { get; }

            public ScaleTransform TitleScale { get; }

            public StackPanel Items { get; }

            public TranslateTransform ItemsOffset { get; }

            public List<Tile> Tiles { get; } = new List<Tile>();
        }

        private sealed class Tile
        {
            public Tile(Grid root, Border highlight, ScaleTransform scale, TextBlock label)
            {
                Root = root;
                Highlight = highlight;
                Scale = scale;
                Label = label;
            }

            public Grid Root { get; }

            public Border Highlight { get; }

            public ScaleTransform Scale { get; }

            public TextBlock Label { get; }
        }
    }
}
