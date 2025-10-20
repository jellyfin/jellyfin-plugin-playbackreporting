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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlaybackReporting.Data;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using IActivityRepository = Jellyfin.Plugin.PlaybackReporting.Data.IActivityRepository;

namespace Jellyfin.Plugin.PlaybackReporting
{
    public class TaskCleanDb : IScheduledTask
    {
        private ILogger<TaskCleanDb> _logger;
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;

        public string Name => "Playback Reporting Trim Db";
        public string Key => "PlaybackHistoryTrimTask";
        public string Description => "Runs the report history trim task";
        public string Category => "Playback Reporting";

        private IActivityRepository Repository;

        public TaskCleanDb(ILoggerFactory loggerFactory, IServerConfigurationManager config, IFileSystem fileSystem)
        {
            _logger = loggerFactory.CreateLogger<TaskCleanDb>();
            _config = config;
            _fileSystem = fileSystem;

            _logger.LogInformation("TaskCleanDb Loaded");
            var repo = new ActivityRepository(loggerFactory.CreateLogger<ActivityRepository>(), _config.ApplicationPaths, _fileSystem);
            //repo.Initialize();
            Repository = repo;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var trigger = new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks
            }; //12am
            return new[] { trigger };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {

            await Task.Run(() =>
            {
                _logger.LogInformation("Playback Reporting Data Trim");

                var config = _config.GetReportPlaybackOptions();

                int max_data_age = config.MaxDataAge;

                _logger.LogInformation("MaxDataAge : {MaxDataAge}", max_data_age);

                if(max_data_age == -1)
                {
                    _logger.LogInformation("Keep data forever, not doing any data cleanup");
                    return;
                }
                else if(max_data_age == 0)
                {
                    _logger.LogInformation("Removing all data");
                    Repository.DeleteOldData(null);
                }
                else
                {
                    DateTime del_defore = DateTime.Now.AddMonths(max_data_age * -1);
                    Repository.DeleteOldData(del_defore);
                }
            }, cancellationToken);
        }
    }
}
