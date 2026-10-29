using System.Security.AccessControl;
using System.Security.Principal;

namespace Agent.Service;

internal static class AgentStatePaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Opsifin",
        "PrintAgent");

    public static string StateDirectory { get; } = Path.Combine(RootDirectory, "state");

    public static string IdentityFile { get; } = Path.Combine(StateDirectory, "identity.json");

    public static string OutboxDatabase { get; } = Path.Combine(StateDirectory, "agent.db");

    public static string DocumentsDirectory { get; } = Path.Combine(StateDirectory, "documents");

    public static string PrintJournalDirectory { get; } = Path.Combine(StateDirectory, "active-print-jobs");

    public static string CommandJournalDirectory { get; } = Path.Combine(StateDirectory, "commands");

    public static string LogDirectory { get; } = Path.Combine(StateDirectory, "logs");

    public static string CommandAttachmentDirectory { get; } = Path.Combine(StateDirectory, "command-attachments");

    public static void EnsureSecureStateDirectory()
    {
        Directory.CreateDirectory(StateDirectory);

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(StateDirectory).SetAccessControl(security);
    }
}
