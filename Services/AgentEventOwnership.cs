namespace DevezCode.Services;

internal static class AgentEventOwnership
{
    internal static bool IsMatch(string? sessionAgentId, string sourceAgentId)
        => !string.IsNullOrWhiteSpace(sessionAgentId)
           && !string.IsNullOrWhiteSpace(sourceAgentId)
           && string.Equals(sessionAgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase);
}
