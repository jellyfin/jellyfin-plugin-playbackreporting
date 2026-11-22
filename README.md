# Jellystics

Jellyfin 10.11 plugin that records every playback precisely from server events (real watch time, pauses, seeks, audio/subtitle tracks, transcoding), keeps the full history in its own SQLite database, imports the official Playback Reporting history, and serves it to Jellystics through a cursor-based sync API.

## Install

Add this repository manifest URL in Jellyfin (Dashboard > Plugins > Repositories):

`https://raw.githubusercontent.com/Jellystics/jellystics-plugin-playbackreporting/main/manifest.json`

## Credits

Based on jellyfin-plugin-playbackreporting (GPL-3).
