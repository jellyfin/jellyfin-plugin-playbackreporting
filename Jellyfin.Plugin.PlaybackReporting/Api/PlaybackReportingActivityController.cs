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
using System.Linq;
using System.Net.Mime;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PlaybackReporting.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting.Api
{
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("user_usage_stats")]
    [Produces(MediaTypeNames.Application.Json)]
    public class PlaybackReportingActivityController : ControllerBase
    {
        private readonly ILogger<PlaybackReportingActivityController> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _config;
        private readonly IUserManager _userManager;

        private readonly IActivityRepository _repository;

        public PlaybackReportingActivityController(
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager config,
            IUserManager userManager)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<PlaybackReportingActivityController>();
            _fileSystem = fileSystem;
            _config = config;
            _userManager = userManager;

            _logger.LogInformation("PlaybackReportingActivityController Loaded");
            var repo = new ActivityRepository(loggerFactory.CreateLogger<ActivityRepository>(), _config.ApplicationPaths, _fileSystem);
            //repo.Initialize();
            _repository = repo;
        }

        /// <summary>
        /// Gets types filter list items.
        /// </summary>
        /// <returns></returns>
        [HttpGet("type_filter_list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetTypeFilterList()
        {
            return Ok(_repository.GetTypeFilterList());
        }

        /// <summary>
        /// Gets a report of the available activity per hour.
        /// </summary>
        /// <param name="days">Number of Days</param>
        /// <param name="endDate">Optional. End date of the report in yyyy-MM-dd format. Defaults to <see cref="DateTime.Now"/>.</param>
        /// <response code="200">Report returned.</response>
        /// <returns></returns>
        [HttpGet("user_activity")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetUserReport([FromQuery] int days, [FromQuery] DateTime? endDate, [FromQuery] float? timezoneOffset)
        {
            List<Dictionary<string, object>> report = _repository.GetUserReport(days, endDate ?? DateTime.Now, timezoneOffset ?? 0);

            foreach(var user_info in report)
            {
                string user_id = (string)user_info["user_id"];
                Guid user_guid = new Guid(user_id);
                User? user = _userManager.GetUserById(user_guid);
                bool has_image = !(user?.ProfileImage is null);
                string user_name = user?.Username ?? "Not Known";

                user_info.Add("user_name", user_name);
                user_info.Add("has_image", has_image);

                DateTime last_seen = (DateTime)user_info["latest_date"];
                TimeSpan time_ago = DateTime.Now.Subtract(last_seen);

                string last_seen_string = GetLastSeenString(time_ago);
                if (last_seen_string == "")
                {
                    last_seen_string = "just now";
                }
                user_info.Add("last_seen", last_seen_string);

                int seconds = (int)user_info["total_time"];
                TimeSpan total_time = new TimeSpan(10000000L * (long)seconds);

                string time_played = GetLastSeenString(total_time);
                if (time_played == "")
                {
                    time_played = "< 1 minute";
                }
                user_info.Add("total_play_time", time_played);

            }

            return Ok(report);
        }

        /// <summary>
        /// Prune unknown users
        /// </summary>
        /// <returns></returns>
        [HttpGet("user_manage/prune")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<bool> PruneUnknownUsers()
        {
            List<string> userIdList = new List<string>();
            foreach (var jellyfinUser in _userManager.Users)
            {
                userIdList.Add(jellyfinUser.Id.ToString("N"));
            }
            _repository.RemoveUnknownUsers(userIdList);

            return true;
        }

        /// <summary>
        /// Add user to ignore list
        /// </summary>
        /// <param name="id">User Id to perform the action on</param>
        /// <returns></returns>
        [HttpGet("user_manage/add")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<bool> IgnoreListAdd([FromQuery] string id)
        {
            _repository.ManageUserList("add", id);

            return true;
        }

        /// <summary>
        /// Remove user to ignore list
        /// </summary>
        /// <param name="id">User Id to perform the action on</param>
        /// <returns></returns>
        [HttpGet("user_manage/remove")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<bool> IgnoreListRemove([FromQuery] string id)
        {
            _repository.ManageUserList("remove", id);

            return true;
        }

        /// <summary>
        /// Gets the users.
        /// </summary>
        /// <returns>A <see cref="List{Dictionary}"/> containing the users.</returns>
        [HttpGet("user_list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetJellyfinUsers()
        {
            List<string> userIdList = _repository.GetUserList();

            List<Dictionary<string, object>> users = new List<Dictionary<string, object>>();

            foreach (var jellyfinUser in _userManager.Users)
            {
                Dictionary<string, object> user_info = new Dictionary<string, object>
                {
                    { "name", jellyfinUser.Username },
                    { "id", jellyfinUser.Id.ToString("N") },
                    { "in_list", userIdList.Contains(jellyfinUser.Id.ToString("N")) }
                };
                users.Add(user_info);
            }

            return Ok(users);
        }

        /// <summary>
        /// Gets activity for {USER} for {Date} formatted as yyyy-MM-dd.
        /// </summary>
        /// <param name="userId">User Id.</param>
        /// <param name="date">UTC DateTime, Format yyyy-MM-dd.</param>
        /// <param name="filter">Comma separated list of media types to filter (movies,series).</param>
        /// <response code="200">Activity returned.</response>
        /// <returns></returns>
        [HttpGet("{userId}/{date}/GetItems")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetUserReportData([FromRoute] string userId, [FromRoute] string date, [FromQuery] string? filter, [FromQuery] float? timezoneOffset)
        {
            string[] filter_tokens = Array.Empty<string>();
            if (filter != null)
            {
                filter_tokens = filter.Split(',');
            }

            List<Dictionary<string, string>> results = _repository.GetUsageForUser(date, userId, filter_tokens, timezoneOffset ?? 0);

            List<Dictionary<string, object>> user_activity = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, string> item_data in results)
            {
                Dictionary<string, object> item_info = new Dictionary<string, object>
                {
                    ["Time"] = item_data["Time"],
                    ["Id"] = item_data["Id"],
                    ["Name"] = item_data["ItemName"],
                    ["Type"] = item_data["Type"],
                    ["Client"] = item_data["ClientName"],
                    ["Method"] = item_data["PlaybackMethod"],
                    ["Device"] = item_data["DeviceName"],
                    ["Duration"] = item_data["PlayDuration"],
                    ["RowId"] = item_data["RowId"]
                };

                user_activity.Add(item_info);
            }

            return Ok(user_activity);
        }

        /// <summary>
        /// Loads a backup from a file.
        /// </summary>
        /// <param name="backupFilePath">File name of file to load.</param>
        /// <response code="200">Backup loaded.</response>
        /// <returns></returns>
        [HttpGet("load_backup")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<string>> LoadBackup(string backupFilePath)
        {
            FileInfo fi = new FileInfo(backupFilePath);
            if (fi.Exists == false)
            {
                return new List<string> { "Backup file does not exist" };
            }

            int count;
            try
            {
                string load_data;
                using (StreamReader sr = new StreamReader(new FileStream(fi.FullName, FileMode.Open)))
                {
                    load_data = sr.ReadToEnd();
                }
                count = _repository.ImportRawData(load_data);
            }
            catch (Exception e)
            {
                return new List<string> { e.Message };
            }

            return new List<string> { "Backup loaded " + count + " items" };
        }

        /// <summary>
        /// Saves a backup of the playback report data to the backup path.
        /// </summary>
        /// <param name="saveBackup"></param>
        /// <response code="200">Backup Saved</response>
        /// <returns></returns>
        [HttpGet("save_backup")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<List<string>> SaveBackup()
        {
            BackupManager bum = new BackupManager(_config, _loggerFactory, _fileSystem);
            string message = bum.SaveBackup();

            return new List<string> { message };
        }

        /// <summary>
        /// Gets play activity for number of days
        /// </summary>
        /// <param name="days">Number of Days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <param name="filter">Comma separated list of media types to filter (movies,series).</param>
        /// <param name="dataType">Data type to return (count,time).</param>
        /// <response code="200">Activity returned.</response>
        /// <returns></returns>
        [HttpGet("PlayActivity")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetUsageStats(int days, DateTime? endDate, string? filter, string? dataType, [FromQuery] float? timezoneOffset)
        {
            string[] filter_tokens = Array.Empty<string>();
            if (filter != null)
            {
                filter_tokens = filter.Split(',');
            }

            endDate ??= DateTime.Now;

            Dictionary<String, Dictionary<string, int>> results = _repository.GetUsageForDays(days, endDate.Value, filter_tokens, dataType, timezoneOffset ?? 0);

            // add empty user for labels
            results.Add("labels_user", new Dictionary<string, int>());

            List<Dictionary<string, object>> user_usage_data = new List<Dictionary<string, object>>();
            foreach (string user_id in results.Keys)
            {
                Dictionary<string, int> user_usage = results[user_id];

                // fill in missing dates for time period
                SortedDictionary<string, int> userUsageByDate = new SortedDictionary<string, int>();
                DateTime from_date = endDate.Value.AddDays(days * -1 + 1);
                while (from_date <= endDate.Value)
                {
                    string date_string = from_date.ToString("yyyy-MM-dd");
                    if (user_usage.ContainsKey(date_string) == false)
                    {
                        userUsageByDate.Add(date_string, 0);
                    }
                    else
                    {
                        userUsageByDate.Add(date_string, user_usage[date_string]);
                    }

                    from_date = from_date.AddDays(1);
                }

                string user_name = "Not Known";
                if (user_id == "labels_user")
                {
                    user_name = "labels_user";
                }
                else
                {
                    Guid user_guid = new Guid(user_id);
                    User? user = _userManager.GetUserById(user_guid);
                    if (user != null)
                    {
                        user_name = user.Username;
                    }
                }

                Dictionary<string, object> user_data = new Dictionary<string, object>
                {
                    { "user_id", user_id },
                    { "user_name", user_name },
                    { "user_usage", userUsageByDate }
                };

                user_usage_data.Add(user_data);
            }

            var sorted_data = user_usage_data.OrderBy(dict => (dict["user_name"] as string)?.ToLower());

            return Ok(sorted_data);
        }

        /// <summary>
        /// Gets a report of the available activity per hour.
        /// </summary>
        /// <param name="days">Number of Days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <param name="filter">Comma separated list of media types to filter (movies,series).</param>
        /// <returns></returns>
        [HttpGet("HourlyReport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetHourlyReport(int days, DateTime? endDate, string? filter, [FromQuery] float? timezoneOffset)
        {
            string[] filter_tokens = Array.Empty<string>();
            if (filter != null)
            {
                filter_tokens = filter.Split(',');
            }

            endDate ??= DateTime.Now;

            SortedDictionary<string, int> report = _repository.GetHourlyUsageReport(days, endDate.Value, filter_tokens, timezoneOffset ?? 0);

            for (int day = 0; day < 7; day++)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    string key = day + "-" + hour.ToString("D2");
                    if(report.ContainsKey(key) == false)
                    {
                        report.Add(key, 0);
                    }
                }
            }

            return Ok(report);
        }

        /// <summary>
        /// Gets a breakdown of a usage metric.
        /// </summary>
        /// <param name="breakdownType"></param>
        /// <param name="days">Number of days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <response code="200">Activity returned.</response>
        /// <returns></returns>
        [HttpGet("{breakdownType}/BreakdownReport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetBreakdownReport([FromRoute] string breakdownType, int days, DateTime? endDate, [FromQuery] float? timezoneOffset)
        {
            List<Dictionary<string, object>> report = _repository.GetBreakdownReport(days, endDate ?? DateTime.Now, breakdownType, timezoneOffset ?? 0);

            if (breakdownType == "UserId")
            {
                foreach (var row in report)
                {
                    if (row["label"] is string user_id)
                    {
                        Guid user_guid = new Guid(user_id);
                        User? user = _userManager.GetUserById(user_guid);

                        if (user != null)
                        {
                            row["label"] = user.Username;
                        }
                    }
                    else
                    {
                        row["label"] = "unknown";
                    }
                }
            }

            return Ok(report);
        }

        /// <summary>
        /// Gets duration histogram.
        /// </summary>
        /// <param name="days">Number of Days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <param name="filter">Comma separated list of media types to filter (movies,series).</param>
        /// <response code="200">Histogram returned.</response>
        /// <returns></returns>
        [HttpGet("DurationHistogramReport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetDurationHistogramReport(int days, DateTime? endDate, string? filter)
        {
            string[] filter_tokens = Array.Empty<string>();
            if (filter != null)
            {
                filter_tokens = filter.Split(',');
            }

            SortedDictionary<int, int> report = _repository.GetDurationHistogram(days, endDate ?? DateTime.Now, filter_tokens);

            // find max
            int max = -1;
            foreach (int key in report.Keys)
            {
                if (key > max)
                {
                    max = key;
                }
            }

            for(int x = 0; x < max; x++)
            {
                if(report.ContainsKey(x) == false)
                {
                    report.Add(x, 0);
                }
            }

            return Ok(report);
        }

        /// <summary>
        /// Gets TV Shows counts.
        /// </summary>
        /// <param name="days">Number of Days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <returns></returns>
        [HttpGet("GetTvShowsReport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetTvShowsReport(int days, DateTime? endDate, [FromQuery] float? timezoneOffset)
        {
            return Ok(_repository.GetTvShowReport(days, endDate ?? DateTime.Now, timezoneOffset ?? 0));
        }

        /// <summary>
        /// Gets TV Shows counts.
        /// </summary>
        /// <param name="days">Number of Days.</param>
        /// <param name="endDate">End date of the report in yyyy-MM-dd format.</param>
        /// <response code="200">TV Shows returned.</response>
        /// <returns></returns>
        [HttpGet("MoviesReport")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetMovieReport(int days, DateTime? endDate, [FromQuery] float? timezoneOffset)
        {
            return Ok(_repository.GetMoviesReport(days, endDate ?? DateTime.Now, timezoneOffset ?? 0));
        }

        public class CustomQueryData
        {
            public string CustomQueryString {get;set;} = "";
            public bool ReplaceUserId {get;set;} = false;

        }
        /// <summary>
        /// Submit an SQL query.
        /// </summary>
        /// <param name="customQueryString">The custom SQL query.</param>
        /// <param name="replaceUserId"></param>
        /// <response code="200">SQL query executed.</response>
        /// <returns></returns>
        [HttpPost("submit_custom_query")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string,object>> CustomQuery([FromBody] CustomQueryData data)
        {
            _logger.LogInformation("CustomQuery : {CustomerQueryString}", data.CustomQueryString);

            Dictionary<string, object> responce = new Dictionary<string, object>();

            List<List<object>> result = new List<List<object>>();
            List<string> colums = new List<string>();
            string message = _repository.RunCustomQuery(data.CustomQueryString, colums, result);

            int index_of_user_col = colums.IndexOf("UserId");
            if (data.ReplaceUserId && index_of_user_col > -1)
            {
                colums[index_of_user_col] = "UserName";

                Dictionary<string, string> user_map = new Dictionary<string, string>();
                foreach (var user in _userManager.Users)
                {
                    user_map.Add(user.Id.ToString("N"), user.Username);
                }

                foreach(var row in result)
                {
                    String user_id = (string)row[index_of_user_col];
                    if(user_map.ContainsKey(user_id))
                    {
                        row[index_of_user_col] = user_map[user_id];
                    }
                }
            }

            responce.Add("colums", colums);
            responce.Add("results", result);
            responce.Add("message", message);

            return responce;
        }

        private static string GetLastSeenString(TimeSpan span)
        {
            String last_seen = "";

            if (span.TotalDays > 365)
            {
                last_seen += GetTimePart((int)(span.TotalDays / 365), "year");
            }

            if ((double)(span.TotalDays % 365) > 7)
            {
                last_seen += GetTimePart((int)((span.TotalDays % 365) / 7), "week");
            }

            if ((int)(span.TotalDays % 7) > 0)
            {
                last_seen += GetTimePart((int)(span.TotalDays % 7), "day");
            }

            if (span.Hours > 0)
            {
                last_seen += GetTimePart(span.Hours, "hour");
            }

            if (span.Minutes > 0)
            {
                last_seen += GetTimePart(span.Minutes, "minute");
            }

            return last_seen;
        }

        private static string GetTimePart(int value, string name)
        {
            string part = value + " " + name;
            if (value > 1)
            {
                part += "s";
            }
            return part + " ";
        }
    }
}
