using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;

namespace Launcher.App.Controls;

/// <summary>
/// TextBox tailored for Minecraft usernames — restricts input to ASCII
/// alphanumeric + underscore and disables IME composition while focused so a
/// Korean/Japanese/Chinese IME cannot inject composition characters.
///
/// IME is scoped to this control (focus in/out) instead of process-wide so
/// every *other* TextBox in the app (new instance name, JVM args, etc.) still
/// accepts Hangul / kanji / pinyin normally.
/// </summary>
public class UsernameTextBox : TextBox
{
    [DllImport("imm32.dll")]
    private static extern nint ImmAssociateContext(nint hWnd, nint hIMC);

    // Inherit TextBox's FluentTheme styling. Without this, the FluentTheme's
    // `Style Selector="TextBox"` rule (exact-type match) does not apply to this
    // subclass and the control renders without any chrome.
    protected override Type StyleKeyOverride => typeof(TextBox);

    private nint _savedImc = nint.Zero;
    private nint _windowHandle = nint.Zero;

    public UsernameTextBox()
    {
        GotFocus += OnGotFocusHandler;
        LostFocus += OnLostFocusHandler;
    }

    private void OnGotFocusHandler(object? sender, FocusChangedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        var topLevel = TopLevel.GetTopLevel(this);
        var handle = topLevel?.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "HWND") return;

        _windowHandle = handle.Handle;
        // ImmAssociateContext returns the previously associated IME context so
        // we can put it back when focus leaves us.
        _savedImc = ImmAssociateContext(_windowHandle, nint.Zero);
    }

    private void OnLostFocusHandler(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_windowHandle == nint.Zero || _savedImc == nint.Zero) return;

        ImmAssociateContext(_windowHandle, _savedImc);
        _windowHandle = nint.Zero;
        _savedImc = nint.Zero;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 })
        {
            foreach (var c in e.Text)
            {
                if (!IsValid(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnTextInput(e);
    }

    private static bool IsValid(char c) =>
        (c is >= 'A' and <= 'Z')
        || (c is >= 'a' and <= 'z')
        || (c is >= '0' and <= '9')
        || c == '_';
}
