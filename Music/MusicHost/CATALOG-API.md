# MeloDesk catalogue API · 0.15.0

The native Telegram client talks to `MeloDesk.MusicHost.exe` through its existing UTF-8 JSON-lines stdin/stdout connection. No public port, account credentials or additional Telegram login is required. Each request has a string `id`; its reply contains the same `Id`, `Ok`, and either `Result` or `Error`. Playback events have no `Ok` field and must not be mistaken for replies.

## Commands

```json
{"id":"1","command":"search","query":"Oasis Wonderwall"}
{"id":"2","command":"discover"}
{"id":"3","command":"artist","artistId":"UCmMUZbaYdNH0bEd1PAlAqsA"}
{"id":"4","command":"trackStats","videoId":"hpSrLjc5SMs"}
```

- `search`: `Tracks`, `Artists`, `Messages`. Default search uses **YouTube Music**; `source:"SoundCloud"` selects SoundCloud (see below). Track URLs remain canonical YouTube watch URLs for playback and playlist compatibility. Search returns the source's first song page and up to four artist cards; it is not the complete catalogue.
- `discover`: `Artists`, `Source`. A small editorial selection of artist cards; these are not personalized recommendations.
- `artist`: `Id`, `Name`, `Thumbnail`, `Description`, `MonthlyListeners`, `Subscribers`, `Tracks`, `Albums`, `TrackContinuation`, `AlbumContinuation`, `Source`, `RetrievedAt`. The ID is the music artist browse ID, not an interchangeable uploader/channel subscription ID. Songs use the artist's full songs shelf when provided by the source; albums include albums, singles and EPs. These are first pages, with no local 20/30-song truncation.
- `trackStats`: `VideoId`, nullable `Plays`, `Source`. Uses recently fetched catalogue metadata first. Otherwise looks up the song title and requests musical search results, accepting statistics **only for that exact video ID**. Missing matches and unpublished statistics return `null`.

Track fields add optional `ArtistId`, `Album`, and `Plays`. Older playlists without these fields still load. Existing SoundCloud entries remain playable; they are not included in new catalogue searches.

In 0.12.0 search replies also include `Query`. The native client searches after a 300 ms typing pause (minimum two characters), cancels superseded searches and ignores replies whose query no longer matches the input. Explicit Enter and Find remain available.

## Favorite artists and playback clicks

`library` also returns `FavoriteArtists`, a local per-account list of artist cards. Send `{"id":"artists-1","command":"favoriteArtist","artist":{"Id":"UCmMUZbaYdNH0bEd1PAlAqsA","Name":"Oasis","Thumbnail":null,"MonthlyListeners":null},"favorite":true}` to select an artist, or `favorite:false` to remove it. The reply contains the updated `FavoriteArtists`. Changes are idempotent and persisted before acknowledgment, in a separate DPAPI-protected `artists.dat` beside `playlists.dat`; no server synchronization is used. Up to 200 artists can be selected. The home screen displays only selected favorite artists and offers search-based selection through card hearts; it does not request or show the editorial `discover` selection.

`select` accepts optional `toggle:true`. If the selected URL is currently playing it pauses; if paused it resumes the same playback request and position; if still loading it cancels that load. Ended, stopped or failed tracks start normally. Without `toggle`, existing explicit selection behavior is retained. Playback events report the actual asynchronous audio state; a select reply can precede a pause/resume event.

Album cards are sorted in the native UI by descending numeric year, globally across loaded pages; missing years remain last, with stable order within a year.

## Discography and pagination

```json
{"id":"5","command":"artistPage","artistId":"UCpN-UcqHLvtu3kYkxLcu4hA","kind":"tracks","continuation":"<TrackContinuation>"}
{"id":"6","command":"artistPage","artistId":"UCpN-UcqHLvtu3kYkxLcu4hA","kind":"albums","continuation":"<AlbumContinuation>"}
{"id":"7","command":"album","albumId":"MPREb_xAEHFeEf9A1"}
{"id":"8","command":"albumPage","albumId":"MPREb_xAEHFeEf9A1","continuation":"<Continuation>"}
```

`artistPage` returns `{Page,Kind,Previous}`; `albumPage` returns `{Page,Previous}`. `Page` contains `OwnerId`, `Tracks`, `Albums`, and nullable `Continuation`. Append and deduplicate by track URL or album ID only when owner, section and `Previous` match the current page. A null continuation means the source supplied no next page. Cursors are opaque, scoped to owner/section, expire with the bounded cache and do not survive a host restart; reopen the artist or album to obtain a new one.

An album card contains `Id`, `Name`, `Artist`, nullable `Thumbnail`, `Year`, and `Type`. `album` returns `Id`, `Name`, `Artist`, `Thumbnail`, `Year`, `Tracks`, and nullable `Continuation`. Availability is limited to public catalogue metadata and regional source restrictions.

## Local library and likes

```json
{"id":"9","command":"library"}
{"id":"10","command":"like","liked":true,"track":{"Title":"Example","Artist":"Artist","Provider":"YouTube Music","Url":"https://www.youtube.com/watch?v=GMHdFXq9hUU","Duration":244,"IsLocal":false}}
{"id":"11","command":"playlistChange","kind":"create","playlistId":"<new UUID>","name":"My playlist","track":null}
```

The native client supplies its Telegram account ID when starting the host. `library` returns `{Playlists,Pending}` for that account. Each playlist has `Id`, `Name`, `Tracks`, and `IsFavorites`. The reserved favorites ID is `00000000-0000-0000-0000-000000000001`; it exists even for a new account and cannot be renamed or deleted. `like` adds/removes by canonical track URL and is idempotent. A new playlist can include a non-null `track` in the same `create` request, committing both operations together.

`playlistChange` supports `create`, `rename`, `delete`, `add`, `remove` with the existing `playlistId`, `name`, `track`, `trackUrl` fields. In **0.9.0**, mutation replies return `{Change,Pending}` rather than the entire library. `Change` contains `Kind`, `PlaylistId`, optional `Name`, `Track`, and `TrackUrl`. The matching native client applies these deltas to its cached library; request `library` for a complete snapshot. The JSON-lines envelope remains protocol 1; ship host and native client from the same release. Local persistence completes before a success reply. Errors do not expose an uncommitted library snapshot.

## Statistics

```json
{
  "Display":"1.7B",
  "Count":null,
  "Approximate":true,
  "Unit":"plays",
  "Source":"YouTube Music",
  "RetrievedAt":"2026-10-28T10:15:26Z"
}
```

`Display` preserves the value published by the source. A rounded `K`/`M`/`B` value has `Approximate:true` and no fabricated exact `Count`. An exact integer can populate `Count`. Units are `plays`, `monthlyListeners`, or `subscribers`. Subscribers, video views, local playback events, and monthly audience are never substituted for one another. An absent metric is `null`, not zero. The UI names the source and displays an unavailable state where needed.

Requests use public YouTube Music metadata without a Google account. This is an unofficial adapter to the web catalogue, whose response structure and regional availability can change. It uses four concurrent HTTP slots, cancellation, a 12-second request timeout, and a bounded 15-minute memory cache. It does not bypass sign-in or region restrictions. Metadata retrieval is separate from audio resolution and never changes the active playback queue.

`Моя волна` is an explicitly labeled **В разработке** screen. It does not claim to implement Yandex's recommendation algorithm. Playlists remain encrypted locally and separated by the Telegram account; this change does not upload them to a server.

Reference response shapes: [ytmusicapi search](https://ytmusicapi.readthedocs.io/en/stable/reference/search.html), [artist browsing](https://ytmusicapi.readthedocs.io/en/latest/reference/browsing.html). The C# adapter is implemented in `Music/YouTubeMusicCatalog.cs` and requires no Python runtime or additional API key in the distributed app.

## Search discovery (0.12.0)

`{"id":"discovery","command":"searchDiscovery"}` returns `{Albums,Artists,Messages,Source,RetrievedAt}`. Albums are up to 12 distinct available cards from the source's Explore / New albums & singles shelf. No release year or audience number is invented. Artist cards are an editorial selection, not personalized recommendations. Responses use an independent 15-minute cache and never replace search tracks or the playback queue. Failed release requests return `Messages` and are not cached, allowing retry. The native client requests this only for empty Search; late responses are cached without replacing an active query, artist page or album. Genre/mood cards open source playlists (see below). Typing retains the existing artist and track results; clearing restores discovery. Home continues to show only favorite artists.

## Genre and mood playlists (0.12.0)

`{"id":"genre","command":"genre","key":"rock"}` returns `{Key,Name,SourcePlaylistId,SourcePlaylistName,Tracks,Continuation,Source}`. Allowed keys: `pop`, `rock`, `hiphop`, `electronic`, `focus`, `evening`, `indie`, `energy`. The catalogue discovers public playlists for the genre, prefers YouTube Music curated playlist identifiers and reads the selected playlist's actual tracks. It tries up to three available playlists and caches a successful result independently for 15 minutes; failures remain retryable. This is a source playlist, not a list of song search hits or a new local user playlist.

`{"id":"more","command":"genrePage","key":"rock","continuation":"<opaque cursor>"}` returns `{Page,Previous}` with the existing catalogue page shape. Cursors belong to their genre and cannot be used with another genre or an artist. The source may end with an empty final page. The native client discards replies superseded by another genre request. Genre navigation does not overwrite typed search results or start audio; Play queues the displayed playlist. Local likes and Add to playlist remain available for every track.

Genre and mood card previews are native vector drawings: microphone, electric guitar, turntable, synthesizer, headphones, crescent moon, acoustic guitar and speaker. Decorative note captions have been removed; names are vertically centered. Collection cards retain their track count without a note symbol.

## Search source switch (0.13.0)

`search` accepts `source: "YouTube Music"` (default) or `source: "SoundCloud"`. The reply contains `{Tracks,Artists,Messages,Query,Source}`. YouTube Music keeps its existing artist cards and track catalogue. SoundCloud uses the existing bundled yt-dlp search adapter and returns track results inside the native client; it does not open a website. Unsupported source names are rejected. Source switches share the latest-search cancellation guard, including cancellation of the retired search subprocess; the native client also checks the echoed query and source. Search does not start playback. Existing select/queue/likes/playlist operations accept canonical SoundCloud track URLs. Availability and preview restrictions depend on the source.

The native search field and Find button share a text center line. A wrapping, clickable label under the field switches to SoundCloud using the current query, and becomes a return link to YouTube Music. It is shown only on Search. The home discovery page still belongs to YouTube Music, and choosing favorite artists resets the source to YouTube Music.

Исправление ссылки SoundCloud: индекс ссылки явно связан с LambdaClickHandler после каждого обновления надписи. Ссылка открывает поиск SoundCloud с текущим запросом; «Назад» возвращает поиск YouTube Music. Подсказка в обоих поисках — «Трек или исполнитель». Проверка мышью оставлена пользователю по его просьбе.


## Local transfer and player controls (0.14.0)

Ship native client and MusicHost 0.14.0 together. Protocol envelope stays version 1. No HTTP port, second Telegram session, external server or recommendation model is introduced.

```json
{"id":"q","command":"queue","tracks":[],"startUrl":"<selected URL>"}
{"id":"qs","command":"queueState","offset":0}
{"id":"o","command":"controls","shuffle":true,"repeat":"all","eqEnabled":true,"preamp":-2,"bands":[0,0,0,0,0,0,0,0,0,0],"muted":false}
{"id":"d","command":"dislike","track":{"Title":"Example","Artist":"Artist","Provider":"YouTube Music","Url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","Duration":213},"disliked":true}
{"id":"v","command":"volume","value":70}
{"id":"s","command":"seek","position":0.5}
{"id":"l","command":"lyrics","track":{"Title":"Example","Artist":"Artist","Provider":"YouTube Music","Url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","Duration":213}}
```

`state`, `controls` and `dislike` return `{Playback,Time,Length,Volume,Muted,Options,Disliked}`. Times are milliseconds, seek is 0..1. Options: Shuffle, Repeat (`off`, `all`, `one`), EqEnabled, Preamp and 10 Bands (-20..20 dB), Volume, Muted. Omitted control properties retain their previous value. Options/dislikes are saved atomically to per-account DPAPI `player.dat`. Mute uses this player's volume without changing Windows session mute. Volume changes unmute the app. Queue state is paged in blocks of 100 and contains `{Tracks,Total,Offset,CurrentIndex}`; shuffle retains the current track first, Next skips disliked songs, manual Next advances even in repeat-one. Previous restarts a seekable song after 3 seconds, otherwise chooses the previous song. Like removes that URL's dislike.

`lyrics` returns `{TrackUrl,Text,Synced,Source,Instrumental}`. The UI shows plain text; synced-line highlighting is not implemented. Only explicit requests contact LRCLIB, with bounded caching/timeouts and retry throttling. Local lyrics come from managed `.lyrics.txt`, copied from tags or an adjacent `.lrc` during local import.

```json
{"id":"files","command":"localAdd","paths":["C:\\Music\\Song.flac"],"playlistId":"<optional existing ID>","name":"My songs"}
{"id":"p","command":"importPreview","input":"https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M","file":false}
{"id":"csv","command":"importPreview","input":"C:\\Music\\export.csv","file":true}
{"id":"r","command":"importResolve","previewId":"<Id>"}
{"id":"c","command":"importChoice","previewId":"<Id>","index":0,"url":"<one of Candidates URLs>"}
{"id":"cancel","command":"importCancel"}
{"id":"save","command":"importCommit","previewId":"<Id>","name":"Transferred playlist"}
```

Local import accepts MP3/FLAC/M4A/OGG/OPUS/WAV/AAC/WMA, up to 1000 files, 1 GiB each. It copies content-hashed audio into `accounts/<account-id>/audio`; originals are untouched. Metadata comes from TagLibSharp. Artwork is a managed cover file; the native client accepts only this account's hash-named cover files and decodes off the GUI thread. Playlist operations allow `IsLocal:true` file URLs only in local mode; cloud merge continues to reject them. `localAdd` replies `{Playlists,Pending,PlaylistId,Added}` after local save.

Preview accepts public YouTube/YouTube Music playlist URLs, Spotify `/playlist/<id>`, SoundCloud `/artist/sets/<name>`, Yandex `/users/<owner>/playlists/<kind>` or `/playlists/<UUID>`, or CSV/JSON files up to 8 MiB and 1000 entries. CSV supports comma/semicolon, escaped quotes, multiline fields, Title/Artist/URL/Duration aliases and Exportify Track Name/Artist Name(s)/Duration(ms)/Track URI. JSON supports common tracks/items/entries/trackList layouts and wrapped Spotify/Yandex track rows. Private and unavailable playlists require exports; no cookies or service account tokens are obtained.

Preview contains `{Id,Name,Entries,Source,CreatedAt,Matched,Unresolved}`. Entry fields are Title, Artist, Duration(seconds), SourceUrl, Match, Status, Candidates. YouTube and SoundCloud URLs are playable directly. SoundCloud URL-only playlist rows are enriched through public oEmbed (3 concurrent requests, bounded timeout); Yandex public metadata uses an anonymous catalogue endpoint. Spotify uses public embed metadata and does not download Spotify audio. Public formats and counts are controlled by each source.

Resolve searches YouTube Music song metadata in batches of 3. Artist/title/duration and version markers must match conservatively; ambiguous live/remix/cover variants require manual selection. Candidates are capped at 5. Progress events: `{Event:"importProgress",PreviewId,Done,Total}`. A new import/cancel retires ongoing matching; native UI ignores superseded replies. Previews are memory-only, bounded to 8 and expire after 2 hours.

Commit imports only confirmed matches atomically into a new local playlist and returns `{Playlists,Pending,PlaylistId,Added,Unresolved}`. A DPAPI report preserving all original rows and unresolved choices is saved under `accounts/<account-id>/imports/<preview-id>.dat`; no audio or private credentials are included. A committed preview cannot be reused accidentally. Import, search and browsing never start playback. Library snapshot responses to imports do not clear favorite artists.

Five-artist onboarding and a local artist-graph recommendation engine are implemented in0.19.0. Behavior tracking, population training and audio analysis remain future work.


## Native volume popover (0.14.1)

The native 0.14.1 player uses the unchanged 0.14.0 MusicHost protocol. A vertical hover popover replaces the permanent volume strip at all widths. Drag/progress changes are coalesced to at most one volume command per 80 ms, with an immediate final value. The native MediaSlider supplies wheel and drag behavior. UI opacity and slide animations use the Telegram animation engine (160 ms), with no permanent animation timer. Pending local slider changes temporarily ignore stale state polls. Window deactivation and hiding close the sibling popover. No Windows mixer mute command is introduced. Visual verification is manual.


## Audio volume smoothing (0.15.0)

`volume` and internal mute retain their existing desired-value protocol. The audio engine applies active-player gain through a 180ms smoothstep ramp, sampled at 15ms only while changing. New targets start at the current interpolated gain; stopped/new streams retain the target setting. No OS-wide session mute is changed.


## Local taste recommendations (0.19.0)

- `artistChoices {query}`: `{Query, Artists}`. Empty query gives searchable starter choices; superseded requests cancel.
- `library` and `favoriteArtist` include `OnboardingCompleted` in their existing results.
- `completeTaste`: validates at least five distinct favorite artists, persists account-local onboarding, returns `{OnboardingCompleted, FavoriteArtists}`.
- `recommendations {requestKey, refresh?}`: `{RequestKey, Feed:{SeedKey,SeedNames,Artists,Albums,Messages,GeneratedAt,Cached}}`. Seeds are read from the account's saved preferences, not trusted caller payloads. `SeedKey` is ordinal-sorted distinct artist IDs joined with `|`. New generation cancels the previous one. UI checks request key and seed fingerprint before accepting a result. Offline fallback never crosses fingerprints/accounts.
