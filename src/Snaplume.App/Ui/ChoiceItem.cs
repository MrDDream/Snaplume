namespace Snaplume.Ui;

/// <summary>
/// Item of a drop-down list: the stored value and the text shown. Not generic, so that compiled XAML bindings
/// (DisplayMemberBinding="{Binding Label, x:DataType=l:ChoiceItem}") can name it.
/// </summary>
public sealed record ChoiceItem(object Value, string Label)
{
    public override string ToString() => Label;
}
