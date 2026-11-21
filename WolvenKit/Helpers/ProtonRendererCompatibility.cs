#nullable enable
using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;

namespace WolvenKit.Helpers;

/// <summary>
/// Explicit Proton workaround: opaque WPF window, swapchain, and a precomputed
/// viewcube avoid the unsupported D3DImage/Direct2D paths. No changes by default.
/// </summary>
internal static class ProtonRendererCompatibility
{
    private const string ViewCubeResource = "WolvenKit.Proton.ViewCube.png";

    internal static bool IsOptedIn(string? value) => value == "1";

    private static bool IsEnabled => IsOptedIn(Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT"));

    internal static void ApplyProcessSettings()
    {
        if (IsEnabled)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    // Call immediately after InitializeComponent, before the HWND is created.
    internal static void ConfigureWindow(Window window)
    {
        if (IsEnabled)
        {
            window.SetCurrentValue(Window.AllowsTransparencyProperty, false);
        }
    }

    // Call before the viewport's template/Loaded starts the render host.
    internal static void ConfigureViewport(Viewport3DX viewport, object? modalState = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        viewport.SetCurrentValue(Viewport3DX.ViewCubeTextureProperty,
            new TextureModel(Guid.NewGuid(), new EmbeddedViewCubeLoader()));
        viewport.SetCurrentValue(Viewport3DX.EnableSwapChainRenderingProperty, true);
        // Helix resumes IsRendering on visibility changes but does not dirty a
        // settled scene. Request a fresh frame for this native surface on return.
        // The viewport owns this static handler: no app-lifetime subscription,
        // dispatcher loop, or captured view; it also survives unload/reload.
        viewport.IsVisibleChanged += ViewportIsVisibleChanged;

        if (modalState is not null)
        {
            // Native swapchain HWNDs otherwise sit above the shell's embedded WPF
            // dialogs. Hidden retains layout and lets Helix hide its native host.
            // Style triggers restore the underlying visibility, not unconditional
            // Visible, so hidden tabs/parents and existing local values stay hidden.
            // Both real viewports have no local Visibility value or binding.
            // WPF bindings use weak INotifyPropertyChanged listeners; never attach
            // a view-capturing handler to the application-lifetime AppViewModel.
            var style = new Style(typeof(Viewport3DX), viewport.Style);
            foreach (var property in new[] { "IsDialogShown", "IsOverlayShown" })
            {
                var trigger = new DataTrigger
                {
                    Binding = new Binding(property) { Source = modalState, Mode = BindingMode.OneWay },
                    Value = true
                };
                trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden));
                style.Triggers.Add(trigger);
            }
            viewport.SetValue(FrameworkElement.StyleProperty, style);
        }
    }

    private static void ViewportIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            ((Viewport3DX)sender).InvalidateRender();
        }
    }

    private sealed class EmbeddedViewCubeLoader : ITextureInfoLoader
    {
        // A fresh resource stream per load supports concurrent viewports and
        // effects-manager recreation. Never keep a stream closed by a prior upload.
        public TextureInfo Load(Guid id) => new(
            typeof(ProtonRendererCompatibility).Assembly.GetManifestResourceStream(ViewCubeResource)
            ?? throw new InvalidOperationException($"Missing embedded viewcube: {ViewCubeResource}"));

        public void Complete(Guid id, TextureInfo info, bool succeeded) => info.Texture.Dispose();
    }
}
