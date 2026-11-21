using System;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Models.ProjectManagement.Project;
using WolvenKit.App.ViewModels.Shell;

namespace WolvenKit.UnitTests.App.ViewModels;

[TestClass]
public class AppViewModelImportArchiveTests
{
    [TestMethod]
    public void ProjectAndDialogTransitionsNotifyArchiveImportCommand()
    {
        // Deliberately bypass startup/DI/network work: only generated setters and the
        // command's pure CanExecute predicate are under test. Never execute import.
        var vm = (AppViewModel)RuntimeHelpers.GetUninitializedObject(typeof(AppViewModel));
        var first = new Cp77Project("first.cpmodproj", "first", "first");
        var second = new Cp77Project("second.cpmodproj", "second", "second");
        var command = vm.ImportArchiveCommand;
        var notifications = 0;
        command.CanExecuteChanged += (_, _) => notifications++;

        void Transition(Action change, bool enabled, int expectedNotifications)
        {
            change();
            Assert.AreEqual(expectedNotifications, notifications, "CanExecuteChanged count");
            Assert.AreEqual(enabled, command.CanExecute(null), "Archive import enablement");
        }

        Assert.IsFalse(command.CanExecute(null));
        Transition(() => vm.ActiveProject = first, true, 1); // create/open
        Transition(() => vm.ActiveProject = second, true, 2); // switch
        Transition(() => vm.ActiveProject = second, true, 2); // unchanged
        Transition(() => vm.IsDialogShown = true, false, 3);
        Transition(() => vm.IsDialogShown = true, false, 3); // unchanged
        Transition(() => vm.IsDialogShown = false, true, 4);
        Transition(() => vm.ActiveProject = null, false, 5); // close
        Transition(() => vm.IsDialogShown = true, false, 6);
        Transition(() => vm.ActiveProject = first, false, 7); // open during modal
        Transition(() => vm.IsDialogShown = false, true, 8);
    }
}
