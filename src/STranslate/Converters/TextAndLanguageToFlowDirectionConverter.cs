using STranslate.Helpers;
using STranslate.Plugin;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace STranslate.Converters;

public class TextAndLanguageToFlowDirectionConverter : MarkupExtension, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0)
            return FlowDirection.LeftToRight;

        var text = values[0] as string;
        var language = values.Length > 1 && values[1] is LangEnum lang ? lang : LangEnum.Auto;
        var detectedLanguage = values.Length > 2 && values[2] is LangEnum identified ? identified : LangEnum.Auto;

        return BidiDirectionHelper.GetFlowDirection(text, language, detectedLanguage);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => [Binding.DoNothing];

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}
