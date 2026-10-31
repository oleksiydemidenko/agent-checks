using System;
using System.Globalization;
using UnityEditor;

namespace AgentChecks.Editor
{
    public static class EditorLease
    {
        public const int DefaultTtlSeconds = 600;
        private const string ClientKey = "AgentTools.EditorLease.Client";
        private const string ReasonKey = "AgentTools.EditorLease.Reason";
        private const string ExpiresKey = "AgentTools.EditorLease.Expires";

        public static EditorLeaseHolder Current
        {
            get
            {
                var client = SessionState.GetString(ClientKey, string.Empty);
                if (client.Length == 0 || !long.TryParse(SessionState.GetString(ExpiresKey, string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                    return null;
                var expires = new DateTime(ticks, DateTimeKind.Utc);
                return expires > DateTime.UtcNow ? new EditorLeaseHolder(client, SessionState.GetString(ReasonKey, string.Empty), expires) : null;
            }
        }

        public static bool IsHeldBy(string client)
        {
            return Current?.Client == client;
        }

        public static bool TryAcquire(string client, string reason, int ttlSeconds, out EditorLeaseHolder holder)
        {
            holder = Current;
            if (holder != null && holder.Client != client)
                return false;
            holder = new EditorLeaseHolder(client, reason, DateTime.UtcNow.AddSeconds(ttlSeconds));
            SessionState.SetString(ClientKey, holder.Client);
            SessionState.SetString(ReasonKey, holder.Reason);
            SessionState.SetString(ExpiresKey, holder.ExpiresUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        public static bool Release(string client)
        {
            if (!IsHeldBy(client))
                return false;
            SessionState.EraseString(ClientKey);
            SessionState.EraseString(ReasonKey);
            SessionState.EraseString(ExpiresKey);
            return true;
        }
    }
}
