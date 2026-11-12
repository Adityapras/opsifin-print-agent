using Agent.Local;

namespace Agent.Desktop.Services;

internal sealed record AgentStatusPresentation(
    string Kind,
    string Title,
    string Description,
    string HubState);

internal static class AgentStatusPresentationFactory
{
    public static AgentStatusPresentation ForStaleSnapshot(
        AgentHealthSnapshot health,
        bool processIsActive)
    {
        if (!processIsActive)
        {
            return new AgentStatusPresentation(
                "danger",
                "Service is unresponsive",
                "Windows reports the service as running, but its process and local status are unavailable.",
                "Status is stale");
        }

        if (health.ConnectionState == AgentConnectionStates.Degraded)
        {
            return new AgentStatusPresentation(
                "warning",
                "Hub connection is degraded",
                "The Windows Service is running. Hub communication or remote configuration failed and will retry automatically.",
                "Retrying");
        }

        return new AgentStatusPresentation(
            "warning",
            "Agent status is delayed",
            "The Windows Service is running, but its local status has not been refreshed recently. Review diagnostics if this continues.",
            "Status delayed");
    }
}
