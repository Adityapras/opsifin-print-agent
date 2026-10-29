namespace Agent.Service;

internal static class AgentMetadata
{
    // Versi protokol dikirim eksplisit supaya Native AOT tidak bergantung pada reflection
    // metadata assembly yang bisa dipangkas saat publish.
    public const string Version = "0.1.0";
}

internal static class AgentRuntimeDefaults
{
    public const int HeartbeatIntervalSeconds = 30;
}
