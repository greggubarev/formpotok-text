namespace FormPotok.PilotCore.Learning;

public enum LocalRecognitionOutcome
{
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
}

public enum LocalUsagePlatform
{
    Windows = 1,
    Linux = 2,
    MacOS = 3,
    Other = 4,
}

/// <summary>Content-free allowlisted data only; bucket values are upper bounds, not exact measurements.</summary>
public sealed record LocalUsageStatisticsEvent(
    int SchemaVersion,
    string ProductVersion,
    LocalUsagePlatform Platform,
    LocalRecognitionOutcome Outcome,
    int PageCountBucket,
    int BlockCountBucket,
    int DurationSecondsBucket);
