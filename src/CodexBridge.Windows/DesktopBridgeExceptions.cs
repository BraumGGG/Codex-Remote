namespace CodexBridge.Windows;

public sealed class DesktopUnavailableException : InvalidOperationException
{
    public DesktopUnavailableException(string message) : base(message)
    {
    }
}

public sealed class InteractiveSessionUnavailableException : InvalidOperationException
{
    public InteractiveSessionUnavailableException(string message) : base(message)
    {
    }
}

public sealed class DesktopVersionUnsupportedException : InvalidOperationException
{
    public DesktopVersionUnsupportedException(string message) : base(message)
    {
    }
}
