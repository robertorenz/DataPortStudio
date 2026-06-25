using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using DataPortStudio.Behaviors;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>In-app user guide: renders the bundled HTML documentation in the current language.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        var lang = LocalizationManager.Instance.Language;
        Title = LocalizationManager.Instance["Help_Title"];

        // Open external (http/https) links — e.g. library homepages in the credits — in the system browser
        // instead of navigating away inside the guide.
        Web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess) Web.CoreWebView2.NavigationStarting += OnNavigationStarting;
        };

        // The attached property handles WebView2 initialization and renders the HTML once loaded.
        WebViewHtml.SetHtml(Web, HelpDocs.GetHtml(lang));
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return; // in-page anchors and the initial content navigate normally

        e.Cancel = true;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
