namespace Agent.Runtime;

public static class JobProgressStages
{
    public const string Downloading = "downloading";
    public const string SendingToPrinter = "sending_to_printer";
    public const string Printing = "printing";
    public const string VerifyingCompletion = "verifying_completion";
}

public sealed record JobProgressUpdate(string Stage, long? SpoolerJobId = null);
