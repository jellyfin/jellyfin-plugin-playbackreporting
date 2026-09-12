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
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PlaybackReporting.Data;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaybackReporting.Api
{
    [ApiController]
    [Authorize]
    [Route("user_usage_stats")]
    [Produces(MediaTypeNames.Application.Json)]
    public class PlaybackReportingOfflineActivityController : ControllerBase, IDisposable
    {
        private readonly ILogger<PlaybackReportingOfflineActivityController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IAuthorizationContext _authContext;

        private readonly IActivityRepository _repository;

        public PlaybackReportingOfflineActivityController(
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager config,
            ILibraryManager libraryManager,
            IAuthorizationContext authContext)
        {
            _logger = loggerFactory.CreateLogger<PlaybackReportingOfflineActivityController>();
            _libraryManager = libraryManager;
            _authContext = authContext;

            var repo = new ActivityRepository(loggerFactory.CreateLogger<ActivityRepository>(), config.ApplicationPaths, fileSystem);
            _repository = repo;
        }

        public class OfflinePlaybackData
        {
            public Guid ItemId { get; set; }
            public DateTime Date { get; set; }
            public int PlayDuration { get; set; }
        }

        public class OfflinePlaybackResult
        {
            public string Status { get; set; } = "";
            public string? Message { get; set; }
        }

        /// <summary>
        /// Reports playback that has already happened, such as playback from a client that was offline.
        /// </summary>
        /// <param name="plays">
        /// The plays to report. Date must be an ISO 8601 timestamp carrying an offset or a trailing Z, and must not be
        /// in the future. PlayDuration is the number of seconds spent playing, excluding paused time. Do not send the
        /// run time of the item or the position reached. Send 0 if the client did not measure it.
        /// </param>
        /// <param name="userId">Optional. The user the plays belong to, defaulting to the authenticated user. Naming another user requires administrator rights. Required when authenticating with an api key, which has no user of its own.</param>
        /// <response code="200">Plays processed. One result is returned per submitted play, in the order they were submitted.</response>
        /// <response code="400">Authenticated with an api key and no userId was given.</response>
        /// <response code="403">Naming another user without administrator rights.</response>
        /// <returns></returns>
        [HttpPost("offline_activity")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<List<OfflinePlaybackResult>>> ReportOfflineActivity([FromBody] List<OfflinePlaybackData> plays, [FromQuery] Guid? userId)
        {
            AuthorizationInfo auth = await _authContext.GetAuthorizationInfo(Request);

            // an api key authenticates without a user, so the caller has to name one
            Guid target_user_id = userId ?? auth.UserId;
            if (target_user_id.Equals(Guid.Empty))
            {
                return BadRequest("A userId is required when authenticating with an api key");
            }

            bool is_elevated = auth.IsApiKey || auth.User?.HasPermission(PermissionKind.IsAdministrator) == true;
            if (target_user_id.Equals(auth.UserId) == false && is_elevated == false)
            {
                _logger.LogWarning("Offline activity reported for another user without elevation : {UserId}", target_user_id);
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            string user_id = target_user_id.ToString("N");
            string client_name = string.IsNullOrEmpty(auth.Client) ? "Not Known" : auth.Client;
            string device_name = string.IsNullOrEmpty(auth.Device) ? "Not Known" : auth.Device;

            _logger.LogInformation("Reporting {PlayCount} offline plays for {UserId} from {ClientName}", plays.Count, user_id, client_name);

            List<OfflinePlaybackResult> results = new List<OfflinePlaybackResult>();
            DateTime now = DateTime.Now;

            foreach (OfflinePlaybackData play in plays)
            {
                DateTime play_date = play.Date.ToLocalTime();

                if (play_date > now)
                {
                    results.Add(new OfflinePlaybackResult { Status = "Error", Message = "Date is in the future" });
                    continue;
                }

                if (play.PlayDuration < 0)
                {
                    results.Add(new OfflinePlaybackResult { Status = "Error", Message = "PlayDuration is negative" });
                    continue;
                }

                BaseItem? item = _libraryManager.GetItemById(play.ItemId);
                if (item == null)
                {
                    results.Add(new OfflinePlaybackResult { Status = "Error", Message = "Item not found : " + play.ItemId.ToString("N") });
                    continue;
                }

                if (item.IsThemeMedia)
                {
                    results.Add(new OfflinePlaybackResult { Status = "Skipped", Message = "Theme media is not reported" });
                    continue;
                }

                PlaybackInfo play_info = new PlaybackInfo(
                    id: Guid.NewGuid().ToString("N"),
                    date: play_date,
                    clientName: client_name,
                    deviceName: device_name,
                    playbackMethod: "Offline",
                    userId: user_id,
                    itemId: play.ItemId.ToString("N"),
                    itemName: EventMonitorEntryPoint.GetItemName(item),
                    itemType: item.GetBaseItemKind().ToString()
                );
                play_info.PlaybackDuration = play.PlayDuration;

                try
                {
                    bool added = _repository.AddPlaybackActionIfMissing(play_info);
                    results.Add(added
                        ? new OfflinePlaybackResult { Status = "Added" }
                        : new OfflinePlaybackResult { Status = "Skipped", Message = "Already reported" });
                }
                catch (SQLiteException exp)
                {
                    // the event monitor writes on a separate connection and lock, so SQLITE_BUSY can land
                    // here. Catching per play keeps one failure from taking the whole batch with it.
                    _logger.LogError(exp, "Error storing offline play : {ItemId}", play_info.ItemId);
                    results.Add(new OfflinePlaybackResult { Status = "Error", Message = "Database was busy, retry this play" });
                }
            }

            return results;
        }

        public void Dispose()
        {
            _repository.Dispose();
        }
    }
}
