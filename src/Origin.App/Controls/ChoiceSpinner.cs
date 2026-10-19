using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Origin.Controls
{
    /// <summary>An option and the text shown for it.</summary>
    public sealed class Choice
    {
        public Choice(string label, object value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public object Value { get; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Media Center style "‹ value ›" selector: Left/Right change the value, Up/Down move on.
    /// Unlike a ComboBox it never traps the arrow keys, which matters on a remote.
    /// </summary>
    [TemplatePart(Name = "PART_Previous", Type = typeof(UIElement))]
    [TemplatePart(Name = "PART_Next", Type = typeof(UIElement))]
    public class ChoiceSpinner : Control
    {
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IList), typeof(ChoiceSpinner), new PropertyMetadata(null, (d, e) => ((ChoiceSpinner)d).UpdateText()));

        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
            nameof(SelectedItem), typeof(object), typeof(ChoiceSpinner),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ChoiceSpinner)d).OnSelectedItemChanged()));

        public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(
            nameof(DisplayText), typeof(string), typeof(ChoiceSpinner), new PropertyMetadata(string.Empty));

        static ChoiceSpinner()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ChoiceSpinner), new FrameworkPropertyMetadata(typeof(ChoiceSpinner)));
            FocusableProperty.OverrideMetadata(typeof(ChoiceSpinner), new FrameworkPropertyMetadata(true));
        }

        public event System.EventHandler SelectedItemChanged;

        public IList ItemsSource
        {
            get => (IList)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public string DisplayText
        {
            get => (string)GetValue(DisplayTextProperty);
            private set => SetValue(DisplayTextProperty, value);
        }

        public void Step(int delta)
        {
            var items = ItemsSource;
            if (items == null || items.Count == 0)
            {
                return;
            }

            var index = items.IndexOf(SelectedItem);
            index = index < 0 ? 0 : (index + delta + items.Count) % items.Count;
            SelectedItem = items[index];
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (GetTemplateChild("PART_Previous") is UIElement previous)
            {
                previous.MouseLeftButtonUp += (s, e) => { Focus(); Step(-1); e.Handled = true; };
            }

            if (GetTemplateChild("PART_Next") is UIElement next)
            {
                next.MouseLeftButtonUp += (s, e) => { Focus(); Step(1); e.Handled = true; };
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                Step(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.Enter)
            {
                Step(1);
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            base.OnMouseLeftButtonDown(e);
        }

        private void OnSelectedItemChanged()
        {
            UpdateText();
            SelectedItemChanged?.Invoke(this, System.EventArgs.Empty);
        }

        private void UpdateText()
        {
            DisplayText = SelectedItem?.ToString() ?? string.Empty;
        }
    }
}
