using System.Windows.Forms;
using MashiroLauncher.Core.Common;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MashiroLauncher.App.Services;

/// <summary>
/// Pops up an embedded WebView2 window that drives the Microsoft v2.0 authorize
/// URL and intercepts the navigation to the registered redirect URI (the
/// nativeclient page) the instant Microsoft tries to reach it. The intercepted
/// URL is returned (containing ?code=) so the caller can exchange it — with the
/// PKCE verifier — and complete the Xbox/XSTS/Minecraft chain.
///
/// Returns null if the user closes the window without completing sign-in.
/// </summary>
internal static class WebViewLoginHelper
{
    public static Task<string?> ShowAsync(string authorizeUrl, string redirectUriPrefix)
    {
        var tcs = new TaskCompletionSource<string?>();

        // WebView2 / WinForms require an STA thread with its own message pump.
        // Avalonia owns the main UI thread, so spin up a dedicated one.
        var thread = new Thread(() =>
        {
            Form? form = null;
            try
            {
                form = new Form
                {
                    Width = 480,
                    Height = 720,
                    Text = "Microsoft 로그인 — Mashiro Launcher",
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = true,
                    TopMost = true,
                };

                var webView = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(webView);

                bool intercepted = false;

                webView.NavigationStarting += (_, e) =>
                {
                    // The redirect to the nativeclient URI carries the
                    // authorization code (or an error) in its query. Grab it and
                    // cancel the navigation before the blank page even renders.
                    if (e.Uri.StartsWith(redirectUriPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        intercepted = true;
                        e.Cancel = true;
                        tcs.TrySetResult(e.Uri);
                        try { form?.BeginInvoke(() => form?.Close()); } catch { }
                    }
                };

                webView.CoreWebView2InitializationCompleted += (_, e) =>
                {
                    if (e.IsSuccess)
                    {
                        try { webView.CoreWebView2.Navigate(authorizeUrl); }
                        catch (Exception ex) { tcs.TrySetException(ex); form?.BeginInvoke(() => form?.Close()); }
                    }
                    else
                    {
                        tcs.TrySetException(
                            e.InitializationException
                            ?? new InvalidOperationException("WebView2 초기화 실패. Microsoft Edge WebView2 Runtime이 설치되어 있는지 확인해 주세요."));
                        form?.BeginInvoke(() => form?.Close());
                    }
                };

                form.FormClosed += (_, _) =>
                {
                    // If we close without having intercepted a redirect, the user cancelled.
                    if (!intercepted) tcs.TrySetResult(null);
                };

                // Use our own user-data folder under data/ so we don't litter
                // %LOCALAPPDATA% and we can clear sessions by deleting it.
                _ = InitWebView2Async(webView);

                Application.Run(form);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                form?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static async Task InitWebView2Async(WebView2 webView)
    {
        try
        {
            var userDataFolder = Path.Combine(Paths.Data, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await webView.EnsureCoreWebView2Async(env);
        }
        catch
        {
            // Surfaced via CoreWebView2InitializationCompleted with IsSuccess=false.
        }
    }
}
