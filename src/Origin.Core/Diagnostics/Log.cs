using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Origin.Core.Diagnostics
{
    /// <summary>
    /// Small thread-safe file logger. Anything registered with <see cref="RegisterSecret"/>, plus
    /// username/password/token query parameters, is masked before it reaches the disk.
    /// </summary>
    public static class Log
    {
        private const int RetentionDays = 14;

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Regex CredentialQuery = new Regex(
            @"(?i)\b(username|password|token)=([^&\s""']+)", RegexOptions.Compiled);

        private static string _directory;

        public static void Initialize(string directory)
        {
            lock (Sync)
            {
                _directory = directory;
                Directory.CreateDirectory(directory);
            }

            PruneOldLogs();
        }

        /// <summary>Masks <paramref name="secret"/> (and its URL-encoded form) in every future log line.</summary>
        public static void RegisterSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 3)
            {
                return;
            }

            lock (Sync)
            {
                Secrets.Add(secret);
                Secrets.Add(Uri.EscapeDataString(secret));
            }
        }

        public static void Info(string message) => Write("INFO ", message, null);

        public static void Warn(string message, Exception exception = null) => Write("WARN ", message, exception);

        public static void Error(string message, Exception exception = null) => Write("ERROR", message, exception);

        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = CredentialQuery.Replace(text, "$1=***");

            string[] secrets;
            lock (Sync)
            {
                // Longest first so a secret that contains another is masked whole.
                secrets = Secrets.OrderByDescending(s => s.Length).ToArray();
            }

            foreach (var secret in secrets)
            {
                result = result.Replace(secret, "***");
            }

            return result;
        }

        private static void Write(string level, string message, Exception exception)
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                   .Append(' ').Append(level).Append(' ')
                   .Append(message);

            if (exception != null)
            {
                builder.AppendLine().Append(exception);
            }

            var line = Sanitize(builder.ToString());
            Debug.WriteLine(line);

            lock (Sync)
            {
                if (_directory == null)
                {
                    return;
                }

                try
                {
                    var file = Path.Combine(_directory, "origin-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Logging must never take the application down.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static void PruneOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var file in Directory.GetFiles(_directory, "origin-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
