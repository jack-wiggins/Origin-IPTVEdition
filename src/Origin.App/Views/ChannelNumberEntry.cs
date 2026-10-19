using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Origin.Infrastructure;

namespace Origin.Views
{
    /// <summary>
    /// Typing a channel number on the remote: digits collect in a small panel and are
    /// committed after a short pause or with OK, like a cable box.
    /// </summary>
    internal sealed class ChannelNumberEntry
    {
        private const int MaxDigits = 5;

        private readonly StringBuilder _digits = new StringBuilder();
        private readonly UIElement _panel;
        private readonly TextBlock _text;
        private readonly Action<string> _commit;
        private readonly DispatcherTimer _timer;

        public ChannelNumberEntry(Dispatcher dispatcher, UIElement panel, TextBlock text, Action<string> commit)
        {
            _panel = panel;
            _text = text;
            _commit = commit;
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(1.8) };
            _timer.Tick += (s, e) => Commit();
        }

        public bool IsActive => _digits.Length > 0;

        public void Add(int digit)
        {
            if (_digits.Length >= MaxDigits)
            {
                return;
            }

            _digits.Append(digit);
            _text.Text = _digits.ToString();
            Anim.To(_panel, UIElement.OpacityProperty, 1, 150);

            _timer.Stop();
            _timer.Start();
        }

        public void Commit()
        {
            var number = _digits.ToString();
            Cancel();
            if (number.Length > 0)
            {
                _commit(number);
            }
        }

        public void Cancel()
        {
            _timer.Stop();
            _digits.Clear();
            Anim.To(_panel, UIElement.OpacityProperty, 0, 250);
        }
    }
}
