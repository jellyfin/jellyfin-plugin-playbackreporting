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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting
{
    public class EventMonitorEntryPoint : IHostedService, IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly IServerConfigurationManager _config;
        private readonly ILogger<EventMonitorEntryPoint> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IFileSystem _fileSystem;
        private readonly Dictionary<string, PlaybackTracker>? playback_trackers = null;
        private IActivityRepository? _repository;

        public EventMonitorEntryPoint(
            ISessionManager sessionManager,
            IServerConfigurationManager config,
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<EventMonitorEntryPoint>();
            _sessionManager = sessionManager;
            _config = config;
            _fileSystem = fileSystem;
            playback_trackers = new Dictionary<string, PlaybackTracker>();
        }

        private void SessionManager_PlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item?.Id.ToString("N");
            if (playback_trackers != null && playback_trackers.ContainsKey(key))
            {
                try
                {
                    PlaybackTracker tracker = playback_trackers[key];
                    DateTime now = DateTime.Now;
                    if (now.Subtract(tracker.LastUpdated).TotalSeconds > 20) // update every 20 seconds
                    {
                        tracker.LastUpdated = now;
                        _logger.LogInformation("Processing playback tracker : {Key}", key);
                        List<string> event_log = tracker.ProcessProgress(e);
                        if (event_log.Count > 0)
                        {
                            _logger.LogDebug("ProcessProgress : {Events}", string.Join("", event_log));
                        }
                        if (tracker.TrackedPlaybackInfo != null)
                        {
                            _repository?.UpdatePlaybackAction(tracker.TrackedPlaybackInfo);
                        }
                    }
                }
                catch (Exception exp)
                {
                    playback_trackers.Remove(key);
                    throw new Exception("Error saving playback state: " + exp.Message);
                }
            }
            else
            {
                _logger.LogDebug("Playback progress did not have a tracker : {Key}", key);
            }
        }

        private void SessionManager_PlaybackStop(object? sender, PlaybackStopEventArgs e)
        {
            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item?.Id.ToString("N");
            if (playback_trackers != null && playback_trackers.ContainsKey(key))
            {
                _logger.LogInformation("Playback stop tracker found, processing stop : {Key}", key);
                PlaybackTracker tracker = playback_trackers[key];
                List<string> event_log = tracker.ProcessStop(e);
                if (event_log.Count > 0)
                {
                    _logger.LogDebug("ProcessProgress : {Events}", string.Join("", event_log));
                }

                // if playback duration was long enough save the action
                if (tracker.TrackedPlaybackInfo != null)
                {
                    _logger.LogInformation("Saving playback tracking activity in DB");
                    _repository?.UpdatePlaybackAction(tracker.TrackedPlaybackInfo);
                }
                else
                {
                    _logger.LogInformation("Playback stop but TrackedPlaybackInfo not found! not storing activity in DB");
                }

                // remove the playback tracer from the map as we no longer need it.
                playback_trackers.Remove(key);
            }
            else
            {
                _logger.LogInformation("Playback stop did not have a tracker : {Key}", key);
            }
        }

        private void SessionManager_PlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            if (e.MediaInfo == null)
            {
                return;
            }

            if (e.Item != null && e.Item.IsThemeMedia)
            {
                // Don't report theme song or local trailer playback
                return;
            }

            if (e.Users.Count == 0)
            {
                return;
            }

            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item?.Id.ToString("N");
            if (playback_trackers != null && playback_trackers.ContainsKey(key))
            {
                _logger.LogInformation("Existing tracker found! : " + key);

                PlaybackTracker track = playback_trackers[key];
                if (track.TrackedPlaybackInfo != null)
                {
                    _logger.LogInformation("Saving existing playback tracking activity in DB");
                    List<string> event_log = new List<string>();
                    track.CalculateDuration(event_log);
                    if (event_log.Count > 0)
                    {
                        _logger.LogDebug("CalculateDuration : {Events}", string.Join("", event_log));
                    }
                    _repository?.UpdatePlaybackAction(track.TrackedPlaybackInfo);
                }

                _logger.LogInformation("Removing existing tracker : " + key);
                playback_trackers.Remove(key);
            }

            _logger.LogInformation("Adding playback tracker : " + key);
            PlaybackTracker tracker = new PlaybackTracker(_loggerFactory.CreateLogger<PlaybackTracker>());
            tracker.ProcessStart(e);
            playback_trackers?.Add(key, tracker);

            // start a task to report playback started
            _logger.LogInformation("Creating StartPlaybackTimer Task");
            Task.Run(() => StartPlaybackTimer(e));

        }

        public async Task StartPlaybackTimer(PlaybackProgressEventArgs e)
        {
            _logger.LogInformation("StartPlaybackTimer : Entered");
            await Task.Delay(20000);

            try
            {
                var session = _sessionManager.GetSession(e.DeviceId, e.ClientName, "");
                if (session != null)
                {
                    string event_playing_id = e.Item.Id.ToString("N");

                    string event_user_id = e.Users[0].Id.ToString("N");
                    long event_user_id_int = e.Users[0].InternalId;

                    string session_playing_id = "";
                    if (session.NowPlayingItem != null)
                    {
                        session_playing_id = session.NowPlayingItem.Id.ToString("N");
                    }
                    string session_user_id = "";

                    _logger.LogInformation("session.RemoteEndPoint : {RemoteEndPoint}", session.RemoteEndPoint);

                    if (session.UserId != Guid.Empty)
                    {
                        session_user_id = session.UserId.ToString("N");
                    }

                    string play_method = "na";
                    if (session.PlayState != null && session.PlayState.PlayMethod != null)
                    {
                        play_method = session.PlayState.PlayMethod.Value.ToString();
                    }
                    if (session.PlayState != null && session.PlayState.PlayMethod == MediaBrowser.Model.Session.PlayMethod.Transcode)
                    {
                        if(session.TranscodingInfo !=  null)
                        {
                            string video_codec = "direct";
                            if(session.TranscodingInfo.IsVideoDirect == false)
                            {
                                video_codec = session.TranscodingInfo.VideoCodec;
                            }
                            string audio_codec = "direct";
                            if (session.TranscodingInfo.IsAudioDirect == false)
                            {
                                audio_codec = session.TranscodingInfo.AudioCodec;
                            }
                            play_method += " (v:" + video_codec + " a:" + audio_codec + ")";
                        }
                    }

                    string item_name = GetItemName(e.Item);
                    string item_id = e.Item.Id.ToString("N");
                    string item_type = e.MediaInfo.Type.ToString();

                    _logger.LogInformation("StartPlaybackTimer : event_playing_id     = {EventPlayingId}", event_playing_id);
                    _logger.LogInformation("StartPlaybackTimer : event_user_id        = {EventUserId}", event_user_id);
                    _logger.LogInformation("StartPlaybackTimer : event_user_id_int    = {EventUserIdInternal}", event_user_id_int);
                    _logger.LogInformation("StartPlaybackTimer : session_playing_id   = {SessionPlayingId}", session_playing_id);
                    _logger.LogInformation("StartPlaybackTimer : session_user_id      = {SessionUserId}", session_user_id);
                    _logger.LogInformation("StartPlaybackTimer : play_method          = {PlayMethod}", play_method);
                    _logger.LogInformation("StartPlaybackTimer : e.ClientName         = {ClientName}", e.ClientName);
                    _logger.LogInformation("StartPlaybackTimer : e.DeviceName         = {DeviceName}", e.DeviceName);
                    _logger.LogInformation("StartPlaybackTimer : ItemName             = {ItemName}", item_name);
                    _logger.LogInformation("StartPlaybackTimer : ItemId               = {ItemId}", item_id);
                    _logger.LogInformation("StartPlaybackTimer : ItemType             = {ItemType}", item_type);

                    PlaybackInfo play_info = new PlaybackInfo(
                        id: Guid.NewGuid().ToString("N"),
                        date: DateTime.Now,
                        clientName: e.ClientName,
                        deviceName: e.DeviceName,
                        playbackMethod: play_method,
                        userId: event_user_id,
                        itemId: item_id,
                        itemName: item_name,
                        itemType: item_type
                    );

                    if (event_playing_id == session_playing_id && event_user_id == session_user_id)
                    {
                        _logger.LogInformation("StartPlaybackTimer : All matches, playback registered");

                        // update tracker with playback info
                        string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item?.Id.ToString("N");
                        if (playback_trackers != null && playback_trackers.ContainsKey(key))
                        {
                            _logger.LogInformation("Playback tracker found, adding playback info : {Key}", key);
                            PlaybackTracker tracker = playback_trackers[key];
                            tracker.TrackedPlaybackInfo = play_info;

                            _logger.LogInformation("Saving playback tracking activity in DB");
                            _repository?.AddPlaybackAction(tracker.TrackedPlaybackInfo);
                        }
                        else
                        {
                            _logger.LogInformation("Playback trackler not found : {Key}", key);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("StartPlaybackTimer : Details do not match for play item");
                    }

                }
                else
                {
                    _logger.LogInformation("StartPlaybackTimer : session Not Found");
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "StartPlaybackTimer : Unexpected error occurred");
            }
            _logger.LogInformation("StartPlaybackTimer : Exited");
        }

        private static string GetItemName(BaseItem item)
        {
            string item_name = "Not Known";

            if (item == null)
            {
                return item_name;
            }

            if (typeof(Episode) == item.GetType())
            {
                if (item is Episode epp_item)
                {
                    string season_no = "00";
                    if (epp_item.Season != null && epp_item.Season.IndexNumber != null)
                    {
                        season_no = $"{epp_item.Season.IndexNumber:D2}";
                    }
                    string epp_no = "00";
                    if (epp_item.IndexNumber != null)
                    {
                        epp_no = $"{epp_item.IndexNumber:D2}";
                    }
                    item_name = epp_item.Series.Name + " - s" + season_no + "e" + epp_no + " - " + epp_item.Name;
                }
            }
            else if (typeof(Audio) == item.GetType())
            {
                Audio? audio_item = item as Audio;
                string artist = "Not Known";
                var albumArtists = audio_item?.AlbumArtists;
                if (albumArtists != null && albumArtists.Count > 0)
                {
                    artist = string.Join(", ", albumArtists);
                }
                string album = "Not Known";
                if(string.IsNullOrEmpty(audio_item?.Album) == false)
                {
                    album = audio_item.Album;
                }
                item_name = artist + " - " + audio_item?.Name + " (" + album + ")";
            }
            else
            {
                item_name = item.Name;
            }

            return item_name;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("EventMonitorEntryPoint Running");
            var repo = new ActivityRepository(_loggerFactory.CreateLogger<ActivityRepository>(), _config.ApplicationPaths, _fileSystem);
            repo.Initialize();
            _repository = repo;

            _sessionManager.PlaybackStart += SessionManager_PlaybackStart;
            _sessionManager.PlaybackStopped += SessionManager_PlaybackStop;
            _sessionManager.PlaybackProgress += SessionManager_PlaybackProgress;

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStart -= SessionManager_PlaybackStart;
            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStop;
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_repository != null)
            {
                _repository.Dispose();
                _repository = null;
            }
        }

        ~EventMonitorEntryPoint()
        {
            Dispose();
        }
    }
}
