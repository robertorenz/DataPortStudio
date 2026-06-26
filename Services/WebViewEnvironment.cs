using System.IO;
using Microsoft.Web.WebView2.Core;

namespace DataPortStudio.Services;

/// <summary>
/// Shared WebView2 environment with a user-data folder under %LOCALAPPDATA%
/// so the app works correctly when installed to Program Files (read-only to users).
/// </summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _task;

    public static Task<CoreWebView2Environment> GetAsync() =>
        _task ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataPortStudio", "WebView2"));
}
