using UnityEngine;

namespace Synty.Tools
{
    /// <summary>
    /// Customer-facing console logging for Synty Passport. Every line is prefixed with
    /// [Synty Passport] so users can filter the Console. Use Info for normal status,
    /// Warn for recoverable issues (yellow), and Error for failures (red).
    /// </summary>
    public static class SyntyLog
    {
        private const string Prefix = "[Synty Importer] ";

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
    }
}
