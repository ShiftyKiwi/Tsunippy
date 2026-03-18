using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsunippy.Runtime.Controller
{
    public static class TimingControllerProfileJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        public static TimingControllerProfile Load(string path)
            => JsonSerializer.Deserialize<TimingControllerProfile>(File.ReadAllText(path), Options)
               ?? TimingControllerProfile.CreateFrontierDefault();

        public static void Save(string path, TimingControllerProfile profile)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(profile ?? TimingControllerProfile.CreateFrontierDefault(), Options));
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
