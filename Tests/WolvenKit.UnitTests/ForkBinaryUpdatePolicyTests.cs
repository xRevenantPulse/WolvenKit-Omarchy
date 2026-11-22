using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using WolvenKit.App;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.Dialogs;
using WolvenKit.Common;
using WolvenKit.Core.Interfaces;
using Xunit;

namespace WolvenKit.UnitTests;

public class ForkBinaryUpdatePolicyTests
{
    [Theory]
    [InlineData(EUpdateChannel.Stable, false, false)]
    [InlineData(EUpdateChannel.Stable, true, false)]
    [InlineData(EUpdateChannel.Stable, true, true)]
    [InlineData(EUpdateChannel.Nightly, false, false)]
    [InlineData(EUpdateChannel.Nightly, true, false)]
    [InlineData(EUpdateChannel.Nightly, true, true)]
    [InlineData((EUpdateChannel)12345, true, false)]
    public async Task ServiceRejectsInstallationAndMetadataBeforeNetwork(EUpdateChannel channel, bool startup, bool ask)
    {
        var settings = new Mock<ISettingsManager>();
        settings.SetupGet(x => x.UpdateChannel).Returns(channel);
        settings.SetupGet(x => x.AutoUpdateOnStartup).Returns(startup);
        settings.SetupGet(x => x.AlwaysAskBeforeUpdating).Returns(ask);
        var handler = new Tripwire();
        using var client = new HttpClient(handler);
        var service = new UpdateService(Mock.Of<ILoggerService>(), settings.Object);
        // Inject the existing transport without changing DI registration or creating any real request.
        typeof(UpdateService).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);

        var denial = await Assert.ThrowsAsync<InvalidOperationException>(service.UpdateToNewestVersion);
        Assert.Contains("manual binary updates", denial.Message);
        Assert.Equal(UpdateAvailability.None, await service.IsUpdateAvailable());
        Assert.Equal(UpdateAvailability.None, await service.IsUpdateAvailable(new MinimalGithubRelease { TagName = "invalid-version" }));
        await Assert.ThrowsAsync<InvalidOperationException>(service.GetLatestVersionTag);
        Assert.Null(await service.GetRemoteChangelog("invalid", "invalid"));
        Assert.Equal(0, handler.Calls);
        // Policy precedes even reading persisted channel/automatic-install preferences.
        settings.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, UpdateAvailability.None)]
    [InlineData(true, UpdateAvailability.Minor)]
    [InlineData(true, UpdateAvailability.Major)]
    public async Task EveryDialogEntryShowsManualPolicyWithoutInvokingService(bool skipPermission, UpdateAvailability availability)
    {
        var service = new Mock<IUpdateService>(MockBehavior.Strict);
        var settings = new Mock<ISettingsManager>(MockBehavior.Strict);
        var vm = new UpdateDialogViewModel(null!, service.Object, settings.Object, Mock.Of<ILoggerService>(), skipPermission, availability);
        AssertManual(vm);
        vm.SetDialogToNoUpdateAvailableState();
        AssertManual(vm);
        await vm.SetDialogToAskForPermissionState();
        AssertManual(vm);
        vm.SetDialogToUpdateExecutingState(); // Retry uses this same entry.
        AssertManual(vm);
        service.VerifyNoOtherCalls();
        settings.VerifyNoOtherCalls();
    }

    private static void AssertManual(UpdateDialogViewModel vm)
    {
        Assert.Equal(ForkBinaryUpdatePolicy.Title, vm.Title);
        Assert.Equal(ForkBinaryUpdatePolicy.Message, vm.Body);
        Assert.DoesNotContain("already on the latest", vm.Body);
        Assert.Equal(new[] { "OK" }, vm.Buttons);
        Assert.True(vm.NoUpdateAvailableState);
        Assert.False(vm.AskForPermissionState);
        Assert.False(vm.UpdateExecutingState);
        Assert.False(vm.ShowLoadingSpinner);
    }

    private sealed class Tripwire : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("HTTP_TRIPWIRE: no real requests are permitted in policy tests");
        }
    }
}
