using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Origin.Infrastructure
{
    /// <summary>
    /// Shorthand for the eased property animations used throughout the interface. WPF composes
    /// these on the GPU through Direct3D, so they stay smooth on modest HTPC hardware.
    /// </summary>
    public static class Anim
    {
        public static readonly IEasingFunction EaseOut = Freeze(new QuarticEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
        public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

        public static void To(IAnimatable target, DependencyProperty property, double to, int milliseconds, IEasingFunction easing = null, Action completed = null)
        {
            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = easing ?? EaseOut,
            };

            if (completed != null)
            {
                animation.Completed += (s, e) => completed();
            }

            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        public static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds, IEasingFunction easing = null, Action completed = null)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = easing ?? EaseOut,
            };

            if (completed != null)
            {
                animation.Completed += (s, e) => completed();
            }

            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>Stops any animation and pins the property to <paramref name="value"/>.</summary>
        public static void Set(IAnimatable target, DependencyProperty property, double value)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, value);
        }

        private static IEasingFunction Freeze(EasingFunctionBase easing)
        {
            easing.Freeze();
            return easing;
        }
    }
}
