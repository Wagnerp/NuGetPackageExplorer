﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Data;

namespace PackageExplorer
{
    public class StringShorternerConverter : IValueConverter
    {

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (parameter == null)
            {
                return value;
            }
            string stringValue = (string)value;
            int maxLength = System.Convert.ToInt32(parameter);
            if (maxLength < 5)
            {
                throw new ArgumentOutOfRangeException("parameter");
            }

            if (stringValue.Length <= maxLength)
            {
                return stringValue;
            }

            int prefixLength = (maxLength - 3) / 2;
            int suffixLength = maxLength - 3 - prefixLength;

            return stringValue.Substring(0, prefixLength) + "..." + stringValue.Substring(stringValue.Length - suffixLength);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
