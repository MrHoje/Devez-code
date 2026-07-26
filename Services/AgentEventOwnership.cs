namespace DevezCode.Services;

internal static class AgentEventOwnership
{
    internal static bool IsMatch(string? sessionAgentId, string sourceAgentId)
        => !string.IsNullOrWhiteSpace(sessionAgentId)
           && !string.IsNullOrWhiteSpace(sourceAgentId)
           && string.Equals(sessionAgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase);
}

internal static class TrackingEnvironment
{
    internal const string VariableName = "DEVEZCODE_TRACKING_AGENT";

    internal static bool IsExpected(string? actualAgentId, string expectedAgentId)
        => AgentEventOwnership.IsMatch(actualAgentId, expectedAgentId);

    internal static string CmdSetLine(string agentId)
        => $"set \"{VariableName}={agentId}\"\r\n";
}
