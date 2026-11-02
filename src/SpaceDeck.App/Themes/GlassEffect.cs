using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SpaceDeck.App.Themes;

/// <summary>
/// Frosted glass for the dashboard and sidebar windows. When the current skin sets "Skin.Glass" to "True", the window
/// uses the skin's see-through "Skin.Overlay.Background" and Windows blurs whatever is behind it; with any other skin
/// it goes back to the solid "Skin.Background" and no blur. Follows skin changes live.
/// </summary>
/// <remarks>
/// Uses SetWindowCompositionAttribute (blur behind), which works on Windows 10 and 11 with layered
/// (AllowsTransparency) windows. If Windows refuses, the window is simply translucent without blur.
/// </remarks>
public static class GlassEffect
{
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;

    /// <summary>True when the current skin asks for glass.</summary>
    public static bool IsGlassSkin =>
        Application.Current?.TryFindResource("Skin.Glass") is string s && s.Equals("True", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps <paramref name="window"/>'s background and blur in step with the skin until it closes.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void Update() => Apply(window);

        SkinManager.SkinChanged += Update;
        window.Closed += (_, _) => SkinManager.SkinChanged -= Update;
        window.SourceInitialized += (_, _) => Update();
        Update();
    }

    private static void Apply(Window window)
    {
        var glass = IsGlassSkin && Application.Current?.TryFindResource("Skin.Overlay.Background") is not null;
        window.SetResourceReference(Control.BackgroundProperty, glass ? "Skin.Overlay.Background" : "Skin.Background");

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetBlur(hwnd, glass);
        }
    }

    private static void SetBlur(IntPtr hwnd, bool on)
    {
        var accent = new AccentPolicy { AccentState = on ? AccentEnableBlurBehind : AccentDisabled };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WindowCompositionAttributeData { Attribute = WcaAccentPolicy, Data = pointer, SizeOfData = size };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            // Very old Windows: no blur, the tint alone still works.
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
