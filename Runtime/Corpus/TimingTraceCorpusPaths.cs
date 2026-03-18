using System;
using System.IO;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Corpus
{
    public static class TimingTraceCorpusPaths
    {
        public static string DefaultRoot
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TsunippyLab", "Corpus", "v1");

        public static string DefaultManifestPath
            => Path.Combine(DefaultRoot, TimingTraceCorpusSchema.DefaultManifestFileName);

        public static string ResolveManifestPath(string pathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(pathOrDirectory))
                return DefaultManifestPath;

            if (File.Exists(pathOrDirectory))
                return Path.GetFullPath(pathOrDirectory);

            return Path.Combine(Path.GetFullPath(pathOrDirectory), TimingTraceCorpusSchema.DefaultManifestFileName);
        }

        public static string ResolveCorpusRoot(string pathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(pathOrDirectory))
                return DefaultRoot;

            if (File.Exists(pathOrDirectory))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(pathOrDirectory));
                return string.IsNullOrEmpty(directory) ? DefaultRoot : directory;
            }

            return Path.GetFullPath(pathOrDirectory);
        }

        public static string ResolveTracePath(string corpusRoot, TimingTraceCorpusEntry entry)
        {
            var relativePath = entry?.RelativeTracePath ?? string.Empty;
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(corpusRoot, normalized);
        }

        public static string GetBucketDirectory(TimingTraceScenarioBucket bucket)
            => bucket switch
            {
                TimingTraceScenarioBucket.InstantBaseline => "instant-baseline",
                TimingTraceScenarioBucket.CastBaseline => "cast-baseline",
                TimingTraceScenarioBucket.ConflictRecovery => "conflict-recovery",
                TimingTraceScenarioBucket.MessyGameplay => "messy-gameplay",
                _ => "uncategorized",
            };
    }
}
