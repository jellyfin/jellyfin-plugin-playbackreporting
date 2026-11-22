# Jellystics

Jellyfin 10.11 plugin that records every playback precisely from server events (real watch time, pauses, seeks, audio/subtitle tracks, transcoding), keeps the full history in its own SQLite database, imports the official Playback Reporting history, and serves it to Jellystics through a cursor-based sync API.

## Install

Add this repository manifest URL in Jellyfin (Dashboard > Plugins > Repositories):

`https://raw.githubusercontent.com/Jellystics/jellystics-plugin-playbackreporting/master/manifest.json`

## Credits

Based on jellyfin-plugin-playbackreporting (GPL-3).

## Development

Requires the Nix dev shell (`nix develop`, or direnv): dotnet 9, go-task, docker, sqlite.

- `task test` runs the unit tests.
- `task build` builds the plugin, copies the dll to `config/plugins/Jellystics/` and restarts the dev Jellyfin (`docker compose up -d`, http://localhost:8097, container `jellystics-plugin-jellyfin`). `config/` and `media/` are git-ignored.

### Manual end-to-end check

1. `docker compose up -d`, `task build`, open http://localhost:8097, create the admin and add `/media` as a library with a few short clips.
2. Check the log: `docker logs jellystics-plugin-jellyfin 2>&1 | grep -i jellystics` (the plugin must load).
3. Play a clip 60 s, pause 30 s, seek forward 2 min, resume 30 s, stop, then:
   `sqlite3 config/plugins/Jellyfin.Plugin.Jellystics/jellystics.db "select WatchedMs, PausedMs, PauseCount, SeekCount, SkippedMs, State from Plays"`
   (on macOS, read the db after `docker stop`/`docker restart`, or copy `jellystics.db*` first: the WAL of a running container is not visible through the bind mount) expect about `90000 | 30000 | 1 | 1 | ~120000 | closed`.
4. Restart the container mid-play: the play is closed at its last checkpoint.
5. Dashboard > Plugins > Jellystics: import a copied `playback_reporting.db` and check the counts.
