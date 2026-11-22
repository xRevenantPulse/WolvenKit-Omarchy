using System.Reflection;
using WolvenKit.App.Services;
using Installer = WolvenKit.Unpacker.Program;

namespace InstallerPolicyTests;

public static class Runner
{
#if REAL_INSTALLER
    private const string BoundaryEvidence = "real installer; no substituted-boundary counters";
#else
    private const string BoundaryEvidence = "zero boundary calls";
#endif
    public static async Task<int> Main(string[] args)
    {
        var target = args.Length == 2 ? args[0] : "dummy-install/WolvenKit.exe";
        var payload = args.Length == 2 ? args[1] : "dummy-payload";
        try
        {
            if (!args.Contains("helper-only"))
            {
                var cases = new (string Name, string[] Args)[]
                {
                    ("normal", ["--wolvenkit-exe-path", target, "--unzipped-path", payload]),
                    ("missing", []),
                    ("malformed", ["--not-an-option", "--wolvenkit-exe-path"]),
                    ("help", ["--help"]),
                    ("null", null!),
                };
                foreach (var test in cases)
                {
                    using var output = new StringWriter();
                    var original = Console.Error;
                    int code;
                    try { Console.SetError(output); code = await Installer.Main(test.Args); }
                    finally { Console.SetError(original); }
                    Assert(code == 1, $"{test.Name}: expected policy exit code 1, got {code}; stderr={output}");
                    Assert(output.ToString() == ForkBinaryUpdatePolicy.Message + System.Environment.NewLine,
                        $"{test.Name}: expected exact branded denial, got {output}");
                    AssertNoBoundaries();
                    Console.WriteLine($"PASS Main {test.Name}: code=1 exact branded denial; {BoundaryEvidence}");
                }
            }
            if (!args.Contains("main-only"))
            {
                var handler = typeof(Installer).GetMethod("UpdateWolvenKitExe", BindingFlags.Static | BindingFlags.NonPublic)!;
                foreach (var paths in new[] { new[] { target, payload }, new[] { "", "" }, new string[] { null!, null! } })
                {
                    try
                    {
                        await (Task)handler.Invoke(null, paths)!;
                        throw new Exception("Direct installer handler did not reject policy");
                    }
                    catch (InvalidOperationException ex) when (ex.Message == ForkBinaryUpdatePolicy.Message) { }
                    AssertNoBoundaries();
                }
                Console.WriteLine($"PASS direct handler normal/empty/null: exact branded exception; {BoundaryEvidence}");
                foreach (var helper in new[] { "IsProcessRunningByPath", "MoveDirectoryWithOverwrite" })
                {
                    var method = typeof(Installer).GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic)!;
                    object?[] parameters = helper == "IsProcessRunningByPath" ? [target] : [payload, target];
                    try
                    {
                        method.Invoke(null, parameters);
                        throw new Exception($"Direct {helper} did not reject policy");
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException denied && denied.Message == ForkBinaryUpdatePolicy.Message) { }
                    AssertNoBoundaries();
                    Console.WriteLine($"PASS direct {helper}: exact branded exception; {BoundaryEvidence}");
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void AssertNoBoundaries()
    {
#if !REAL_INSTALLER
        Assert(Boundaries.Calls == 0, $"Expected zero side-effect boundary calls, got {Boundaries.Calls}");
#endif
    }
}
