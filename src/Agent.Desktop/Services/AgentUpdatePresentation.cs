using Agent.Local;

namespace Agent.Desktop.Services;

internal static class AgentUpdatePresentation
{
    public static bool ShouldDisplay(AgentUpdateState? update) => update is not null
        && update.Status is not AgentUpdateStatuses.Succeeded;
}
