using System.Text.Json.Serialization;

namespace FormPotok.PilotCore.Learning;

public sealed record LocalLearningConsent(
    bool Statistics = false,
    bool Examples = false,
    int SchemaVersion = LocalLearningConsent.CurrentSchemaVersion,
    int ConsentVersion = LocalLearningConsent.CurrentConsentVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentConsentVersion = 1;

    [JsonIgnore]
    public bool IsCurrent => SchemaVersion == CurrentSchemaVersion && ConsentVersion == CurrentConsentVersion;

    [JsonIgnore]
    public bool StatisticsEnabled => IsCurrent && Statistics;

    [JsonIgnore]
    public bool ExamplesEnabled => IsCurrent && Examples;
}

public enum LocalExampleApprovalRequirement
{
    SeparatePerExampleApproval = 1,
}

/// <summary>
/// Text and crops may contain personal or confidential data; they are not anonymous.
/// General example consent only permits a future approval prompt, never automatic sharing.
/// </summary>
public static class LocalLearningExamplePolicy
{
    public const bool TextMayContainSensitiveData = true;
    public static LocalExampleApprovalRequirement ApprovalRequirement => LocalExampleApprovalRequirement.SeparatePerExampleApproval;
    public static bool CanRequestApproval(LocalLearningConsent consent) => consent.ExamplesEnabled;
    public static bool AllowsAutomaticUpload(LocalLearningConsent consent) => false;
}
