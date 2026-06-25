using System.Windows.Data;
using System.Windows.Markup;

namespace DataPortStudio.Services;

/// <summary>
/// XAML markup extension that yields a live, language-aware string:
/// <c>Text="{loc:Tr Btn_Save}"</c>. Internally binds to
/// <see cref="LocalizationManager"/>'s indexer so values refresh when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
