using System.Net.Http;
using System.Reflection;
using WolvenKit.App.Services;
using WolvenKit.Core.Interfaces;

var handler = new Tripwire();
var service = new UpdateService(new Logger(), new Settings());
// Existing private transport replaced BEFORE invoking any updater method. Never a real request.
typeof(UpdateService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, new HttpClient(handler));
try { await service.UpdateToNewestVersion(); throw new Exception("Install did not reject manual-only policy"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("manual", StringComparison.OrdinalIgnoreCase)) { }
if (handler.Calls != 0) throw new Exception($"Policy reached HTTP {handler.Calls} time(s)");
Console.WriteLine("PASS direct install rejects before HTTP/files/process/exit");
if (args.Contains("checks")) {
 if (await service.IsUpdateAvailable() != UpdateAvailability.None) throw new Exception("Startup must not offer an install");
 try { await service.GetLatestVersionTag(); throw new Exception("Tag lookup must explicitly reject manual-only checks"); }
 catch (InvalidOperationException ex) when (ex.Message.Contains("manual", StringComparison.OrdinalIgnoreCase)) { }
 if (await service.GetRemoteChangelog("10.0.0", "99.0.0") != null) throw new Exception("Disabled changelog lookup");
 if (await service.IsUpdateAvailable(new WolvenKit.App.Models.MinimalGithubRelease { TagName = "invalid" }) != UpdateAvailability.None) throw new Exception("Supplied metadata bypassed policy");
 if (handler.Calls != 0) throw new Exception("Binary metadata reached HTTP");
 Console.WriteLine("PASS startup availability/tag/changelog never use HTTP");
}
if (args.Contains("constructor") && AppDataProbe.Calls != 0) throw new Exception("Updater construction touched app-data filesystem before policy denial");
if (args.Contains("ui")) {
 var fake = new UiService();
 var vm = new WolvenKit.App.ViewModels.Dialogs.UpdateDialogViewModel(null!, fake, new Settings(), new Logger(), true, UpdateAvailability.Minor);
 void AssertManual() {
  if (!vm.Title.Contains("manual", StringComparison.OrdinalIgnoreCase) || !vm.Body.Contains("xRevenantPulse/WolvenKit-Omarchy") || vm.Body.Contains("already on the latest") || vm.Buttons.Count != 1 || vm.Buttons[0] != "OK" || vm.ShowLoadingSpinner || vm.AskForPermissionState || vm.UpdateExecutingState || fake.Calls != 0)
   throw new Exception("UI must describe manual downstream updates without service calls/Update/Retry: " + vm.Title);
 }
 AssertManual();
 vm.SetDialogToNoUpdateAvailableState(); AssertManual();
 await vm.SetDialogToAskForPermissionState(); AssertManual();
 vm.SetDialogToUpdateExecutingState(); AssertManual();
 Console.WriteLine("PASS manual/skip-permission/no-update/permission/retry UI are manual-only");
}
if (args.Contains("matrix")) {
 var previous = Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT");
 var cases = 0;
 try {
  foreach (var compat in new[] { "0", "1", null })
  foreach (var channel in new[] { EUpdateChannel.Stable, EUpdateChannel.Nightly, (EUpdateChannel)12345 })
  foreach (var startup in new[] { false, true })
  foreach (var ask in new[] { false, true }) {
   Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", compat);
   var settings = new Settings(channel, startup, ask);
   var boundary = new Tripwire();
   var instance = new UpdateService(new Logger(), settings);
   using var transport = new HttpClient(boundary);
   typeof(UpdateService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, transport);
   try { await instance.UpdateToNewestVersion(); throw new Exception("Matrix install not denied"); }
   catch (InvalidOperationException ex) when (ex.Message.Contains("manual binary updates")) { }
   if (await instance.IsUpdateAvailable() != UpdateAvailability.None || boundary.Calls != 0 || settings.Reads != 0) throw new Exception("Persisted preference/compatibility bypass");
   foreach (var skip in new[] { false, true }) {
    var fake = new UiService();
    var vm = new WolvenKit.App.ViewModels.Dialogs.UpdateDialogViewModel(null!, fake, settings, new Logger(), skip, UpdateAvailability.Patch);
    if (vm.Title != ForkBinaryUpdatePolicy.Title || fake.Calls != 0 || vm.Buttons.Count != 1 || vm.Buttons[0] != "OK") throw new Exception("Matrix UI bypass");
   }
   cases++;
  }
 } finally { Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", previous); }
 if (AppDataProbe.Calls != 0) throw new Exception("Denied matrix touched app-data path");
 Console.WriteLine($"PASS {cases} channel/startup/ask/Proton-flag combinations, each with both permission modes; zero HTTP/settings/app-data calls");
}
sealed class UiService : IUpdateService {
 public int Calls;
 public Task<string> GetLatestVersionTag() { Calls++; return Task.FromResult("99.0.0"); }
 public Task<UpdateAvailability> IsUpdateAvailable(WolvenKit.App.Models.MinimalGithubRelease? release = null) { Calls++; return Task.FromResult(UpdateAvailability.Minor); }
 public Task UpdateToNewestVersion() { Calls++; return Task.CompletedTask; }
 public Semver.SemVersion GetLocalVersion() { Calls++; return Semver.SemVersion.Parse("10.0.0"); }
 public string? GetSavedChangelog() => null;
 public void ClearSavedChangelog() { }
 public Task<string?> GetRemoteChangelog(string a, string b) { Calls++; return Task.FromResult<string?>(null); }
}
sealed class Tripwire : HttpMessageHandler {
 public int Calls;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
  Calls++; throw new Exception("HTTP_TRIPWIRE: original upstream binary updater reached network boundary: " + request.RequestUri);
 }
}
sealed class Settings(EUpdateChannel channel = EUpdateChannel.Stable, bool startup = true, bool ask = false) : ISettingsManager {
 public int Reads;
 public EUpdateChannel UpdateChannel { get { Reads++; return channel; } }
 public bool AutoUpdateOnStartup { get { Reads++; return startup; } }
 public bool AlwaysAskBeforeUpdating { get { Reads++; return ask; } }
}
sealed class Logger : ILoggerService { public void Error(string _) {} public void Error(Exception _) {} }
