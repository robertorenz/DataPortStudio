using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace DataPortStudio.Behaviors;

/// <summary>
/// Attached property that renders an HTML string into a WebView2, initializing the
/// core lazily and deferring until the control is loaded. Fails quietly if the
/// WebView2 runtime is unavailable.
/// </summary>
public static class WebViewHtml
{
    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached(
            "Html", typeof(string), typeof(WebViewHtml),
            new PropertyMetadata(null, OnHtmlChanged));

    public static string? GetHtml(DependencyObject o) => (string?)o.GetValue(HtmlProperty);
    public static void SetHtml(DependencyObject o, string? value) => o.SetValue(HtmlProperty, value);

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WebView2 webView)
            _ = RenderAsync(webView, e.NewValue as string ?? "");
    }

    private static async Task RenderAsync(WebView2 webView, string html)
    {
        try
        {
            // Don't spin up the WebView2 core unless there's actual HTML to show.
            if (string.IsNullOrEmpty(html))
            {
                if (webView.CoreWebView2 is not null)
                    webView.NavigateToString("<html><body></body></html>");
                return;
            }

            if (!webView.IsLoaded)
            {
                void OnLoaded(object? s, RoutedEventArgs a)
                {
                    webView.Loaded -= OnLoaded;
                    _ = RenderAsync(webView, GetHtml(webView) ?? "");
                }
                webView.Loaded += OnLoaded;
                return;
            }

            await webView.EnsureCoreWebView2Async();
            webView.NavigateToString(html);
        }
        catch
        {
            // WebView2 runtime missing or navigation failed — leave the control blank.
        }
    }
}
