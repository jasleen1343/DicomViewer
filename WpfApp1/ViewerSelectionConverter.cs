using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WpfApp1
{
    public class ViewerSelectionConverter : IValueConverter
    {
        private static readonly SolidColorBrush Gray = new SolidColorBrush(Colors.Gray);
        private static readonly SolidColorBrush Blue = new SolidColorBrush(Colors.DeepSkyBlue);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string selected = value as string;
            string target = parameter as string;
            return selected == target ? Blue : Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
