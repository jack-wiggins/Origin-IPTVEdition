using System;
using System.Windows;
using System.Windows.Controls;

namespace Origin.Controls
{
    /// <summary>
    /// Lays children out left to right by time: each child's <see cref="StartProperty"/> and
    /// <see cref="EndProperty"/> are fractions (0-1) of the panel's width, so a program block's
    /// width is proportional to its duration whatever the screen size. A child whose span is
    /// zero (such as the "now" line) keeps its own width.
    /// </summary>
    public sealed class TimelinePanel : Panel
    {
        public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
            "Start", typeof(double), typeof(TimelinePanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static readonly DependencyProperty EndProperty = DependencyProperty.RegisterAttached(
            "End", typeof(double), typeof(TimelinePanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static double GetStart(DependencyObject element) => (double)element.GetValue(StartProperty);

        public static void SetStart(DependencyObject element, double value) => element.SetValue(StartProperty, value);

        public static double GetEnd(DependencyObject element) => (double)element.GetValue(EndProperty);

        public static void SetEnd(DependencyObject element, double value) => element.SetValue(EndProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            var height = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                var span = Span(child, width);
                child.Measure(new Size(span > 0 ? span : availableSize.Width, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(width, double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in InternalChildren)
            {
                var x = Clamp(GetStart(child)) * finalSize.Width;
                var span = Span(child, finalSize.Width);
                var width = span > 0 ? span : child.DesiredSize.Width;
                child.Arrange(new Rect(span > 0 ? x : x - width / 2, 0, width, finalSize.Height));
            }

            return finalSize;
        }

        private static double Span(UIElement child, double width)
        {
            return Math.Max(0, Clamp(GetEnd(child)) - Clamp(GetStart(child))) * width;
        }

        private static double Clamp(double fraction) => Math.Max(0, Math.Min(1, fraction));
    }
}
