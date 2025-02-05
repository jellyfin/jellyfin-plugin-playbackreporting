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
using System.IO;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    public class ActivityRepository : BaseSqliteRepository, IActivityRepository
    {
        private static readonly string DATE_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";
        private readonly ILogger<ActivityRepository> _logger;
        protected IFileSystem FileSystem { get; private set; }

        public ActivityRepository(ILogger<ActivityRepository> logger, IServerApplicationPaths appPaths, IFileSystem fileSystem) : base(logger)
        {
            DbFilePath = Path.Combine(appPaths.DataPath, "playback_reporting.db");
            FileSystem = fileSystem;
            _logger = logger;
        }

        public void Initialize()
        {
            try
            {
                InitializeInternal();
            }

            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading PlaybackActivity database file.");
                //FileSystem.DeleteFile(DbFilePath);
                //InitializeInternal();
            }
        }

        private void InitializeInternal()
        {
            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                _logger.LogInformation("Initialize PlaybackActivity Repository");

                string sql_info = "pragma table_info('PlaybackActivity')";
                List<string> cols = new List<string>();
                foreach (var row in connection.Query(sql_info))
                {
                    string table_schema = row[1].ToString().ToLower() + ":" + row[2].ToString().ToLower();
                    cols.Add(table_schema);
                }
                string actual_schema = string.Join("|", cols);
                string required_schema = "datecreated:datetime|userid:text|itemid:text|itemtype:text|itemname:text|playbackmethod:text|clientname:text|devicename:text|playduration:int";
                if (required_schema != actual_schema)
                {
                    _logger.LogInformation("PlaybackActivity table schema miss match!");
                    _logger.LogInformation("Expected : {RequiredSchema}", required_schema);
                    _logger.LogInformation("Received : {ActualSchema}", actual_schema);
                    _logger.LogInformation("Dropping and recreating PlaybackActivity table");
                    connection.Execute("drop table if exists PlaybackActivity");
                }
                else
                {
                    _logger.LogInformation("PlaybackActivity table schema OK");
                    _logger.LogInformation("Expected : {RequiredSchema}", required_schema);
                    _logger.LogInformation("Received : {ActualSchema}", actual_schema);
                }

                // ROWID
                connection.Execute("create table if not exists PlaybackActivity (" +
                                "DateCreated DATETIME NOT NULL, " +
                                "UserId TEXT, " +
                                "ItemId TEXT, " +
                                "ItemType TEXT, " +
                                "ItemName TEXT, " +
                                "PlaybackMethod TEXT, " +
                                "ClientName TEXT, " +
                                "DeviceName TEXT, " +
                                "PlayDuration INT" +
                                ")");

                connection.Execute("create table if not exists UserList (UserId TEXT)");
            }
        }

        private TimeSpan CalculateUserServerTimezoneOffset(float userOffset)
        {
            var serverOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            return serverOffset.Subtract(TimeSpan.FromHours(userOffset));
        }

        public string RunCustomQuery(string query_string, List<string> col_names, List<List<object>> results)
        {
            string message = "";
            bool columns_done = false;
            int change_count = 0;
            using (WriteLock.Write())
            {
                using var connection = CreateConnection(true);
                try
                {
                    using var statement = connection.PrepareStatement(query_string);
                    foreach (var row in statement.ExecuteQuery())
                    {
                        if (!columns_done)
                        {
                            foreach (var col in row.Columns())
                            {
                                col_names.Add(col.Name);
                            }
                            columns_done = true;
                        }

                        List<object> row_date = new List<object>();
                        for (int x = 0; x < row.Count; x++)
                        {
                            string cell_data = row[x].ToString();
                            row_date.Add(cell_data);
                        }
                        results.Add(row_date);

                        string type = row[0].ToString();
                    }
                    change_count = connection.GetChangeCount();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error in SQL");
                    message = "Error Running Query</br>" + e.Message;
                    message += "<pre>" + e + "</pre>";
                }
            }

            if(string.IsNullOrEmpty(message) && col_names.Count == 0 && results.Count == 0)
            {
                message = "Query executed, no data returned.";
                message += "</br>Number of rows effected : " + change_count;
            }

            return message;
        }

        public int RemoveUnknownUsers(List<string> known_user_ids)
        {
            string sql_query = "delete from PlaybackActivity " +
                               "where UserId not in ('" + string.Join("', '", known_user_ids) + "') ";

            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                connection.RunInTransaction(db =>
                {
                    db.Execute(sql_query);
                }, TransactionMode);
            }
            return 1;
        }

        public void ManageUserList(string action, string id)
        {
            string sql;
            if(action == "add")
            {
                sql = "insert into UserList (UserId) values (@id)";
            }
            else
            {
                sql = "delete from UserList where UserId = @id";
            }
            using (WriteLock.Write())
            {
                using var connection = CreateConnection(true);
                connection.RunInTransaction(db =>
                {
                    using var statement = db.PrepareStatement(sql);
                    statement.TryBind("@id", id);
                    statement.MoveNext();
                }, TransactionMode);
            }
        }

        public List<string> GetUserList()
        {
            List<string> user_id_list = new List<string>();
            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                string sql_query = "select UserId from UserList";
                using var statement = connection.PrepareStatement(sql_query);
                foreach (var row in statement.ExecuteQuery())
                {
                    string type = row[0].ToString();
                    user_id_list.Add(type);
                }
            }

            return user_id_list;
        }

        public List<string> GetTypeFilterList()
        {
            List<string> filter_Type_list = new List<string>();
            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                string sql_query = "select distinct ItemType from PlaybackActivity";
                using var statement = connection.PrepareStatement(sql_query);
                foreach (var row in statement.ExecuteQuery())
                {
                    string type = row[0].ToString();
                    filter_Type_list.Add(type);
                }
            }
            return filter_Type_list;
        }

        public int ImportRawData(string data)
        {
            int count = 0;
            _logger.LogInformation("Loading Data");
            using (WriteLock.Write())
            {
                using var connection = CreateConnection(true);
                StringReader sr = new StringReader(data);

                string? line = sr?.ReadLine();
                while (line != null)
                {
                    string[] tokens = line.Split('\t');
                    _logger.LogInformation("Line Length : {NumberOfTokens}", tokens.Length);
                    if (tokens.Length != 9)
                    {
                        line = sr!.ReadLine();
                        continue;
                    }

                    string date = tokens[0];
                    string user_id = tokens[1];
                    string item_id = tokens[2];
                    string item_type = tokens[3];
                    string item_name = tokens[4];
                    string play_method = tokens[5];
                    string client_name = tokens[6];
                    string device_name = tokens[7];
                    string duration = tokens[8];

                    //_logger.LogInformation(date + "\t" + user_id + "\t" + item_id + "\t" + item_type + "\t" + item_name + "\t" + play_method + "\t" + client_name + "\t" + device_name + "\t" + duration);

                    string sql = "select rowid from PlaybackActivity where DateCreated = @DateCreated and UserId = @UserId and ItemId = @ItemId";
                    using (var statement = connection.PrepareStatement(sql))
                    {

                        statement.TryBind("@DateCreated", date);
                        statement.TryBind("@UserId", user_id);
                        statement.TryBind("@ItemId", item_id);
                        bool found = false;
                        foreach (var row in statement.ExecuteQuery())
                        {
                            found = true;
                            break;
                        }

                        if (found == false)
                        {
                            _logger.LogInformation("Not Found, Adding");

                            string sql_add = "insert into PlaybackActivity " +
                                "(DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration) " +
                                "values " +
                                "(@DateCreated, @UserId, @ItemId, @ItemType, @ItemName, @PlaybackMethod, @ClientName, @DeviceName, @PlayDuration)";

                            connection.RunInTransaction(db =>
                            {
                                using var add_statment = db.PrepareStatement(sql_add);
                                add_statment.TryBind("@DateCreated", date);
                                add_statment.TryBind("@UserId", user_id);
                                add_statment.TryBind("@ItemId", item_id);
                                add_statment.TryBind("@ItemType", item_type);
                                add_statment.TryBind("@ItemName", item_name);
                                add_statment.TryBind("@PlaybackMethod", play_method);
                                add_statment.TryBind("@ClientName", client_name);
                                add_statment.TryBind("@DeviceName", device_name);
                                add_statment.TryBind("@PlayDuration", duration);
                                add_statment.MoveNext();
                            }, TransactionMode);
                            count++;
                        }
                    }

                    line = sr?.ReadLine();
                }
            }
            return count;
        }

        public string ExportRawData()
        {
            StringWriter sw = new StringWriter();

            string sql_raw = "SELECT * FROM PlaybackActivity ORDER BY DateCreated";
            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql_raw);
                foreach (var row in statement.ExecuteQuery())
                {
                    List<string> row_data = new List<string>();
                    for (int x = 0; x < row.Count; x++)
                    {
                        row_data.Add(row[x].ToString());
                    }
                    sw.WriteLine(string.Join("\t", row_data));
                }
            }
            sw.Flush();
            return sw.ToString();
        }

        public void DeleteOldData(DateTime? delBefore)
        {
            string sql = "delete from PlaybackActivity";
            if (delBefore != null)
            {
                DateTime date = (DateTime)delBefore;
                sql += " where DateCreated < '" + date.ToDateTimeParamValue() + "'";
            }

            _logger.LogInformation("DeleteOldData : {Sql}", sql);

            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                connection.RunInTransaction(db =>
                {
                    db.Execute(sql);
                }, TransactionMode);
            }
        }

        public void AddPlaybackAction(PlaybackInfo playInfo)
        {
            string sql_add = "insert into PlaybackActivity " +
                "(DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration) " +
                "values " +
                "(@DateCreated, @UserId, @ItemId, @ItemType, @ItemName, @PlaybackMethod, @ClientName, @DeviceName, @PlayDuration)";

            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                connection.RunInTransaction(db =>
                {
                    using var statement = db.PrepareStatement(sql_add);
                    statement.TryBind("@DateCreated", playInfo.Date.ToDateTimeParamValue());
                    statement.TryBind("@UserId", playInfo.UserId);
                    statement.TryBind("@ItemId", playInfo.ItemId);
                    statement.TryBind("@ItemType", playInfo.ItemType);
                    statement.TryBind("@ItemName", playInfo.ItemName);
                    statement.TryBind("@PlaybackMethod", playInfo.PlaybackMethod);
                    statement.TryBind("@ClientName", playInfo.ClientName);
                    statement.TryBind("@DeviceName", playInfo.DeviceName);
                    statement.TryBind("@PlayDuration", playInfo.PlaybackDuration);
                    statement.MoveNext();
                }, TransactionMode);
            }
        }

        public void UpdatePlaybackAction(PlaybackInfo playInfo)
        {
            string sql_add = "update PlaybackActivity set PlayDuration = @PlayDuration where DateCreated = @DateCreated and UserId = @UserId and ItemId = @ItemId";
            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                connection.RunInTransaction(db =>
                {
                    using var statement = db.PrepareStatement(sql_add);
                    statement.TryBind("@DateCreated", playInfo.Date.ToDateTimeParamValue());
                    statement.TryBind("@UserId", playInfo.UserId);
                    statement.TryBind("@ItemId", playInfo.ItemId);
                    statement.TryBind("@PlayDuration", playInfo.PlaybackDuration);
                    statement.MoveNext();
                }, TransactionMode);
            }
        }

        public List<Dictionary<string, string>> GetUsageForUser(string date, string userId, string[] types, float timezoneOffset)
        {
            List<string> filters = new List<string>();
            foreach (string filter in types)
            {
                filters.Add("'" + filter + "'");
            }

            string sql_query = "SELECT DateCreated, ItemId, ItemType, ItemName, ClientName, PlaybackMethod, DeviceName, PlayDuration, rowid " +
                               "FROM PlaybackActivity " +
                               "WHERE DateCreated >= @date_from AND DateCreated <= @date_to " +
                               "AND UserId = @user_id " +
                               "AND ItemType IN (" + string.Join(",", filters) + ") " +
                               "ORDER BY DateCreated";

            List<Dictionary<string, string>> items = new List<Dictionary<string, string>>();
            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql_query);
                var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
                var fromDate = DateTime.Parse(date + " 00:00:00").Add(userServerTimezoneOffset);
                var toDate = fromDate.AddHours(23).AddMinutes(59).AddSeconds(59);
                statement.TryBind("@date_from", fromDate.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@date_to", toDate.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@user_id", userId);
                foreach (var row in statement.ExecuteQuery())
                {
                    var time = row[0].ReadDateTime();
                    Dictionary<string, string> item = new Dictionary<string, string>
                    {
                        ["Time"] = time.Add(userServerTimezoneOffset).ToString("h:mm tt"),
                        ["Id"] = row[1].ToString(),
                        ["Type"] = row[2].ToString(),
                        ["ItemName"] = row[3].ToString(),
                        ["ClientName"] = row[4].ToString(),
                        ["PlaybackMethod"] = row[5].ToString(),
                        ["DeviceName"] = row[6].ToString(),
                        ["PlayDuration"] = row[7].ToString(),
                        ["RowId"] = row[8].ToString()
                    };

                    items.Add(item);
                }
            }

            return items;
        }

        public Dictionary<String, Dictionary<string, int>> GetUsageForDays(int days, DateTime endDate, string[] types, string? dataType, float timezoneOffset)
        {
            List<string> filters = new List<string>();
            foreach (string filter in types)
            {
                filters.Add("'" + filter + "'");
            }

            string sql_query = "";
            if (dataType == "count")
            {
                sql_query += "SELECT UserId, DateCreated AS date, COUNT(1) AS count ";
            }
            else
            {
                sql_query += "SELECT UserId, DateCreated AS date, SUM(PlayDuration) AS count ";
            }
            sql_query += "FROM PlaybackActivity ";
            sql_query += "WHERE DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql_query += "AND ItemType IN (" + string.Join(",", filters) + ") ";
            sql_query += "AND UserId not IN (select UserId from UserList) ";
            sql_query += "GROUP BY UserId, date ORDER BY UserId, date ASC";

            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            Dictionary<string, Dictionary<string, int>> usage = new Dictionary<string, Dictionary<string, int>>();

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql_query);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    string user_id = row[0].ToString();
                    Dictionary<string, int> userWatchesByDate;
                    if (usage.ContainsKey(user_id))
                    {
                        userWatchesByDate = usage[user_id];
                    }
                    else
                    {
                        userWatchesByDate = new Dictionary<string, int>();
                        usage.Add(user_id, userWatchesByDate);
                    }
                    string date_string = DateTime.Parse(row[1].ToString()).Add(userServerTimezoneOffset).ToString("yyyy-MM-dd");
                    int count_int = row[2].ToInt();
                    if (userWatchesByDate.ContainsKey(date_string))
                    {
                        var count = userWatchesByDate[date_string];
                        userWatchesByDate[date_string] = count_int + count;
                    }
                    else
                    {
                        userWatchesByDate.Add(date_string, count_int);
                    }
                }
            }

            return usage;
        }

        public SortedDictionary<string, int> GetHourlyUsageReport(int days, DateTime endDate, string[] types, float timezoneOffset)
        {
            List<string> filters = new List<string>();
            foreach (string filter in types)
            {
                filters.Add("'" + filter + "'");
            }

            SortedDictionary<string, int> report_data = new SortedDictionary<string, int>();
            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql = "SELECT DateCreated, PlayDuration ";
            sql += "FROM PlaybackActivity ";
            sql += "WHERE DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql += "AND UserId not IN (select UserId from UserList) ";
            sql += "AND ItemType IN (" + string.Join(",", filters) + ")";

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    DateTime date = row[0].ReadDateTime().Add(userServerTimezoneOffset);
                    int duration = row[1].ToInt();

                    int seconds_left_in_hour = 3600 - (date.Minute * 60 + date.Second);
                    _logger.LogInformation("Processing - date: {Date} duration: {Duration} seconds_left_in_hour {SecondsLeftInHour}", date, duration, seconds_left_in_hour);
                    while (duration > 0)
                    {
                        string hour_id = (int)date.DayOfWeek + "-" + date.ToString("HH");
                        if (duration > seconds_left_in_hour)
                        {
                            AddTimeToHours(report_data, hour_id, seconds_left_in_hour);
                        }
                        else
                        {
                            AddTimeToHours(report_data, hour_id, duration);
                        }

                        duration -= seconds_left_in_hour;
                        seconds_left_in_hour = 3600;
                        date = date.AddHours(1);
                    }
                }
            }

            return report_data;
        }

        private void AddTimeToHours(SortedDictionary<string, int> reportData, string key, int count)
        {
            _logger.LogInformation("Adding Time : {Key} - {Count}", key, count);
            if (reportData.ContainsKey(key))
            {
                reportData[key] += count;
            }
            else
            {
                reportData.Add(key, count);
            }
        }

        public List<Dictionary<string, object>> GetBreakdownReport(int days, DateTime endDate, string type, float timezoneOffset)
        {
            // UserId ItemType PlaybackMethod ClientName DeviceName

            List<Dictionary<string, object>> report = new List<Dictionary<string, object>>();

            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql = "SELECT " + type + ", COUNT(1) AS PlayCount, SUM(PlayDuration) AS Seconds ";
            sql += "FROM PlaybackActivity ";
            sql += "WHERE DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql += "AND UserId not IN (select UserId from UserList) ";
            sql += "GROUP BY " + type;

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    string item_label = row[0].ToString();
                    int action_count = row[1].ToInt();
                    int seconds_sum = row[2].ToInt();

                    Dictionary<string, object> row_data = new Dictionary<string, object>
                            {
                                {"label", item_label}, {"count", action_count}, {"time", seconds_sum}
                            };
                    report.Add(row_data);
                }
            }

            return report;
        }

        public SortedDictionary<int, int> GetDurationHistogram(int days, DateTime endDate, string[] types)
        {
            /*
            SELECT CAST(PlayDuration / 300 as int) AS FiveMinBlock, COUNT(1) ActionCount
            FROM PlaybackActivity
            GROUP BY CAST(PlayDuration / 300 as int)
            ORDER BY CAST(PlayDuration / 300 as int) ASC;
            */

            List<string> filters = new List<string>();
            foreach (string filter in types)
            {
                filters.Add("'" + filter + "'");
            }

            SortedDictionary<int, int> report = new SortedDictionary<int, int>();
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql =
                "SELECT CAST(PlayDuration / 300 as int) AS FiveMinBlock, COUNT(1) ActionCount " +
                "FROM PlaybackActivity " +
                "WHERE DateCreated >= @start_date AND DateCreated <= @end_date " +
                "AND UserId not IN (select UserId from UserList) " +
                "AND ItemType IN (" + string.Join(",", filters) + ") " +
                "GROUP BY CAST(PlayDuration / 300 as int) " +
                "ORDER BY CAST(PlayDuration / 300 as int) ASC";

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString("yyyy-MM-dd 00:00:00"));
                statement.TryBind("@end_date", endDate.ToString("yyyy-MM-dd 23:59:59"));

                foreach (var row in statement.ExecuteQuery())
                {
                    int block_num = row[0].ToInt();
                    int count = row[1].ToInt();
                    report.Add(block_num, count);
                }
            }

            return report;
        }

        public List<Dictionary<string, object>> GetTvShowReport(int days, DateTime endDate, float timezoneOffset)
        {
            List<Dictionary<string, object>> report = new List<Dictionary<string, object>>();

            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql = "";
            sql += "SELECT substr(ItemName,0, instr(ItemName, ' - ')) AS name, ";
            sql += "COUNT(1) AS play_count, ";
            sql += "SUM(PlayDuration) AS total_duarion ";
            sql += "FROM PlaybackActivity ";
            sql += "WHERE ItemType = 'Episode' ";
            sql += "AND DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql += "AND UserId not IN (select UserId from UserList) ";
            sql += "GROUP BY name";

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    string item_label = row[0].ToString();
                    int action_count = row[1].ToInt();
                    int seconds_sum = row[2].ToInt();

                    Dictionary<string, object> row_data = new Dictionary<string, object>
                    {
                        { "label", item_label },
                        { "count", action_count },
                        { "time", seconds_sum }
                    };
                    report.Add(row_data);
                }
            }

            return report;
        }

        public List<Dictionary<string, object>> GetMoviesReport(int days, DateTime endDate, float timezoneOffset)
        {
            List<Dictionary<string, object>> report = new List<Dictionary<string, object>>();

            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql = "";
            sql += "SELECT ItemName AS name, ";
            sql += "COUNT(1) AS play_count, ";
            sql += "SUM(PlayDuration) AS total_duarion ";
            sql += "FROM PlaybackActivity ";
            sql += "WHERE ItemType = 'Movie' ";
            sql += "AND DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql += "AND UserId not IN (select UserId from UserList) ";
            sql += "GROUP BY name";

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    string item_label = row[0].ToString();
                    int action_count = row[1].ToInt();
                    int seconds_sum = row[2].ToInt();

                    Dictionary<string, object> row_data = new Dictionary<string, object>
                            {
                                {"label", item_label}, {"count", action_count}, {"time", seconds_sum}
                            };
                    report.Add(row_data);
                }
            }

            return report;
        }

        public List<Dictionary<string, object>> GetUserReport(int days, DateTime endDate, float timezoneOffset)
        {
            List<Dictionary<string, object>> report = new List<Dictionary<string, object>>();

            var userServerTimezoneOffset = CalculateUserServerTimezoneOffset(timezoneOffset);
            endDate = endDate.Add(userServerTimezoneOffset);
            DateTime start_date = endDate.Subtract(new TimeSpan(days, 0, 0, 0));

            string sql = "";
            sql += "SELECT x.latest_date, x.UserId, x.play_count, x.total_duarion, y.ItemName, y.DeviceName ";
            sql += "FROM( ";
            sql += "SELECT MAX(DateCreated) AS latest_date, UserId, COUNT(1) AS play_count, SUM(PlayDuration) AS total_duarion ";
            sql += "FROM PlaybackActivity ";
            sql += "WHERE DateCreated >= @start_date AND DateCreated <= @end_date ";
            sql += "AND UserId not IN (select UserId from UserList) ";
            sql += "GROUP BY UserId ";
            sql += ") AS x ";
            sql += "INNER JOIN PlaybackActivity AS y ON x.latest_date = y.DateCreated AND x.UserId = y.UserId ";
            sql += "ORDER BY x.latest_date DESC";

            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@start_date", start_date.ToString(DATE_TIME_FORMAT));
                statement.TryBind("@end_date", endDate.AddDays(1).AddSeconds(-1).ToString(DATE_TIME_FORMAT));

                foreach (var row in statement.ExecuteQuery())
                {
                    Dictionary<string, object> row_data = new Dictionary<string, object>();

                    DateTime latest_date = row[0].ReadDateTime().Add(userServerTimezoneOffset);
                    row_data.Add("latest_date", latest_date);

                    string user_id = row[1].ToString();
                    row_data.Add("user_id", user_id);

                    int action_count = row[2].ToInt();
                    int seconds_sum = row[3].ToInt();
                    row_data.Add("total_count", action_count);
                    row_data.Add("total_time", seconds_sum);

                    string item_name = row[4].ToString();
                    row_data.Add("item_name", item_name);

                    string client_name = row[5].ToString();
                    row_data.Add("client_name", client_name);

                    report.Add(row_data);
                }
            }

            return report;
        }
    }
}
