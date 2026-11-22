using System;

namespace WolvenKit.App.Services;

/// <summary>
/// Product policy for this downstream build, on every platform. Not a persisted preference
/// or Proton compatibility option. Missing downstream releases never permit an upstream fallback.
/// Scripts and templates use a separate updater and are not governed by this policy.
/// </summary>
public static class ForkBinaryUpdatePolicy
{
    public static bool RequiresManualUpdates => true;
    public const string Title = "WolvenKit-Omarchy: manual binary updates";
    public const string Message = "This xRevenantPulse/WolvenKit-Omarchy build uses manual binary updates. " +
        "In-app binary checks and installation are disabled, regardless of release channel or saved update preferences. " +
        "Review upstream changes and obtain a reviewed downstream build manually: https://github.com/xRevenantPulse/WolvenKit-Omarchy. " +
        "No upstream binary fallback is allowed. Scripts and templates are updated separately.";

    public static void EnsureInstallationAllowed()
    {
        if (RequiresManualUpdates)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
