using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace SkillStudio.App.Behaviors;

/// <summary>TextEditor.Text não é DependencyProperty; esta propriedade anexada faz a ponte para o MVVM.</summary>
public static class AvalonEditBinding
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(AvalonEditBinding),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.RegisterAttached(
        "Syntax", typeof(string), typeof(AvalonEditBinding), new PropertyMetadata(null, OnSyntaxChanged));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(AvalonEditBinding));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string? GetSyntax(DependencyObject d) => (string?)d.GetValue(SyntaxProperty);
    public static void SetSyntax(DependencyObject d, string? value) => d.SetValue(SyntaxProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;
        if (!(bool)editor.GetValue(IsHookedProperty))
        {
            editor.SetValue(IsHookedProperty, true);
            editor.TextChanged += (_, _) =>
            {
                if (GetText(editor) != editor.Text) SetText(editor, editor.Text);
            };
        }
        var text = e.NewValue as string ?? "";
        if (editor.Text != text)
        {
            var caret = Math.Min(editor.CaretOffset, text.Length);
            editor.Text = text;
            editor.CaretOffset = caret;
        }
    }

    private static void OnSyntaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor && e.NewValue is string name)
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(name);
    }
}
