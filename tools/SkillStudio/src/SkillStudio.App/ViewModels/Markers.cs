using System.Globalization;
using System.Windows.Data;

namespace SkillStudio.App.ViewModels;

/// <summary>Conversores pequenos usados direto no XAML via x:Static.</summary>
public static class Markers
{
    public static IValueConverter Not { get; } = new NotConverter();
    public static IValueConverter Dirty { get; } = new DirtyConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
    }

    private sealed class DirtyConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? " •" : "";
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }
}
