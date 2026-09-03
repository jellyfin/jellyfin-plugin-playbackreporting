/*
Copyright(C) 2026

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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    public static class TimeZoneHelper
    {
        public readonly record struct DateRange(DateTime StartUtc, DateTime EndUtc);
        private const int MaxCacheSize = 256;
        private static readonly ConcurrentDictionary<string, TimeZoneInfo> _timezoneCache = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _warnedTimezoneIds = new(StringComparer.Ordinal);

        public static TimeZoneInfo Resolve(string timezoneId, ILogger logger)
        {
            if (_timezoneCache.TryGetValue(timezoneId, out TimeZoneInfo? cached))
            {
                return cached;
            }

            TimeZoneInfo timezone;
            try
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                if (_warnedTimezoneIds.TryAdd(timezoneId, 0))
                {
                    logger.LogWarning("Unknown timezone '{TimezoneId}', falling back to UTC", timezoneId);
                }

                return TimeZoneInfo.Utc;
            }
            catch (InvalidTimeZoneException)
            {
                if (_warnedTimezoneIds.TryAdd(timezoneId, 0))
                {
                    logger.LogWarning("Invalid timezone '{TimezoneId}', falling back to UTC", timezoneId);
                }

                return TimeZoneInfo.Utc;
            }

            if (_timezoneCache.Count < MaxCacheSize)
            {
                _timezoneCache.TryAdd(timezoneId, timezone);
            }

            return timezone;
        }

        public static DateTime UtcToUserLocal(DateTime utcTime, TimeZoneInfo timezone)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcTime, timezone);
        }

        public static DateTime UserLocalToUtc(DateTime userLocal, TimeZoneInfo timezone)
        {
            DateTime unspecified = DateTime.SpecifyKind(userLocal, DateTimeKind.Unspecified);
            while (timezone.IsInvalidTime(unspecified))
            {
                unspecified = unspecified.AddMinutes(1);
            }

            if (!timezone.IsAmbiguousTime(unspecified))
            {
                return TimeZoneInfo.ConvertTimeToUtc(unspecified, timezone);
            }

            DateTime[] candidates = Array.ConvertAll(timezone.GetAmbiguousTimeOffsets(unspecified), offset =>
                DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc));
            Array.Sort(candidates);
            return candidates[0];
        }

        public static DateRange GetDateRange(int days, DateTime endDate, TimeZoneInfo timezone)
        {
            DateTime localStart = endDate.Date.AddDays(1 - days);
            DateTime localEnd = endDate.Date.AddDays(1);
            return new DateRange(UserLocalToUtc(localStart, timezone), UserLocalToUtc(localEnd, timezone));
        }

        public static IEnumerable<(string Key, int Seconds)> SplitIntoLocalHours(DateTime startUtc, int duration, TimeZoneInfo timezone)
        {
            DateTime current = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
            DateTime end = current.AddSeconds(duration);
            while (current < end)
            {
                DateTime local = UtcToUserLocal(current, timezone);
                DateTime next = GetNextLocalHourBoundaryUtc(current, local, timezone);
                if (next <= current || next > end)
                {
                    next = end;
                }

                yield return ($"{(int)local.DayOfWeek}-{local:HH}", (int)(next - current).TotalSeconds);
                current = next;
            }
        }

        private static DateTime GetNextLocalHourBoundaryUtc(DateTime currentUtc, DateTime local, TimeZoneInfo timezone)
        {
            DateTime boundary = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
            while (true)
            {
                if (timezone.IsInvalidTime(boundary))
                {
                    boundary = boundary.AddHours(1);
                    continue;
                }

                if (!timezone.IsAmbiguousTime(boundary))
                {
                    return UserLocalToUtc(boundary, timezone);
                }

                DateTime[] candidates = Array.ConvertAll(timezone.GetAmbiguousTimeOffsets(boundary), offset =>
                    DateTime.SpecifyKind(boundary - offset, DateTimeKind.Utc));
                Array.Sort(candidates);
                foreach (DateTime candidate in candidates)
                {
                    if (candidate > currentUtc)
                    {
                        return candidate;
                    }
                }

                boundary = boundary.AddHours(1);
            }
        }
    }
}
