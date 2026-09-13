using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Marshals work from the HTTP worker threads onto the Unity main thread.
    ///
    /// Contract §8.2: <b>every</b> Unity API call must run on the main thread. The
    /// bridge therefore never touches UnityEditor/UnityEngine APIs from a socket
    /// thread — it enqueues a delegate here and blocks on the result.
    ///
    /// The queue is pumped from <see cref="EditorApplication.update"/>. During domain
    /// reload, script compilation, or editor shutdown the pump stalls; every caller
    /// supplies a timeout so a stalled editor produces an MCP error instead of a hung
    /// HTTP connection.
    /// </summary>
    [InitializeOnLoad]
    public static class RevvyMainThread
    {
        /// <summary>Default budget for a single tool dispatch. Long imports can exceed this.</summary>
        public const int DefaultTimeoutMs = 30000;

        private static readonly ConcurrentQueue<PendingWork> Queue = new ConcurrentQueue<PendingWork>();
        private static int _mainThreadId = -1;

        /// <summary>Number of main-thread jobs completed since load. Surfaced in the status window.</summary>
        public static long CompletedJobs;

        static RevvyMainThread()
        {
            // Static ctor of an [InitializeOnLoad] type always runs on the main thread.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        public static bool IsMainThread
        {
            get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        public static int PendingCount
        {
            get { return Queue.Count; }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and returns its value.
        /// Called from a socket worker thread; blocks until the pump executes it.
        /// </summary>
        /// <exception cref="TimeoutException">The main thread did not drain the queue in time.</exception>
        public static T Run<T>(Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            if (work == null)
            {
                throw new ArgumentNullException("work");
            }

            // Re-entrancy: already on the main thread, just call through. Enqueuing here
            // would deadlock (the pump cannot run while we block inside it).
            if (IsMainThread)
            {
                return work();
            }

            PendingWork pending = new PendingWork(() => work());
            Queue.Enqueue(pending);

            if (!pending.Completed.Wait(timeoutMs))
            {
                pending.Abandoned = true;
                throw new TimeoutException(
                    "Unity main thread did not respond within " + timeoutMs +
                    "ms (editor busy, compiling, or reloading domain).");
            }

            if (pending.Error != null)
            {
                // A tool's own RevvyToolException already carries a caller-facing message;
                // re-describing it would prefix the type name into the MCP error text.
                RevvyToolException toolFailure = Unwrap(pending.Error) as RevvyToolException;
                throw toolFailure != null
                    ? new RevvyToolException(toolFailure.Message, toolFailure.ErrorCode, pending.Error)
                    : new RevvyToolException(RevvyLog.Describe(pending.Error), pending.Error);
            }

            return (T)pending.Result;
        }

        /// <summary>Peels the TargetInvocationException wrappers reflection adds.</summary>
        private static Exception Unwrap(Exception exception)
        {
            Exception current = exception;
            while (current is System.Reflection.TargetInvocationException && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current;
        }

        /// <summary>Void overload of <see cref="Run{T}"/>.</summary>
        public static void Run(Action work, int timeoutMs = DefaultTimeoutMs)
        {
            Run<object>(() =>
            {
                work();
                return null;
            }, timeoutMs);
        }

        /// <summary>Fire-and-forget main-thread work; used for shutdown-time bookkeeping.</summary>
        public static void Post(Action work)
        {
            if (work == null)
            {
                return;
            }

            if (IsMainThread)
            {
                work();
                return;
            }

            PendingWork pending = new PendingWork(() =>
            {
                work();
                return null;
            })
            { Abandoned = true };
            Queue.Enqueue(pending);
        }

        private static void Pump()
        {
            // Bounded drain: a flood of queued work must not stall the editor's frame.
            const int MaxPerFrame = 32;
            for (int i = 0; i < MaxPerFrame; i++)
            {
                PendingWork pending;
                if (!Queue.TryDequeue(out pending))
                {
                    return;
                }

                try
                {
                    pending.Result = pending.Work();
                }
                catch (Exception exception)
                {
                    pending.Error = exception;
                }
                finally
                {
                    Interlocked.Increment(ref CompletedJobs);
                    if (!pending.Abandoned)
                    {
                        pending.Completed.Set();
                    }
                    else
                    {
                        pending.Completed.Dispose();
                    }
                }
            }
        }

        private sealed class PendingWork
        {
            public readonly Func<object> Work;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;

            /// <summary>Set when the caller stopped waiting (timeout) or never intended to.</summary>
            public volatile bool Abandoned;

            public PendingWork(Func<object> work)
            {
                Work = work;
            }
        }
    }

    /// <summary>
    /// Tool-level failure that should surface to the client as an MCP
    /// <c>isError</c> result rather than a JSON-RPC protocol error.
    /// </summary>
    public sealed class RevvyToolException : Exception
    {
        public string ErrorCode { get; private set; }

        /// <summary>
        /// Extra machine-readable fields merged into the MCP error payload alongside
        /// <c>error_code</c> and <c>error</c>. Used where a caller needs to act on *why*
        /// something failed rather than just that it did — e.g. Tier-6's
        /// <c>viewport_unavailable</c> carries a <c>renderer</c> object so the agent can
        /// tell "no display" from "no Scene view open"
        /// (docs/design/tier6-screenshot-contract.md §8). Null for most failures.
        /// </summary>
        public RevvyJson Details { get; private set; }

        public RevvyToolException(string message)
            : base(message)
        {
        }

        public RevvyToolException(string message, string errorCode, RevvyJson details)
            : base(message)
        {
            ErrorCode = errorCode;
            Details = details;
        }

        public RevvyToolException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public RevvyToolException(string message, Exception inner)
            : base(message, inner)
        {
        }

        public RevvyToolException(string message, string errorCode, Exception inner)
            : base(message, inner)
        {
            ErrorCode = errorCode;

            // Preserve the diagnosis across every rewrap. The original is usually buried
            // under a TargetInvocationException from the reflection dispatch, and is
            // rewrapped again when it crosses the main-thread hop, so walk the chain
            // rather than testing only the immediate inner exception.
            Exception current = inner;
            while (current != null)
            {
                RevvyToolException toolInner = current as RevvyToolException;
                if (toolInner != null && toolInner.Details != null)
                {
                    Details = toolInner.Details;
                    break;
                }

                current = current.InnerException;
            }
        }
    }
}
