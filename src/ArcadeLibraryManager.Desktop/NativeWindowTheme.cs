using System.Runtime.InteropServices;

namespace ArcadeLibraryManager.Desktop;

internal static class NativeWindowTheme
{
    // Caption colors are supported from Windows 11 build 22000. Keep the native
    // window frame and treat unsupported styling as a normal system fallback.
    // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    internal static void ApplyBlackCaption(IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemInformation.HighContrast) return;
        SetAttribute(handle, 20, 1);          // DWMWA_USE_IMMERSIVE_DARK_MODE
        SetAttribute(handle, 35, 0x000000);   // DWMWA_CAPTION_COLOR: black COLORREF
        SetAttribute(handle, 36, 0xFFFFFF);   // DWMWA_TEXT_COLOR: white COLORREF
    }

    private static void SetAttribute(IntPtr handle, int attribute, uint value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
