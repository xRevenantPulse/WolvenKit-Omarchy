global using Process = InstallerPolicyTests.ProcessTripwire;
global using Directory = InstallerPolicyTests.DirectoryTripwire;
global using File = InstallerPolicyTests.FileTripwire;
global using Environment = InstallerPolicyTests.EnvironmentTripwire;

namespace InstallerPolicyTests;

internal static class Boundaries
{
    internal static int Calls;
    internal static Exception Hit(string name)
    {
        Calls++;
        return new InvalidOperationException("SIDE_EFFECT_TRIPWIRE: " + name);
    }
}
internal sealed class ProcessTripwire
{
    public static ProcessTripwire[] GetProcessesByName(string name) => throw Boundaries.Hit("Process.GetProcessesByName");
    public static ProcessTripwire Start(string path) => throw Boundaries.Hit("Process.Start");
}
internal static class DirectoryTripwire
{
    public static bool Exists(string path) => throw Boundaries.Hit("Directory.Exists");
    public static string[] GetFiles(string path, string pattern, SearchOption option) => throw Boundaries.Hit("Directory.GetFiles");
    public static void CreateDirectory(string path) => throw Boundaries.Hit("Directory.CreateDirectory");
    public static void Delete(string path, bool recursive) => throw Boundaries.Hit("Directory.Delete");
}
internal static class FileTripwire
{
    public static void Move(string source, string destination, bool overwrite) => throw Boundaries.Hit("File.Move");
}
internal static class EnvironmentTripwire
{
    public static void Exit(int code) => throw Boundaries.Hit("Environment.Exit");
}
