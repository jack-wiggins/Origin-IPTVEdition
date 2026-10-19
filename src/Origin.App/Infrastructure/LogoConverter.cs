using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Origin.Infrastructure
{
    /// <summary>
    /// Turns a logo URL into a small, cached bitmap. Playlists can list thousands of logos, so
    /// they are decoded at guide size and only the most recent few hundred are kept.
    /// </summary>
    public sealed class LogoConverter : IValueConverter
    {
        private const int Capacity = 600;

        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapImage>>> Index =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapImage>>>(StringComparer.Ordinal);

        private static readonly LinkedList<KeyValuePair<string, BitmapImage>> Recent = new LinkedList<KeyValuePair<string, BitmapImage>>();

        public int DecodeHeight { get; set; } = 96;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            {
                return null;
            }

            var key = DecodeHeight + "|" + uri.AbsoluteUri;
            if (Index.TryGetValue(key, out var node))
            {
                Recent.Remove(node);
                Recent.AddFirst(node);
                return node.Value.Value;
            }

            BitmapImage image;
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.DecodePixelHeight = DecodeHeight;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
            }
            catch (Exception)
            {
                return null;
            }

            node = Recent.AddFirst(new KeyValuePair<string, BitmapImage>(key, image));
            Index[key] = node;
            if (Recent.Count > Capacity)
            {
                Index.Remove(Recent.Last.Value.Key);
                Recent.RemoveLast();
            }

            return image;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
