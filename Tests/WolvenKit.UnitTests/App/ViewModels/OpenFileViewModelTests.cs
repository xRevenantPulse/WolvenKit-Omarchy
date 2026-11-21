using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Moq;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.Dialogs;
using WolvenKit.Core.Interfaces;

namespace WolvenKit.UnitTests.App.ViewModels;

[TestClass]
public class OpenFileViewModelTests
{
    [DataTestMethod]
    [DataRow(false, "")]
    [DataRow(null, "")]
    [DataRow(true, "")]
    [DataRow(true, null)]
    [DataRow(true, "   ")]
    [DataRow(false, "selected.archive")]
    [DataRow(null, "selected.archive")]
    public async Task CanceledOrEmptySelectionReturnsNullBeforeCopying(bool? accepted, string? selectedName)
    {
        using var fixture = new Fixture(accepted, selectedName);
        fixture.ViewModel.FileName = "previous.archive";

        Assert.IsNull(await fixture.ViewModel.OpenFile());

        Assert.AreEqual(1, fixture.ViewModel.DialogCalls);
        Assert.AreEqual("previous.archive", fixture.ViewModel.FileName);
        Assert.AreEqual("keep me", File.ReadAllText(fixture.Sentinel));
        CollectionAssert.AreEquivalent(new[] { fixture.Sentinel }, Directory.GetFiles(fixture.ExtraDirectory));
        Assert.AreEqual("archive content", File.ReadAllText(fixture.Source));
        fixture.Settings.Verify(x => x.GetRED4GameRootDir(), Times.Never);
        fixture.Logger.Verify(x => x.Info("No file selected."), Times.Once);
    }

    [TestMethod]
    public async Task AcceptedSelectionCopiesArchiveAndReturnsItsBaseName()
    {
        using var fixture = new Fixture(true, "selected.archive");

        Assert.AreEqual("selected.archive", await fixture.ViewModel.OpenFile());

        Assert.AreEqual(fixture.Source, fixture.ViewModel.FileName);
        Assert.AreEqual("archive content", File.ReadAllText(Path.Combine(fixture.ExtraDirectory, "selected.archive")));
        Assert.AreEqual("archive content", File.ReadAllText(fixture.Source));
        Assert.AreEqual("keep me", File.ReadAllText(fixture.Sentinel));
        fixture.Logger.Verify(x => x.Info("No file selected."), Times.Never);
        fixture.Settings.Verify(x => x.GetRED4GameRootDir(), Times.Once);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WolvenKit_OpenFileTests", Guid.NewGuid().ToString("N"));
        internal string ExtraDirectory { get; }
        internal string Source { get; }
        internal string Sentinel { get; }
        internal Mock<ISettingsManager> Settings { get; } = new();
        internal Mock<ILoggerService> Logger { get; } = new();
        internal TestViewModel ViewModel { get; }

        internal Fixture(bool? accepted, string? selectedName)
        {
            ExtraDirectory = Path.Combine(_root, "extra");
            Directory.CreateDirectory(ExtraDirectory);
            Source = Path.Combine(_root, "selected.archive");
            File.WriteAllText(Source, "archive content");
            Sentinel = Path.Combine(ExtraDirectory, "sentinel.archive");
            File.WriteAllText(Sentinel, "keep me");
            Settings.SetupGet(x => x.ExtraModDirPath).Returns(ExtraDirectory);
            Settings.Setup(x => x.GetRED4GameRootDir()).Returns(Path.Combine(_root, "game"));
            var project = new Mock<IProjectManager>();
            project.SetupGet(x => x.IsProjectLoaded).Returns(true);
            var selectedPath = selectedName == "selected.archive" ? Source : selectedName;
            ViewModel = new TestViewModel(Settings.Object, project.Object, Logger.Object, accepted, selectedPath);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class TestViewModel(
        ISettingsManager settings, IProjectManager project, ILoggerService logger, bool? accepted, string? selectedPath)
        : OpenFileViewModel(settings, project, logger)
    {
        internal int DialogCalls { get; private set; }

        protected override bool? ShowFileDialog(OpenFileDialog dialog)
        {
            DialogCalls++;
            dialog.FileName = selectedPath ?? "";
            return accepted;
        }
    }
}
