using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>Central logging gate so the stock setting can suppress every non-error message.</summary>
    internal static class ScrollGuardLog
    {
        internal static void Info(object message)
        {
            if (!ScrollGuard.ErrorsOnlyLogging)
                Debug.Log(message);
        }

        internal static void Warning(object message)
        {
            if (!ScrollGuard.ErrorsOnlyLogging)
                Debug.LogWarning(message);
        }

        internal static void Error(object message)
        {
            // Errors are intentionally never suppressed.
            Debug.LogError(message);
        }
    }
}
