using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SkillStudio.Core.Validation;
using Wpf.Ui.Controls;

namespace SkillStudio.App.Converters;

public sealed class SeverityToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Error => SymbolRegular.ErrorCircle24,
        Severity.Warning => SymbolRegular.Warning24,
        _ => SymbolRegular.Info24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Severity.Error => "SystemFillColorCriticalBrush",
            Severity.Warning => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (value is int i) b = i > 0;
        if (value is string s) b = s.Length > 0;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nome de um recurso de pincel (ex.: SystemFillColorSuccessBrush) → o pincel do tema atual.</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && Application.Current.TryFindResource(key) is Brush brush ? brush : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Rótulos em português para os enums do Playground.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Core.Evals.EvalKind.Behavior => "Comportamento",
        Core.Evals.EvalKind.Trigger => "Gatilho",
        Core.Evals.AssertionKind.Rubric => "Rubrica (juiz)",
        Core.Evals.AssertionKind.Contains => "Contém",
        Core.Evals.AssertionKind.NotContains => "Não contém",
        Core.Evals.AssertionKind.Regex => "Regex",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>ConverterParameter="invert" mostra quando o valor é nulo.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is null) ^ (parameter as string == "invert") ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
