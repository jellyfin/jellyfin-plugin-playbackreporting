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
using Jellyfin.Plugin.PlaybackReporting.Data;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting
{
    public class BackupManager
    {

        private readonly IActivityRepository _repository;
        private readonly IServerConfigurationManager _config;
        private readonly ILogger<BackupManager> _logger;
        private readonly IFileSystem _fileSystem;

        public BackupManager(IServerConfigurationManager config, ILoggerFactory loggerFactory, IFileSystem fileSystem)
        {
            _config = config;
            _fileSystem = fileSystem;
            _logger = loggerFactory.CreateLogger<BackupManager>();

            _repository = new ActivityRepository(loggerFactory.CreateLogger<ActivityRepository>(), _config.ApplicationPaths, _fileSystem);
            _repository.Initialize();
        }


        public string SaveBackup()
        {
            var config = _config.GetReportPlaybackOptions();

            if (string.IsNullOrEmpty(config.BackupPath))
            {
                return "No backup path set";
            }

            DirectoryInfo fi = new DirectoryInfo(config.BackupPath);
            _logger.LogInformation("Backup Path : {BackupPath} attributes : {Attributes} exists : {Exists}",
                config.BackupPath, fi.Attributes, fi.Exists);
            if (fi.Exists == false || (fi.Attributes & FileAttributes.Directory) != FileAttributes.Directory)
            {
                return "Backup path does not exist or is not a directory";
            }

            string raw_data = _repository.ExportRawData();

            String fileName = "PlaybackReportingBackup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".tsv";
            string backup_file = Path.Combine(fi.FullName, fileName);
            _logger.LogInformation("Backup Path Final : {BackupFile}", backup_file);

            try
            {
                File.WriteAllText(backup_file, raw_data);
            }
            catch (Exception e)
            {
                return e.Message;
            }

            FileInfo[] files = fi.GetFiles("PlaybackReportingBackup-*.tsv");
            int max_files = config.MaxBackupFiles;
            int files_to_delete = files.Length - max_files;

            _logger.LogInformation(
                "Backup Files Counts Current: {NumberOfFiles} Max: {MaxFiles} ToDelete {FilesToDelete}", files.Length,
                max_files, files_to_delete);

            if (files_to_delete > 0)
            {
                List<string> file_paths = new List<string>();
                foreach (FileInfo file_info in files)
                {
                    file_paths.Add(file_info.FullName);
                    _logger.LogInformation("Existing Backup Files Before: {FileName}", file_info.Name);
                }
                file_paths.Sort();

                for (int file_index = 0; file_index < files_to_delete; file_index++)
                {
                    FileInfo del_file = new FileInfo(file_paths[file_index]);
                    _logger.LogInformation("Deleting backup file : {FullName}", del_file.FullName);
                    del_file.Delete();
                }
            }

            return "Backup saved : " + fileName;
        }

    }
}
