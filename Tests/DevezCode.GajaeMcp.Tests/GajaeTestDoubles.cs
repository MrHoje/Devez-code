namespace DevezCode.Services;

public sealed class AgentDef { }

public static class AgentRegistry
{
    public static AgentDef? Find(string id) => new();
    public static bool IsInstalled(AgentDef agent) => true;
}

public static class ConfigFileWatcher
{
    public static IDisposable? Watch(string? filePath, Action onChanged) => null;
}
