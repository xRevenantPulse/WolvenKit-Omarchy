// Platform boundaries only; UpdateService and UpdateDialogViewModel are linked production sources.
namespace WolvenKit.App.Helpers { public static class DesktopBridgeHelper { public static bool IsRunningAsPackage() => false; } }
namespace WolvenKit.Common { public static class Constants { public const string AssemblyName = "fixture"; } }
namespace WolvenKit.Core { public static class CommonFunctions { public static Semver.SemVersion GetAssemblyVersion(string _) => Semver.SemVersion.Parse("10.0.0"); } }
namespace WolvenKit.Core.Interfaces { public interface ILoggerService { void Error(string message); void Error(System.Exception exception); } }
namespace WolvenKit.App.Services {
 public static class AppDataProbe { public static int Calls; }
 public enum UpdateAvailability { None, Major, Minor, Patch }
 public enum EUpdateChannel { Stable, Nightly }
 public interface ISettingsManager { EUpdateChannel UpdateChannel { get; } public static string GetAppData() { AppDataProbe.Calls++; return System.AppContext.BaseDirectory; } }
}
namespace WolvenKit.App.ViewModels.Shell { public class AppViewModel { } }
namespace WolvenKit.App.ViewModels.Dialogs { public abstract class DialogViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject { } }
