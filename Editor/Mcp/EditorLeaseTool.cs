using AgentChecks.Editor;
using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace AgentChecks.Editor.Mcp
{
    [McpForUnityTool(
        "editor_lease",
        Description = "Cooperative editor lease for parallel agents. Acquire it before execute_code, scene or prefab saves, asset refresh, script changes that trigger compilation, test runs or Play; on busy, wait or report the holder. acquire by the holder renews the lease. The lease survives domain reload and expires after ttl seconds.",
        Group = "core")]
    public static class EditorLeaseTool
    {
        public sealed class Parameters
        {
            [ToolParameter("acquire, release or status.", Required = false, DefaultValue = "status")]
            public string action { get; set; }

            [ToolParameter("Caller name, stable for the session, for example claude-ui or codex-worker. Required for acquire and release.", Required = false)]
            public string client { get; set; }

            [ToolParameter("What the caller is about to change.", Required = false)]
            public string reason { get; set; }

            [ToolParameter("Lease duration in seconds.", Required = false, DefaultValue = "600")]
            public int? ttl { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var parameters = @params?.ToObject<Parameters>() ?? new Parameters();
            var action = (parameters.action ?? "status").Trim().ToLowerInvariant();
            var client = parameters.client?.Trim() ?? string.Empty;
            if (action is "acquire" or "release" && client.Length == 0)
                return new ErrorResponse("client_required", new { message = "Pass client for acquire and release." });
            return action switch
            {
                "acquire" => Acquire(client, parameters.reason?.Trim() ?? string.Empty, parameters.ttl ?? EditorLease.DefaultTtlSeconds),
                "release" => EditorLease.Release(client)
                    ? new SuccessResponse("released", Describe(EditorLease.Current))
                    : new ErrorResponse("not_holder", Describe(EditorLease.Current)),
                "status" => new SuccessResponse(EditorLease.Current == null ? "free" : "held", Describe(EditorLease.Current)),
                _ => new ErrorResponse("unsupported_action", new { message = "Valid actions: acquire, release, status." })
            };
        }

        private static object Acquire(string client, string reason, int ttl)
        {
            if (ttl <= 0)
                return new ErrorResponse("invalid_ttl", new { message = "ttl must be a positive number of seconds." });
            return EditorLease.TryAcquire(client, reason, ttl, out var holder)
                ? new SuccessResponse("acquired", Describe(holder))
                : new ErrorResponse("busy", Describe(holder));
        }

        private static object Describe(EditorLeaseHolder holder)
        {
            if (holder == null)
                return new { status = "free" };
            return new
            {
                status = "held",
                client = holder.Client,
                reason = holder.Reason,
                expires_in = (int)Math.Ceiling((holder.ExpiresUtc - DateTime.UtcNow).TotalSeconds)
            };
        }
    }
}
