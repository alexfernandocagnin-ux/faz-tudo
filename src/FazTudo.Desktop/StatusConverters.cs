using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FazTudo.Core.Models;

namespace FazTudo.Desktop;

internal static class Palette
{
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Danger = Brush("#DC2626");
    public static readonly SolidColorBrush DangerSoft = Brush("#FEF2F2");
    public static readonly SolidColorBrush Warning = Brush("#B45309");
    public static readonly SolidColorBrush WarningSoft = Brush("#FFFBEB");
    public static readonly SolidColorBrush Success = Brush("#15803D");
    public static readonly SolidColorBrush SuccessSoft = Brush("#F0FDF4");
    public static readonly SolidColorBrush Violet = Brush("#7C3AED");
    public static readonly SolidColorBrush VioletSoft = Brush("#F5F3FF");
    public static readonly SolidColorBrush Neutral = Brush("#475569");
    public static readonly SolidColorBrush NeutralSoft = Brush("#F1F5F9");
}

/// <summary>Cor principal para severidade de erro ou status de checagem.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckHealthStatus.Healthy => Palette.Success,
        CheckHealthStatus.Attention or ErrorSeverity.PartialGlosa or ErrorSeverity.Warning => Palette.Warning,
        CheckHealthStatus.Critical or ErrorSeverity.CriticalGlosa => Palette.Danger,
        ErrorSeverity.SystemBlocker => Palette.Violet,
        _ => Palette.Neutral
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Fundo suave correspondente, para pílulas e ícones.</summary>
public sealed class StatusBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckHealthStatus.Healthy => Palette.SuccessSoft,
        CheckHealthStatus.Attention or ErrorSeverity.PartialGlosa or ErrorSeverity.Warning => Palette.WarningSoft,
        CheckHealthStatus.Critical or ErrorSeverity.CriticalGlosa => Palette.DangerSoft,
        ErrorSeverity.SystemBlocker => Palette.VioletSoft,
        _ => Palette.NeutralSoft
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Ícone (Segoe Fluent Icons) para o status.</summary>
public sealed class StatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckHealthStatus.Healthy => "",                               // check
        CheckHealthStatus.Attention or ErrorSeverity.PartialGlosa or ErrorSeverity.Warning => "", // aviso
        CheckHealthStatus.Critical or ErrorSeverity.CriticalGlosa => "", // X
        ErrorSeverity.SystemBlocker => "",                              // ferramenta
        _ => ""                                                         // info
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Grupo de ocorrência do SIA (GRV, NOR, ACE, AVS, ADV) → cor. ConverterParameter "soft" devolve o fundo.</summary>
public sealed class SiaGroupBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        return (value as string) switch
        {
            "GRV" or "NOR" => soft ? Palette.DangerSoft : Palette.Danger,
            "ACE" or "AVS" => soft ? Palette.WarningSoft : Palette.Warning,
            _ => soft ? Palette.NeutralSoft : Palette.Neutral
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null ou texto vazio → Collapsed; qualquer outro valor → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"A, B, C" → ["A", "B", "C"] (para exibir como chips).</summary>
public sealed class SplitListConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
