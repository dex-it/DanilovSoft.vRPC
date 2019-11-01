﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DanilovSoft.vRPC.Resources
{
    internal partial class SR
    {
        /// <summary>
        /// Форматирует строку.
        /// </summary>
        public static string GetString(string message, object arg0)
        {
            return string.Format(CultureInfo.CurrentCulture, message, arg0);
        }

        /// <summary>
        /// Форматирует строку.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static string GetString(string message, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, message, args);
        }
    }
}
