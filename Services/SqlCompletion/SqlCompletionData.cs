using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace DataPortStudio.Services.SqlCompletion;

public enum CompletionKind { Keyword, Table, Column, Function }

public class SqlCompletionData(string text, CompletionKind kind, string? description = null) : ICompletionData
{
    public string Text { get; } = text;
    public CompletionKind Kind { get; } = kind;

    public object Content => Text;

    public object Description => description ?? KindLabel;

    public double Priority => Kind switch
    {
        CompletionKind.Column  => 3,
        CompletionKind.Table   => 2,
        CompletionKind.Function => 1,
        _                      => 0,
    };

    public ImageSource? Image => null;

    private string KindLabel => Kind switch
    {
        CompletionKind.Keyword  => "Keyword",
        CompletionKind.Table    => "Table",
        CompletionKind.Column   => "Column",
        CompletionKind.Function => "Function",
        _ => ""
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs e)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
