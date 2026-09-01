/*
Copyright(C) 2018

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Model.Serialization;
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    // TODO yet another file that is COPIED from core
    public static class SqliteExtensions
    {
        public static string ToDateTimeParamValue(this DateTime dateValue)
        {
            var kind = DateTimeKind.Utc;

            return (dateValue.Kind == DateTimeKind.Unspecified)
                ? DateTime.SpecifyKind(dateValue, kind).ToString(
                    GetDateTimeKindFormat(kind),
                    CultureInfo.InvariantCulture)
                : dateValue.ToString(
                    GetDateTimeKindFormat(dateValue.Kind),
                    CultureInfo.InvariantCulture);
        }

        private static string GetDateTimeKindFormat(
           DateTimeKind kind)
        {
            return (kind == DateTimeKind.Utc) ? _datetimeFormatUtc : _datetimeFormatLocal;
        }

        /// <summary>
        /// An array of ISO-8601 DateTime formats that we support parsing.
        /// </summary>
        private static readonly string[] _datetimeFormats = {
      "THHmmssK",
      "THHmmK",
      "HH:mm:ss.FFFFFFFK",
      "HH:mm:ssK",
      "HH:mmK",
      "yyyy-MM-dd HH:mm:ss.FFFFFFFK", /* NOTE: UTC default (5). */
      "yyyy-MM-dd HH:mm:ssK",
      "yyyy-MM-dd HH:mmK",
      "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
      "yyyy-MM-ddTHH:mmK",
      "yyyy-MM-ddTHH:mm:ssK",
      "yyyyMMddHHmmssK",
      "yyyyMMddHHmmK",
      "yyyyMMddTHHmmssFFFFFFFK",
      "THHmmss",
      "THHmm",
      "HH:mm:ss.FFFFFFF",
      "HH:mm:ss",
      "HH:mm",
      "yyyy-MM-dd HH:mm:ss.FFFFFFF", /* NOTE: Non-UTC default (19). */
      "yyyy-MM-dd HH:mm:ss",
      "yyyy-MM-dd HH:mm",
      "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
      "yyyy-MM-ddTHH:mm",
      "yyyy-MM-ddTHH:mm:ss",
      "yyyyMMddHHmmss",
      "yyyyMMddHHmm",
      "yyyyMMddTHHmmssFFFFFFF",
      "yyyy-MM-dd",
      "yyyyMMdd",
      "yy-MM-dd"
    };

        private static readonly string _datetimeFormatUtc = _datetimeFormats[5];
        private static readonly string _datetimeFormatLocal = _datetimeFormats[19];

        public static DateTime ReadDateTime(this ResultSetValue result)
        {
            return result.ToString().ParseDateTimeToUtc();
        }

        public static DateTime ParseDateTimeToUtc(this string dateText, TimeZoneInfo? legacyTimezone = null)
        {
            DateTime parsed = DateTime.ParseExact(
                dateText, _datetimeFormats,
                DateTimeFormatInfo.InvariantInfo,
                DateTimeStyles.None);
            if (parsed.Kind == DateTimeKind.Utc || dateText.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || HasExplicitOffset(dateText))
            {
                return DateTimeOffset.ParseExact(dateText, _datetimeFormats, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.None).UtcDateTime;
            }

            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), legacyTimezone ?? TimeZoneInfo.Utc);
        }

        private static bool HasExplicitOffset(string text)
        {
            // A '+' or '-' inside the first 10 characters is a date separator ("yyyy-MM-dd");
            // later in the string it only counts as an explicit UTC offset when the remaining
            // tail is shaped like one ("+HH", "+HHmm", "+HH:mm").
            int separator = text.LastIndexOfAny(new[] { '+', '-' });
            if (separator < 10)
            {
                return false;
            }

            string tail = text.Substring(separator + 1);
            return (tail.Length == 5 && tail[2] == ':') || tail.Length == 4 || tail.Length == 2;
        }

        public static DateTime TruncateToSeconds(this DateTime dateValue)
        {
            return new DateTime(
                dateValue.Ticks - (dateValue.Ticks % TimeSpan.TicksPerSecond),
                dateValue.Kind);
        }

        /// <summary>
        /// Formats a DateTime for the PlaybackActivity DateCreated column: converted to UTC and
        /// truncated to whole seconds, always producing the fixed-width "yyyy-MM-dd HH:mm:ssZ" shape.
        /// DateCreated values are stored and compared as TEXT by SQLite, so every value must share
        /// this exact shape: a fractional-seconds value would sort before a whole-seconds value
        /// ('.' sorts before 'Z') and break the >= / < comparisons.
        /// </summary>
        public static string ToUtcDateParamValue(this DateTime dateValue)
        {
            return dateValue.ToUniversalTime().TruncateToSeconds().ToDateTimeParamValue();
        }

        private static void CheckName(string name)
        {
#if DEBUG
            //if (!name.IndexOf("@", StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new Exception("Invalid param name: " + name);
            }
#endif
        }

        public static void TryBind(this IStatement statement, string name, string value)
        {
            if (statement.BindParameters.TryGetValue(name, out IBindParameter? bindParam))
            {
                if (value == null)
                {
                    bindParam.BindNull();
                }
                else
                {
                    bindParam.Bind(value);
                }
            }
            else
            {
                CheckName(name);
            }
        }

        public static void TryBind(this IStatement statement, string name, int value)
        {
            if (statement.BindParameters.TryGetValue(name, out IBindParameter? bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                CheckName(name);
            }
        }

        public static IEnumerable<IReadOnlyList<ResultSetValue>> ExecuteQuery(
            this IStatement This)
        {
            while (This.MoveNext())
            {
                yield return This.Current;
            }
        }
    }

}
