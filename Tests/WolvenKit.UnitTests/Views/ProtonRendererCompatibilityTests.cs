using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.Helpers;

namespace WolvenKit.UnitTests.Views;

[TestClass]
[DoNotParallelize] // Environment and ProcessRenderMode are process-wide.
public class ProtonRendererCompatibilityTests
{
    [TestMethod]
    public void OptInRequiresExactlyOne()
    {
        foreach (var value in new string?[] { null, "", "0", "true", "TRUE", "01", " 1", "1 ", "1\n" })
        {
            Assert.IsFalse(ProtonRendererCompatibility.IsOptedIn(value), $"Unexpected opt-in: '{value}'");
        }
        Assert.IsTrue(ProtonRendererCompatibility.IsOptedIn("1"));
    }

    [TestMethod]
    public void DisabledModePreservesWpfAndViewportProperties() => RunSta(() =>
    {
        foreach (var value in new string?[] { null, "0", "true", " 1", "1 " })
        {
            WithEnvironment(value, () =>
            {
                var originalRenderMode = RenderOptions.ProcessRenderMode;
                var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true };
                var viewport = new Viewport3DX(); // Never templated: no render host to dispose.
                var originalSwapchain = viewport.EnableSwapChainRendering;
                var originalTexture = viewport.ViewCubeTexture;
                ProtonRendererCompatibility.ApplyProcessSettings();
                ProtonRendererCompatibility.ConfigureWindow(window);
                ProtonRendererCompatibility.ConfigureViewport(viewport);
                Assert.AreEqual(originalRenderMode, RenderOptions.ProcessRenderMode);
                Assert.IsTrue(window.AllowsTransparency);
                Assert.AreEqual(originalSwapchain, viewport.EnableSwapChainRendering);
                Assert.AreSame(originalTexture, viewport.ViewCubeTexture);
                Assert.IsNull(PresentationSource.FromVisual(window));
            });
        }
    });

    [TestMethod]
    public void EnabledModeConfiguresRealPropertiesBeforeNativeWindowCreation() => RunSta(() =>
        WithEnvironment("1", () =>
        {
            var originalRenderMode = RenderOptions.ProcessRenderMode;
            try
            {
                var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true };
                var viewport = new Viewport3DX(); // Never templated: no render host to dispose.
                ProtonRendererCompatibility.ApplyProcessSettings();
                ProtonRendererCompatibility.ConfigureWindow(window);
                ProtonRendererCompatibility.ConfigureViewport(viewport);
                Assert.AreEqual(RenderMode.SoftwareOnly, RenderOptions.ProcessRenderMode);
                Assert.IsFalse(window.AllowsTransparency);
                Assert.IsNull(PresentationSource.FromVisual(window));
                Assert.IsTrue(viewport.EnableSwapChainRendering);
                Assert.IsNotNull(viewport.ViewCubeTexture);
                Assert.IsFalse(viewport.IsLoaded);
                Assert.IsNull(viewport.RenderHost);
            }
            finally
            {
                RenderOptions.ProcessRenderMode = originalRenderMode;
            }
        }));

    [TestMethod]
    public void EmbeddedTextureCanReloadAfterSuccessFailureAndConcurrentLoads() => RunSta(() =>
        WithEnvironment("1", () =>
        {
            var first = new Viewport3DX();
            var second = new Viewport3DX();
            ProtonRendererCompatibility.ConfigureViewport(first);
            ProtonRendererCompatibility.ConfigureViewport(second);
            Assert.IsNotNull(first.ViewCubeTexture);
            Assert.IsNotNull(second.ViewCubeTexture);
            var a = first.ViewCubeTexture.Load();
            var b = second.ViewCubeTexture.Load();
            Assert.AreNotSame(a.Texture, b.Texture);
            Assert.AreEqual(0L, a.Texture.Position);
            Assert.AreEqual(0L, b.Texture.Position);
            var bitmap = BitmapFrame.Create(a.Texture, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.AreEqual(600, bitmap.PixelWidth);
            Assert.AreEqual(100, bitmap.PixelHeight);
            first.ViewCubeTexture.Complete(a, true);
            second.ViewCubeTexture.Complete(b, false);
            Assert.IsFalse(a.Texture.CanRead);
            Assert.IsFalse(b.Texture.CanRead);
            var retry = first.ViewCubeTexture.Load();
            Assert.IsTrue(retry.Texture.CanRead);
            Assert.AreEqual(0L, retry.Texture.Position);
            first.ViewCubeTexture.Complete(retry, true);
        }));

    [TestMethod]
    public void ModalStateHidesViewportUntilBothFlagsClear() => RunSta(() =>
        WithEnvironment("1", () =>
        {
            var state = new ModalState();
            var viewport = new Viewport3DX();
            ConfigureWithModalState(viewport, state);
            Assert.AreEqual(Visibility.Visible, viewport.Visibility);
            state.IsDialogShown = true;
            Assert.AreEqual(Visibility.Hidden, viewport.Visibility, "Dialog must hide native airspace without collapsing layout");
            state.IsOverlayShown = true;
            state.IsDialogShown = false;
            Assert.AreEqual(Visibility.Hidden, viewport.Visibility, "Overlay must keep native airspace hidden");
            state.IsOverlayShown = false;
            Assert.AreEqual(Visibility.Visible, viewport.Visibility);
            state.IsOverlayShown = true;
            Assert.AreEqual(Visibility.Hidden, viewport.Visibility);
            state.IsOverlayShown = false;
            Assert.AreEqual(Visibility.Visible, viewport.Visibility);
        }));

    [TestMethod]
    public void ModalGuardAppliesInitialStateAndIndependentViewports() => RunSta(() =>
        WithEnvironment("1", () =>
        {
            var state = new ModalState { IsOverlayShown = true };
            var quick = new Viewport3DX();
            var document = new Viewport3DX();
            ConfigureWithModalState(quick, state);
            ConfigureWithModalState(document, state);
            Assert.AreEqual(Visibility.Hidden, quick.Visibility);
            Assert.AreEqual(Visibility.Hidden, document.Visibility);
            state.IsOverlayShown = false;
            Assert.AreEqual(Visibility.Visible, quick.Visibility);
            Assert.AreEqual(Visibility.Visible, document.Visibility);
        }));

    [TestMethod]
    public void DisabledModalGuardLeavesVisibilityAndStyleUntouched() => RunSta(() =>
        WithEnvironment("0", () =>
        {
            var state = new ModalState { IsDialogShown = true, IsOverlayShown = true };
            var viewport = new Viewport3DX();
            var originalStyle = viewport.Style;
            ConfigureWithModalState(viewport, state);
            Assert.AreSame(originalStyle, viewport.Style);
            Assert.AreEqual(Visibility.Visible, viewport.Visibility);
            state.IsDialogShown = false;
            state.IsOverlayShown = false;
            Assert.AreEqual(Visibility.Visible, viewport.Visibility);
            Assert.IsFalse(System.Windows.Data.BindingOperations.IsDataBound(viewport, UIElement.VisibilityProperty));
        }));

    [TestMethod]
    public void ModalGuardPreservesParentAndLocalVisibility() => RunSta(() =>
        WithEnvironment("1", () =>
        {
            var state = new ModalState();
            var viewport = new Viewport3DX();
            var parent = new System.Windows.Controls.Grid { Visibility = Visibility.Collapsed };
            parent.Children.Add(viewport);
            ConfigureWithModalState(viewport, state);
            state.IsDialogShown = true;
            state.IsDialogShown = false;
            Assert.AreEqual(Visibility.Collapsed, parent.Visibility);
            Assert.IsFalse(viewport.IsVisible, "Closing modal must not activate a hidden parent/tab");
            viewport.Visibility = Visibility.Collapsed;
            state.IsOverlayShown = true;
            state.IsOverlayShown = false;
            Assert.AreEqual(Visibility.Collapsed, viewport.Visibility);
        }));

    [TestMethod]
    public void VisibilityRestorationInvalidatesOnlyItsOwnRenderHost() => RunSta(() =>
    {
        // Method seam only: no template, HWND, GPU, or real modal is created.
        // Exercise the real Viewport3DX.InvalidateRender forwarding method using
        // recording IRenderHost proxies, not a replacement viewport implementation.
        var handler = typeof(ProtonRendererCompatibility).GetMethod("ViewportIsVisibleChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(handler, "Restored native surfaces need a fresh render invalidation");
        var hostField = typeof(Viewport3DX).GetField("renderHostInternal",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(hostField, "Version-pinned Helix host seam changed");
        var quick = new Viewport3DX();
        var document = new Viewport3DX();
        var quickHost = System.Reflection.DispatchProxy.Create<HelixToolkit.SharpDX.Core.IRenderHost, RecordingRenderHost>();
        var documentHost = System.Reflection.DispatchProxy.Create<HelixToolkit.SharpDX.Core.IRenderHost, RecordingRenderHost>();
        var quickRecord = (RecordingRenderHost)(object)quickHost;
        var documentRecord = (RecordingRenderHost)(object)documentHost;
        hostField.SetValue(quick, quickHost);
        hostField.SetValue(document, documentHost);
        void Notify(Viewport3DX viewport, bool visible) => handler.Invoke(null,
            new object[] { viewport, new DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, !visible, visible) });
        try
        {
            Notify(quick, false);
            Notify(document, false);
            Assert.AreEqual(0, quickRecord.Invalidations);
            Assert.AreEqual(0, documentRecord.Invalidations);
            Notify(quick, true);
            Assert.AreEqual(1, quickRecord.Invalidations);
            Assert.AreEqual(0, documentRecord.Invalidations, "Each restored viewport needs its own frame");
            Notify(document, true);
            Assert.AreEqual(1, documentRecord.Invalidations);
            Notify(quick, false);
            Notify(quick, true);
            Assert.AreEqual(2, quickRecord.Invalidations, "Repeated modal cycles must remain subscribed");
            Assert.AreEqual(1, documentRecord.Invalidations);
            quickRecord.Failure = new InvalidOperationException("render failure must propagate");
            var failure = Assert.ThrowsException<System.Reflection.TargetInvocationException>(() => Notify(quick, true));
            Assert.AreSame(quickRecord.Failure, failure.InnerException);
        }
        finally
        {
            hostField.SetValue(quick, null);
            hostField.SetValue(document, null);
        }
        Notify(quick, true); // Before Loaded/after host detachment is a safe no-op in Helix.
    });

    public class RecordingRenderHost : System.Reflection.DispatchProxy
    {
        public int Invalidations { get; private set; }
        public Exception? Failure { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "InvalidateRender")
                throw new InvalidOperationException($"Unexpected render-host call: {method?.Name}");
            if (Failure is not null) throw Failure;
            Invalidations++;
            return null;
        }
    }

    // Reflection lets the exact same regression source compile against the old
    // helper: the RED binary then fails assertions, not missing-method compilation.
    private static void ConfigureWithModalState(Viewport3DX viewport, ModalState state)
    {
        var method = typeof(ProtonRendererCompatibility).GetMethod("ConfigureViewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            null, new[] { typeof(Viewport3DX), typeof(object) }, null);
        if (method is null) ProtonRendererCompatibility.ConfigureViewport(viewport);
        else method.Invoke(null, new object[] { viewport, state });
    }

    public sealed class ModalState : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _dialog;
        private bool _overlay;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public bool IsDialogShown
        {
            get => _dialog;
            set { _dialog = value; PropertyChanged?.Invoke(this, new(nameof(IsDialogShown))); }
        }
        public bool IsOverlayShown
        {
            get => _overlay;
            set { _overlay = value; PropertyChanged?.Invoke(this, new(nameof(IsOverlayShown))); }
        }
    }

    private static void WithEnvironment(string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT");
        try
        {
            Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", original);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "STA property test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
