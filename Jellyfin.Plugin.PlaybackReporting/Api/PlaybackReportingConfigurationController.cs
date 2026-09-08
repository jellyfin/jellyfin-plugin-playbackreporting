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

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PlaybackReporting.Api
{
    /// <summary>
    /// Playback Reporting Configuration Controller.
    /// </summary>
    [ApiController]
    [Route("System")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public class PlaybackReportingConfigurationController : ControllerBase
    {
        private readonly IServerConfigurationManager _configurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackReportingConfigurationController"/> class.
        /// </summary>
        /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public PlaybackReportingConfigurationController(IServerConfigurationManager configurationManager)
        {
            _configurationManager = configurationManager;
        }

        /// <summary>
        /// Gets playback reporting configuration.
        /// </summary>
        /// <response code="200">Playback reporting configuration returned.</response>
        /// <returns>Playback reporting configuration.</returns>
        [HttpGet("Configuration/playback_reporting")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<ReportPlaybackOptions> GetPlaybackReportingConfiguration()
        {
            return _configurationManager.GetReportPlaybackOptions();
        }

        /// <summary>
        /// Updates playback reporting configuration.
        /// </summary>
        /// <param name="configuration">Configuration.</param>
        /// <response code="204">Configuration updated.</response>
        /// <returns>Update status.</returns>
        [HttpPost("Configuration/playback_reporting")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdatePlaybackReportingConfiguration([FromBody, Required] ReportPlaybackOptions configuration)
        {
            _configurationManager.SaveReportPlaybackOptions(configuration);
            return NoContent();
        }
    }
}
