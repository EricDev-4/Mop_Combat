using System;
using System.Collections.Generic;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>One captured console entry.</summary>
    public struct RevvyLogEntry
    {
        public long Sequence;
        public DateTime TimestampUtc;
        public LogType Type;
        public string Message;
        public string StackTrace;
    }

    /// <summary>
    /// Fixed-size ring buffer over the editor console, feeding the <c>read_logs</c> tool
    /// (contract §4, Tier-1 #3).
    ///
    /// Subscribes to <see cref="Application.logMessageReceived"/> (main-thread variant),
    /// so the writer is always the main thread; readers are socket workers, hence the
    /// lock. Older entries are overwritten silently — the tool reports how many were
    /// dropped so a caller can tell truncation from an empty console.
    /// </summary>
    public static class RevvyLogBuffer
    {
        public const int Capacity = 2048;

        private static readonly object Gate = new object();
        private static readonly RevvyLogEntry[] Ring = new RevvyLogEntry[Capacity];
        private static int _writeIndex;
        private static long _sequence;
        private static long _dropped;
        private static bool _subscribed;

        /// <summary>Idempotent; called from the bridge bootstrap on every domain load.</summary>
        public static void Install()
        {
            lock (Gate)
            {
                if (_subscribed)
                {
                    return;
                }

                _subscribed = true;
            }

            Application.logMessageReceived -= OnLogMessage;
            Application.logMessageReceived += OnLogMessage;
        }

        public static void Uninstall()
        {
            Application.logMessageReceived -= OnLogMessage;
            lock (Gate)
            {
                _subscribed = false;
            }
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                if (_sequence >= Capacity)
                {
                    _dropped++;
                }

                Ring[_writeIndex] = new RevvyLogEntry
                {
                    Sequence = ++_sequence,
                    TimestampUtc = DateTime.UtcNow,
                    Type = type,
                    Message = message ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty
                };
                _writeIndex = (_writeIndex + 1) % Capacity;
            }
        }

        /// <summary>Total entries captured since load (including those already overwritten).</summary>
        public static long TotalCaptured
        {
            get
            {
                lock (Gate)
                {
                    return _sequence;
                }
            }
        }

        public static long DroppedCount
        {
            get
            {
                lock (Gate)
                {
                    return _dropped;
                }
            }
        }

        /// <summary>Oldest-to-newest snapshot of everything currently retained.</summary>
        public static List<RevvyLogEntry> Snapshot()
        {
            lock (Gate)
            {
                int retained = (int)Math.Min(_sequence, Capacity);
                List<RevvyLogEntry> entries = new List<RevvyLogEntry>(retained);
                int start = (_writeIndex - retained + Capacity) % Capacity;
                for (int i = 0; i < retained; i++)
                {
                    entries.Add(Ring[(start + i) % Capacity]);
                }

                return entries;
            }
        }

        /// <summary>Empties the bridge's copy. Does not touch the Unity console itself.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Array.Clear(Ring, 0, Ring.Length);
                _writeIndex = 0;
                _sequence = 0;
                _dropped = 0;
            }
        }

        public static bool IsProblem(LogType type)
        {
            return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        }
    }
}
