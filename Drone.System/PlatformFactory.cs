using Drone.Core;

namespace Drone.System;

/// <summary>
/// Creates platform-specific implementations of system interfaces.
/// </summary>
public static class PlatformFactory
{
    public static IScreenCapture CreateScreenCapture(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new Windows.Win32ScreenCapture(logger);
        if (OperatingSystem.IsLinux())
            return new Linux.X11ScreenCapture(logger);
        if (OperatingSystem.IsMacOS())
            return new MacOS.CoreGraphicsScreenCapture(logger);
        throw new PlatformNotSupportedException("Screen capture not supported on this platform.");
    }

    public static IInputSimulator CreateInputSimulator(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new Windows.Win32InputSimulator(logger);
        if (OperatingSystem.IsLinux())
            return new Linux.X11InputSimulator(logger);
        if (OperatingSystem.IsMacOS())
            return new MacOS.CoreGraphicsInputSimulator(logger);
        throw new PlatformNotSupportedException("Input simulation not supported on this platform.");
    }

    public static IProcessManager CreateProcessManager(ILogger logger)
    {
        return new CrossPlatformProcessManager(logger);
    }

    public static IClipboardManager CreateClipboardManager(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new Windows.Win32ClipboardManager(logger);
        return new CrossPlatformClipboardManager(logger);
    }

    public static IWindowManager CreateWindowManager(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new Windows.Win32WindowManager(logger);
        if (OperatingSystem.IsLinux())
            return new Linux.X11WindowManager(logger);
        if (OperatingSystem.IsMacOS())
            return new MacOS.CoreGraphicsWindowManager(logger);
        throw new PlatformNotSupportedException("Window management not supported on this platform.");
    }
}
