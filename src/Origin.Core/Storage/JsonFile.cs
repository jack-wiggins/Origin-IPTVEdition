using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Origin.Core.Diagnostics;

namespace Origin.Core.Storage
{
    /// <summary>Reads and atomically writes JSON documents.</summary>
    public static class JsonFile
    {
        public static JsonSerializerSettings SerializerSettings { get; } = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Converters = { new StringEnumConverter() },
        };

        public static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using (var reader = new StreamReader(path, Encoding.UTF8))
            using (var json = new JsonTextReader(reader))
            {
                return JsonSerializer.Create(SerializerSettings).Deserialize<T>(json);
            }
        }

        /// <summary>
        /// Like <see cref="Load{T}"/>, but a corrupt file is moved aside and treated as missing
        /// instead of stopping the application.
        /// </summary>
        public static T TryLoad<T>(string path) where T : class
        {
            try
            {
                return Load<T>(path);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                Log.Warn("Could not read " + path + "; it will be recreated.", ex);
                try
                {
                    File.Copy(path, path + ".corrupt", overwrite: true);
                }
                catch (IOException)
                {
                }

                return null;
            }
        }

        public static void Save<T>(string path, T value, bool indented = true)
        {
            var temp = path + ".tmp";
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                var serializer = JsonSerializer.Create(SerializerSettings);
                serializer.Formatting = indented ? Formatting.Indented : Formatting.None;
                serializer.Serialize(writer, value);
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, null);
            }
            catch (IOException)
            {
                // File.Replace is not supported on every file system (FAT32, some network shares).
                File.Delete(path);
                File.Move(temp, path);
            }
        }
    }
}
