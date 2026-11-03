using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace TodoAssistant.Converters
{
    /// <summary>
    /// 天数 → 中文称呼：0=今天 1=明天 2=后天 3=大后天，再往后显示「N 天后」。
    /// 用法：{Binding Offset, Converter={StaticResource DayNameConverter}}
    /// </summary>
    public sealed class DayNameConverter : System.Windows.Data.IValueConverter
    {
        public static string Name(int offset) => offset switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            3 => "大后天",
            _ => offset + " 天后",
        };

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is int i ? Name(i) : "";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>XAML 模板里没法写编译器：这里提供一个最小的 MarkupExtension 备用</summary>
    public sealed class NoopExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => null;
    }
}
