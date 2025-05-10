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
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting
{
    public class TaskRunBackup : IScheduledTask
    {
        private ILogger<TaskRunBackup> _logger;
        private ILoggerFactory _loggerFactory;
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;

        public string Name => "Playback Reporting Run Backup";
        public string Key => "PlaybackHistoryRunBackup";
        public string Description => "Runs the report data backup";
        public string Category => "Playback Reporting";


        public TaskRunBackup(ILoggerFactory loggerFactory, IServerConfigurationManager config, IFileSystem fileSystem)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<TaskRunBackup>();
            _config = config;
            _fileSystem = fileSystem;

            _logger.LogInformation("TaskRunBackup Loaded");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var trigger = new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = 0,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }; //3am on Sunday
            return new[] { trigger };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                BackupManager backup = new BackupManager(_config, _loggerFactory, _fileSystem);
                backup.SaveBackup();
            }, cancellationToken);
        }
    }
}
