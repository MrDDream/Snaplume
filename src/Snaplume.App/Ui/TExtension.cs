using Avalonia.Markup.Xaml;
using Snaplume.Core;

namespace Snaplume.Ui;

/// <summary>XAML: Text="{l:T Editor.Save}" with xmlns:l="using:Snaplume.Ui". The language is fixed at startup.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
