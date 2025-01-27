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

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    public interface IActivityRepository : IDisposable
    {
        int RemoveUnknownUsers(List<string> known_user_ids);
        void ManageUserList(string action, string id);
        List<string> GetUserList();
        List<string> GetTypeFilterList();
        int ImportRawData(string data);
        string ExportRawData();
        void DeleteOldData(DateTime? del_before);
        void AddPlaybackAction(PlaybackInfo play_info);
        void UpdatePlaybackAction(PlaybackInfo play_info);
        List<Dictionary<string, string>> GetUsageForUser(string date, string user_id, string[] filter, float timezoneOffset);
        Dictionary<String, Dictionary<string, int>> GetUsageForDays(int days, DateTime end_date, string[] types, string? data_type, float timezoneOffset);
        SortedDictionary<string, int> GetHourlyUsageReport(int days, DateTime end_date, string[] types, float timezoneOffset);
        List<Dictionary<string, object>> GetBreakdownReport(int days, DateTime end_date, string type, float timezoneOffset);
        SortedDictionary<int, int> GetDurationHistogram(int days, DateTime end_date, string[] types);
        List<Dictionary<string, object>> GetTvShowReport(int days, DateTime end_date, float timezoneOffset);
        List<Dictionary<string, object>> GetMoviesReport(int days, DateTime end_date, float timezoneOffset);
        List<Dictionary<string, object>> GetUserReport(int days, DateTime end_date, float timezoneOffset);
        string RunCustomQuery(string query_string, List<string> col_names, List<List<object>> results);
    }
}
