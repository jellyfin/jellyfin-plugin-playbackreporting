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
using System.Security.Claims;
using Jellyfin.Plugin.PlaybackReporting.Data;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting.Api
{
    /// <summary>
    /// Non-admin endpoints that let an authenticated user read their own playback
    /// stats. Mirrors the shape of the admin <see cref="PlaybackReportingActivityController"/>
    /// but scoped to the caller, so the Schildi client can show "Bildschirmzeit"
    /// without elevated rights.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("user_usage_stats")]
    [Produces(MediaTypeNames.Application.Json)]
    public class UserSelfReportingController : ControllerBase
    {
        private readonly ILogger<UserSelfReportingController> _logger;
        private readonly IActivityRepository _repository;

        public UserSelfReportingController(
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager config)
        {
            _logger = loggerFactory.CreateLogger<UserSelfReportingController>();
            _repository = new ActivityRepository(loggerFactory.CreateLogger<ActivityRepository>(), config.ApplicationPaths, fileSystem);
        }

        private string? GetCurrentUserId()
        {
            // Jellyfin stores the authenticated user's GUID in the "Jellyfin-UserId"
            // claim (hyphen — NOT "Jellyfin:UserId"; Jellyfin also does not set
            // NameIdentifier). Value is the GUID in "N" form (no dashes).
            var claim = User.FindFirst("Jellyfin-UserId")
                        ?? User.FindFirst(ClaimTypes.NameIdentifier);
            return claim?.Value;
        }

        /// <summary>
        /// Gets the daily watchtime (seconds per day) for the currently authenticated
        /// user. Mirrors the admin <c>PlayActivity</c> endpoint but is scoped to the
        /// caller, so non-admin users can read their own data. Missing days in the
        /// period are filled with 0; the result is a single-element list with the same
        /// shape as the admin endpoint (<c>[{ user_id, user_usage }]</c>).
        /// </summary>
        /// <param name="days">Number of days to include.</param>
        /// <param name="endDate">Optional end date. Defaults to now.</param>
        /// <param name="filter">Comma separated list of media types (e.g. Movie,Episode). An empty filter yields no rows.</param>
        /// <param name="dataType">Data type to return (count,time). Defaults to time.</param>
        /// <param name="timezoneOffset">Timezone offset in hours.</param>
        /// <response code="200">Activity returned.</response>
        /// <response code="401">Not authenticated.</response>
        [HttpGet("me/PlayActivity")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetMyPlayActivity([FromQuery] int days, [FromQuery] DateTime? endDate, [FromQuery] string? filter, [FromQuery] string? dataType, [FromQuery] float? timezoneOffset)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
            {
                return Unauthorized();
            }

            _logger.LogDebug("GetMyPlayActivity: userId={UserId} days={Days}", userId, days);

            string[] filterTokens = filter?.Split(',') ?? Array.Empty<string>();
            endDate ??= DateTime.Now;

            Dictionary<string, Dictionary<string, int>> results =
                _repository.GetUsageForDays(days, endDate.Value, filterTokens, dataType, timezoneOffset ?? 0);

            // Datensatz des aktuellen Nutzers heraussuchen (GUID mit/ohne Bindestriche).
            string normalizedUserId = userId.Replace("-", string.Empty).ToLowerInvariant();
            Dictionary<string, int> userUsage = new Dictionary<string, int>();
            foreach (var entry in results)
            {
                if (entry.Key.Replace("-", string.Empty).ToLowerInvariant() == normalizedUserId)
                {
                    userUsage = entry.Value;
                    break;
                }
            }

            // Fehlende Tage im Zeitraum mit 0 auffüllen (wie im Admin-Endpoint).
            SortedDictionary<string, int> userUsageByDate = new SortedDictionary<string, int>();
            DateTime fromDate = endDate.Value.AddDays(days * -1 + 1);
            while (fromDate <= endDate.Value)
            {
                string dateString = fromDate.ToString("yyyy-MM-dd");
                userUsageByDate[dateString] = userUsage.TryGetValue(dateString, out int value) ? value : 0;
                fromDate = fromDate.AddDays(1);
            }

            var userData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["user_id"] = userId,
                    ["user_usage"] = userUsageByDate
                }
            };

            return Ok(userData);
        }

        /// <summary>
        /// Gets the list of recorded media types. Required as the <c>filter</c> for
        /// <see cref="GetMyPlayActivity"/> (an empty filter yields no rows).
        /// </summary>
        /// <response code="200">Type list returned.</response>
        /// <response code="401">Not authenticated.</response>
        [HttpGet("me/type_filter_list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetMyTypeFilterList()
        {
            if (GetCurrentUserId() == null)
            {
                return Unauthorized();
            }

            return Ok(_repository.GetTypeFilterList());
        }
    }
}
