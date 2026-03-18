using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsunippy.Runtime.Trace
{
    public static class TimingTraceJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        public static void Save(string path, TimingTraceDocument trace)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(trace, Options);
            File.WriteAllText(path, json);
        }

        public static TimingTraceDocument Load(string path)
        {
            var trace = JsonSerializer.Deserialize<TimingTraceDocument>(File.ReadAllText(path), Options)
                        ?? new TimingTraceDocument();

            if (trace.SchemaVersion > TimingTraceSchema.CurrentVersion)
                throw new InvalidDataException($"Trace schema {trace.SchemaVersion} is newer than supported schema {TimingTraceSchema.CurrentVersion}.");

            trace.Events ??= new();
            trace.ObservedDecisions ??= new();
            trace.CapturedProfile ??= Controller.TimingControllerProfile.CreateFrontierDefault();
            trace.CapturedKnowledge ??= new TimingKnowledgeSnapshot();
            trace.Metadata ??= new TimingTraceMetadata();
            return trace;
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
