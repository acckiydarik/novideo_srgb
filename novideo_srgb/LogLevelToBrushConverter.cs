using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace novideo_srgb
{
    public class LogLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch ((LogLevel)value)
            {
                case LogLevel.Success:
                    return Brushes.Green;
                case LogLevel.Warning:
                    return Brushes.DarkOrange;
                case LogLevel.Error:
                    return Brushes.Red;
                case LogLevel.Off:
                    return Brushes.Gray;
                default:
                    return Brushes.Black;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
