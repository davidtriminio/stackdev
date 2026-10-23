using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

public enum DeployPhase { Prepare, Build, Inspect, Check, Confirm, Package, Upload, Remote, Verify, Done }

/// <summary>Something the user should see while a long operation runs.</summary>
/// <param name="Percent">Overall progress 0-100, or -1 when this event does not change it.</param>
public sealed record ProgressEvent(string Phase, string Message, Level Level = Level.Normal, int Percent = -1, int Step = 0, int StepTotal = 0);

public enum DeployOutcome
{
    Success,
    /// <summary>The new version failed and the previous one is running again.</summary>
    RolledBack,
    /// <summary>The new version failed and the automatic return failed too: needs manual attention.</summary>
    RollbackFailed,
    Cancelled,
    Failed,
    /// <summary>The service does not exist on the server yet.</summary>
    NeedsProvision,
    /// <summary>First upload: the files are on the server but the service did not start, and there was no earlier version to go back to.</summary>
    StartFailed,
}

public sealed record DeployResult(DeployOutcome Outcome, string Message, string? BackupId = null, ApiStatus? Status = null, IReadOnlyList<string>? Missing = null)
{
    public bool Ok => Outcome == DeployOutcome.Success;
}

/// <summary>What is about to happen, shown to the user before anything on the server changes.</summary>
public sealed record DeployPlan(
    Server Server,
    string Service,
    string Dll,
    string? Domain,
    string ProjectName,
    int FileCount,
    long SizeBytes,
    int? RuntimeMajor,
    bool ServiceActive,
    int ExistingBackups,
    IReadOnlyList<GuardFinding> Warnings,
    IReadOnlyList<string> Removed);

/// <summary>How to continue when the publish contains files that must not be uploaded.</summary>
public enum GuardDecision { RemoveAndContinue, Cancel }

public sealed class DeployRequest
{
    public required Server Server { get; init; }
    /// <summary>The .csproj to publish.</summary>
    public required string Project { get; init; }
    public required string Service { get; init; }
    public required string Dll { get; init; }
    public string? Domain { get; init; }
    public int Keep { get; init; } = 5;
    public int HealthWaitSeconds { get; init; } = 30;
}

/// <summary>Questions a deployment asks while it runs (the window answers them).</summary>
public interface IDeployDecisions
{
    GuardDecision BlockedFiles(IReadOnlyList<GuardFinding> blocked);
    bool Confirm(DeployPlan plan);
    /// <summary>The build failed: offer to delete bin/obj and try again.</summary>
    bool CleanAndRetry();
}
