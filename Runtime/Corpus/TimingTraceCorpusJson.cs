using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsunippy.Runtime.Corpus
{
    public static class TimingTraceCorpusJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        public static void Save(string path, TimingTraceCorpusDocument corpus)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(corpus ?? new TimingTraceCorpusDocument(), Options));
        }

        public static TimingTraceCorpusDocument Load(string path)
        {
            var corpus = JsonSerializer.Deserialize<TimingTraceCorpusDocument>(File.ReadAllText(path), Options)
                         ?? new TimingTraceCorpusDocument();

            if (corpus.SchemaVersion > TimingTraceCorpusSchema.CurrentVersion)
                throw new InvalidDataException($"Corpus schema {corpus.SchemaVersion} is newer than supported schema {TimingTraceCorpusSchema.CurrentVersion}.");

            corpus.Entries ??= new();
            foreach (var entry in corpus.Entries)
                entry.Tags ??= new();

            return corpus;
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
