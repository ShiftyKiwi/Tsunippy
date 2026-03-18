using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tsunippy.Runtime.Controller;

namespace Tsunippy.Runtime.Trace
{
    public sealed class TimingTraceCaptureSession
    {
        private readonly List<TimingTraceEvent> events = new();

        public TimingTraceCaptureSession(TimingControllerProfile profile, TimingKnowledgeSnapshot knowledge, string label, string pluginVersion, int maxEvents = 200_000)
            : this(
                profile,
                knowledge,
                new TimingTraceMetadata
                {
                    Label = label ?? string.Empty,
                    PluginVersion = pluginVersion ?? string.Empty,
                },
                maxEvents)
        {
        }

        public TimingTraceCaptureSession(TimingControllerProfile profile, TimingKnowledgeSnapshot knowledge, TimingTraceMetadata metadata, int maxEvents = 200_000)
        {
            MaxEvents = Math.Max(maxEvents, 1000);
            Trace = new TimingTraceDocument
            {
                Metadata = metadata ?? new TimingTraceMetadata(),
                CapturedProfile = profile?.Clone() ?? TimingControllerProfile.CreateFrontierDefault(),
                CapturedKnowledge = knowledge ?? new TimingKnowledgeSnapshot(),
                Events = events,
                ObservedDecisions = new List<TimingDecisionTrace>(),
            };

            Trace.Metadata.Tags ??= new();
        }

        public TimingTraceDocument Trace { get; }
        public int MaxEvents { get; }
        public int EventCount => events.Count;
        public bool IsTruncated { get; private set; }
        public string LastSavedPath { get; private set; } = string.Empty;

        public bool Record(TimingTraceEvent traceEvent)
        {
            if (traceEvent == null)
                return false;

            if (events.Count >= MaxEvents)
            {
                if (!IsTruncated)
                {
                    IsTruncated = true;
                    Trace.Metadata.Notes = AppendNote(Trace.Metadata.Notes, $"Capture truncated at {MaxEvents} events.");
                }

                return false;
            }

            events.Add(traceEvent);
            return true;
        }

        public void AddNote(double timelineSeconds, string note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return;

            Record(new TimingNoteTraceEvent(timelineSeconds, note.Trim()));
        }

        public void RecordObservedDecision(in TimingDecisionTrace decision)
            => Trace.ObservedDecisions.Add(decision);

        public string SaveToDirectory(string directoryPath)
        {
            var sanitizedLabel = SanitizeFileComponent(Trace.Metadata.Label);
            if (string.IsNullOrEmpty(sanitizedLabel))
                sanitizedLabel = "capture";

            var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sanitizedLabel}.timing-trace.json";
            var path = Path.Combine(directoryPath, fileName);
            TimingTraceJson.Save(path, Trace);
            LastSavedPath = path;
            return path;
        }

        public string SaveToPath(string path)
        {
            TimingTraceJson.Save(path, Trace);
            LastSavedPath = path;
            return path;
        }

        private static string AppendNote(string existing, string next)
            => string.IsNullOrWhiteSpace(existing) ? next : $"{existing} | {next}";

        private static string SanitizeFileComponent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                    builder.Append(ch);
                else if (!char.IsWhiteSpace(ch))
                    builder.Append('-');
                else
                    builder.Append('_');
            }

            return builder.ToString().Trim('-', '_');
        }
    }
}
