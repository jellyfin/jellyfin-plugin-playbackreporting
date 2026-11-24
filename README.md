# Jellystics

Jellyfin 10.11 plugin that records every playback precisely from server events (real watch time, pauses, seeks, audio/subtitle tracks, transcoding), keeps the full history in its own SQLite database, imports the official Playback Reporting history, and serves it to Jellystics through a cursor-based sync API.

## Install

Add this repository manifest URL in Jellyfin (Dashboard > Plugins > Repositories):

`https://raw.githubusercontent.com/Jellystics/jellystics-plugin-playbackreporting/master/manifest.json`

## Credits

Based on jellyfin-plugin-playbackreporting (GPL-3).

## Purge

Dashboard > Plugins > Jellystics can delete closed plays older than a date. The purge is local to the plugin: it is not propagated to Jellystics, and importing the official Playback Reporting history again restores purged rows that came from it.

## My statistics page

Every Jellyfin user gets a "My statistics" entry in the avatar menu (2nd, after Profile). It opens `/Jellystics/Me/`, a page served by Jellystics through the plugin, showing only that user's statistics.

- Jellystics links itself: on sync it calls `POST /Jellystics/Link` (admin) with its URL as seen from Jellyfin and a secret. Dashboard > Plugins > Jellystics shows the link and reachability, and has a URL override for when the pushed address is wrong.
- The browser only talks to Jellyfin. `/Jellystics/Me/api/*` requires a Jellyfin login; the plugin forwards to Jellystics `/api/me/*` with the secret and the user id taken from the Jellyfin authentication (`X-Jellystics-Secret`, `X-Jellystics-User`), never from the browser.
- When Jellystics is not linked or not reachable the page shows a "Statistics unavailable" notice.
- The menu entry comes from a small script (`/Jellystics/Me/client.js`) injected into jellyfin-web's `index.html`.

## Development

Requires the Nix dev shell (`nix develop`, or direnv): dotnet 9, node, go-task, docker, sqlite.

- `task test` runs the unit tests (xUnit, then `node --test` for the pure helpers of `client.js`).
- `task build` builds the plugin, copies the dll to `config/plugins/Jellystics/` and restarts the dev Jellyfin (`docker compose up -d`, http://localhost:8097, container `jellystics-plugin-jellyfin`). `config/` and `media/` are git-ignored.

### Manual end-to-end check

1. `docker compose up -d`, `task build`, open http://localhost:8097, create the admin and add `/media` as a library with a few short clips.
2. Check the log: `docker logs jellystics-plugin-jellyfin 2>&1 | grep -i jellystics` (the plugin must load).
3. Play a clip 60 s, pause 30 s, seek forward 2 min, resume 30 s, stop, then:
   `sqlite3 config/plugins/Jellyfin.Plugin.Jellystics/jellystics.db "select WatchedMs, PausedMs, PauseCount, SeekCount, SkippedMs, State from Plays"`
   (on macOS, read the db after `docker stop`/`docker restart`, or copy `jellystics.db*` first: the WAL of a running container is not visible through the bind mount) expect about `90000 | 30000 | 1 | 1 | ~120000 | closed`.
4. Restart the container mid-play: the play is closed at its last checkpoint.
5. Dashboard > Plugins > Jellystics: import a copied `playback_reporting.db` and check the counts. Re-running the import is safe and also fills `durationRepaired` on rows imported by an earlier plugin version.
