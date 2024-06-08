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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    public class PlaybackTracker
    {
        private bool IsPaused;
        public PlaybackInfo? TrackedPlaybackInfo { set; get; }
        private readonly ILogger<PlaybackTracker> _logger;
        private readonly List<KeyValuePair<DateTime, ActionType>> event_tracking = new List<KeyValuePair<DateTime, ActionType>>();
        public DateTime LastUpdated = DateTime.MinValue;

        private enum ActionType { START, STOP, PAUSE, UNPAUSE, NONE }

        public PlaybackTracker(ILogger<PlaybackTracker> logger)
        {
            _logger = logger;
        }

        public List<string> ProcessProgress(PlaybackProgressEventArgs e)
        {
            List<string> event_log = new List<string>();
            if (IsPaused != e.IsPaused)
            {
                KeyValuePair<DateTime, ActionType> play_event;
                if (e.IsPaused)
                {
                    play_event = new KeyValuePair<DateTime, ActionType>(DateTime.Now, ActionType.PAUSE);
                    event_log.Add("PauseEvent(" + play_event.Key + ")");
                }
                else
                {
                    play_event = new KeyValuePair<DateTime, ActionType>(DateTime.Now, ActionType.UNPAUSE);
                    event_log.Add("UnPaused Event(" + play_event.Key + ")");
                }
                event_tracking.Add(play_event);

                IsPaused = e.IsPaused;
            }

            CalculateDuration(event_log);

            return event_log;
        }

        public void ProcessStart(PlaybackProgressEventArgs e)
        {
            IsPaused = e.IsPaused;
            KeyValuePair<DateTime, ActionType> play_event = new KeyValuePair<DateTime, ActionType>(DateTime.Now, ActionType.START);
            event_tracking.Add(play_event);
            _logger.LogInformation("PlaybackTracker : Adding Start Event : {PlayEventKey}", play_event.Key);
        }

        public List<string> ProcessStop(PlaybackStopEventArgs e)
        {
            IsPaused = e.IsPaused;
            KeyValuePair<DateTime, ActionType> play_event = new KeyValuePair<DateTime, ActionType>(DateTime.Now, ActionType.STOP);
            event_tracking.Add(play_event);
            _logger.LogInformation("PlaybackTracker : Adding Stop Event : {PlayEventKey}", play_event.Key);

            List<string> event_log = new List<string>();
            CalculateDuration(event_log);
            return event_log;
        }

        public void CalculateDuration(List<string> eventLog)
        {
            int duration = 0;

            if (TrackedPlaybackInfo == null)
            {
                return;
            }

            List<KeyValuePair<DateTime, ActionType>> events;
            // if the last event is not a stop event then add one to allow duration calculation to work
            if (event_tracking.Count > 0 && event_tracking[^1].Value != ActionType.STOP)
            {
                events = new List<KeyValuePair<DateTime, ActionType>>();
                foreach (KeyValuePair<DateTime, ActionType> e in event_tracking)
                {
                    events.Add(e);
                }
                KeyValuePair<DateTime, ActionType> stop_event = new KeyValuePair<DateTime, ActionType>(LastUpdated, ActionType.STOP);
                events.Add(stop_event);
            }
            else
            {
                events = event_tracking;
            }

            eventLog.Add("EventCount(" + events.Count + ")");

            KeyValuePair<DateTime, ActionType> prev_event = new KeyValuePair<DateTime, ActionType>(DateTime.Now, ActionType.NONE);

            foreach (KeyValuePair<DateTime, ActionType> e in events)
            {
                eventLog.Add("Event(" + e.Key + "," + e.Value + ")");
                if (prev_event.Value != ActionType.NONE)
                {
                    ActionType action01 = prev_event.Value;
                    ActionType action02 = e.Value;
                    // count up the activity that is considered PLAYING i.e. the client was actually playing and not paused
                    if ((action01 == ActionType.START || action01 == ActionType.UNPAUSE) && (action02 == ActionType.STOP || action02 == ActionType.PAUSE))
                    {
                        TimeSpan diff = e.Key.Subtract(prev_event.Key);
                        double diff_seconds = diff.TotalSeconds;
                        duration += (int)diff_seconds;
                        eventLog.Add("Diff(" + (int)diff_seconds + ","+ duration + ")");
                    }
                }
                prev_event = e;
            }

            eventLog.Add("Total(" + duration + ")");
            TrackedPlaybackInfo.PlaybackDuration = duration;
        }
    }
}
