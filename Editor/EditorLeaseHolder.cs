using System;

namespace AgentChecks.Editor
{
    public sealed class EditorLeaseHolder
    {
        public EditorLeaseHolder(string client, string reason, DateTime expiresUtc)
        {
            Client = client;
            Reason = reason;
            ExpiresUtc = expiresUtc;
        }

        public string Client { get; }
        public string Reason { get; }
        public DateTime ExpiresUtc { get; }
    }
}
