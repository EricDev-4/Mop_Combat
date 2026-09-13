using System;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Bridge-internal logging.
    ///
    /// Deliberately routed through a single choke point: <c>read_logs</c> streams the
    /// editor console back to the LLM, so every bridge message is prefixed and can be
    /// filtered out of a tool result instead of being mistaken for project output.
    /// </summary>
    public static class RevvyLog
    {
        public const string Prefix = "[Revvy] ";

        /// <summary>Verbose transport tracing; off unless REVVY_UNITY_VERBOSE is truthy.</summary>
        private static readonly bool VerboseEnabled =
            RevvyEnv.IsTruthy(Environment.GetEnvironmentVariable("REVVY_UNITY_VERBOSE"));

        public static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }

        public static void Exception(string context, Exception exception)
        {
            Debug.LogError(Prefix + context + ": " + Describe(exception));
        }

        public static void Verbose(string message)
        {
            if (VerboseEnabled)
            {
                Debug.Log(Prefix + message);
            }
        }

        /// <summary>Flattens a (possibly wrapped) exception into a one-line summary.</summary>
        public static string Describe(Exception exception)
        {
            if (exception == null)
            {
                return "unknown error";
            }

            Exception root = exception;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }

            return root.GetType().Name + ": " + root.Message;
        }
    }
}
