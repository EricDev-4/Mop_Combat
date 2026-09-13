using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-1 tool #3 (contract §4): editor console tail / filter / errors.
    ///
    /// Reads the <see cref="RevvyLogBuffer"/> ring, which only retains messages logged
    /// since the bridge loaded — Unity exposes no API for the console's own backlog.
    /// </summary>
    public static class RevvyReadLogsTool
    {
        private const int DefaultCount = 50;
        private const int MaxCount = 500;
        private const int MaxMessageChars = 4000;

        [RevvyTool(
            "read_logs",
            "Read the Unity editor console captured by the bridge. Actions: tail (most recent N entries), " +
            "errors (errors, exceptions and failed assertions only), filter (regex or substring match over the " +
            "message text), stats (counts by severity), clear (drop the bridge's buffer; the Unity console " +
            "window is untouched). Only messages logged since the bridge loaded are available.",
            Title = "Read editor logs",
            ReadOnlyActions = new[] { "tail", "errors", "filter", "stats" },
            Idempotent = true)]
        public static RevvyJson ReadLogs(
            [RevvyToolParam("Operation to perform.",
                Enum = new[] { "tail", "errors", "filter", "stats", "clear" })]
            string action = "tail",
            [RevvyToolParam("Maximum entries returned (newest first). Default 50, max 500.")]
            int count = DefaultCount,
            [RevvyToolParam("Match expression for action=filter. Treated as a regex, falling back to a " +
                            "case-insensitive substring match if it does not compile.")]
            string pattern = null,
            [RevvyToolParam("Restrict results to these severities: Log, Warning, Error, Exception, Assert.",
                Enum = new[] { "Log", "Warning", "Error", "Exception", "Assert" })]
            string[] severities = null,
            [RevvyToolParam("Include the captured stack trace for each entry. Off by default (token cost).")]
            bool include_stack_trace = false)
        {
            string normalized = (action ?? "tail").Trim().ToLowerInvariant();

            if (normalized == "clear")
            {
                long dropped = RevvyLogBuffer.TotalCaptured;
                RevvyLogBuffer.Clear();
                return RevvyJson.Object()
                    .Set("success", true)
                    .Set("action", "clear")
                    .Set("cleared_entries", dropped)
                    .Set("note", "Bridge buffer only; the Unity console window still holds its own entries.");
            }

            List<RevvyLogEntry> entries = RevvyLogBuffer.Snapshot();

            if (normalized == "stats")
            {
                return BuildStats(entries);
            }

            HashSet<LogType> allowed = ParseSeverities(severities);

            switch (normalized)
            {
                case "tail":
                    break;
                case "errors":
                    allowed = new HashSet<LogType> { LogType.Error, LogType.Exception, LogType.Assert };
                    break;
                case "filter":
                    if (string.IsNullOrEmpty(pattern))
                    {
                        throw new RevvyToolException("action=filter requires a 'pattern'.");
                    }

                    break;
                default:
                    throw new RevvyToolException(
                        "Unknown action '" + action + "'. Expected one of: tail, errors, filter, stats, clear.");
            }

            Predicate<string> matches = BuildMatcher(normalized == "filter" ? pattern : null);

            int capped = count <= 0 ? DefaultCount : Math.Min(count, MaxCount);
            RevvyJson items = RevvyJson.Array();
            int totalMatched = 0;

            // Walk newest-first so truncation drops the oldest, not the newest.
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                RevvyLogEntry entry = entries[i];
                if (allowed != null && !allowed.Contains(entry.Type))
                {
                    continue;
                }

                if (matches != null && !matches(entry.Message))
                {
                    continue;
                }

                totalMatched++;
                if (items.Count >= capped)
                {
                    continue;
                }

                RevvyJson item = RevvyJson.Object()
                    .Set("sequence", entry.Sequence)
                    .Set("timestamp", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture))
                    .Set("severity", entry.Type.ToString())
                    .Set("message", Truncate(entry.Message));

                if (include_stack_trace && !string.IsNullOrEmpty(entry.StackTrace))
                {
                    item.Set("stack_trace", Truncate(entry.StackTrace));
                }

                items.Add(item);
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", normalized)
                .Set("entries", items)
                .Set("returned", items.Count)
                .Set("total_matched", totalMatched)
                .Set("truncated", totalMatched > items.Count)
                .Set("buffer_retained", entries.Count)
                .Set("buffer_capacity", RevvyLogBuffer.Capacity)
                .Set("buffer_overwritten", RevvyLogBuffer.DroppedCount);
        }

        private static RevvyJson BuildStats(List<RevvyLogEntry> entries)
        {
            Dictionary<LogType, int> counts = new Dictionary<LogType, int>();
            foreach (RevvyLogEntry entry in entries)
            {
                int current;
                counts.TryGetValue(entry.Type, out current);
                counts[entry.Type] = current + 1;
            }

            RevvyJson bySeverity = RevvyJson.Object();
            foreach (LogType type in (LogType[])Enum.GetValues(typeof(LogType)))
            {
                int value;
                counts.TryGetValue(type, out value);
                bySeverity.Set(type.ToString(), value);
            }

            int problems = 0;
            foreach (RevvyLogEntry entry in entries)
            {
                if (RevvyLogBuffer.IsProblem(entry.Type))
                {
                    problems++;
                }
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "stats")
                .Set("by_severity", bySeverity)
                .Set("problem_count", problems)
                .Set("buffer_retained", entries.Count)
                .Set("buffer_capacity", RevvyLogBuffer.Capacity)
                .Set("buffer_overwritten", RevvyLogBuffer.DroppedCount)
                .Set("total_captured", RevvyLogBuffer.TotalCaptured);
        }

        private static HashSet<LogType> ParseSeverities(string[] severities)
        {
            if (severities == null || severities.Length == 0)
            {
                return null;
            }

            HashSet<LogType> allowed = new HashSet<LogType>();
            foreach (string severity in severities)
            {
                LogType parsed;
                if (!TryParseLogType(severity, out parsed))
                {
                    throw new RevvyToolException(
                        "Unknown severity '" + severity + "'. Expected Log, Warning, Error, Exception or Assert.");
                }

                allowed.Add(parsed);
            }

            return allowed;
        }

        private static bool TryParseLogType(string value, out LogType result)
        {
            result = LogType.Log;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (LogType candidate in (LogType[])Enum.GetValues(typeof(LogType)))
            {
                if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    result = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Regex if it compiles, otherwise a case-insensitive substring test.</summary>
        private static Predicate<string> BuildMatcher(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            try
            {
                Regex regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return text => text != null && regex.IsMatch(text);
            }
            catch (ArgumentException)
            {
                string needle = pattern;
                return text => text != null &&
                               text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxMessageChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxMessageChars) + "... [truncated]";
        }
    }
}
