using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using AeroSceneryNG.Core;

namespace AeroSceneryNG.UI
{
    /// <summary>
    /// XAML markup extension: <c>{ui:Tr Key='...'}</c>. It binds through a single
    /// language-version property, so every label updates in place when the language changes.
    /// </summary>
    public sealed class TrExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public TrExtension()
        {
        }

        public TrExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding(nameof(LocalizationSource.Version))
            {
                Source = LocalizationSource.Instance,
                Mode = BindingMode.OneWay,
                Converter = TrConverter.Instance,
                ConverterParameter = Key
            };
            return binding.ProvideValue(serviceProvider);
        }
    }

    /// <summary>Resolves a Chinese source key through the current language.</summary>
    public sealed class TrConverter : IValueConverter
    {
        public static TrConverter Instance { get; } = new TrConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Loc.T(parameter as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}
