# ShibaPlayer

A shiba-themed Windows music player for MP3, M4A and WebM/Opus files, with albums, playlists, shuffle, effects, a visualizer, YouTube downloads and automatic metadata. Same family as ShibaDownloader and [ShibaTag](https://github.com/iaski15/ShibaTag).

- **Its own audio engine:** no player library. Windows' codecs decode, and everything after that is written here.
- **VLC-style effects:** equalizer, compressor, spatializer, stereo widener, and speed and pitch.
- **Metadata that fills itself in:** iTunes, Deezer and SoundCloud, so Russian and niche music gets found too. Only empty tags are touched.
- **YouTube tab:** search, paste a song or playlist link, and download in the quality you pick.

## Install

Download `ShibaPlayer-Setup-<version>.exe` from [Releases](https://github.com/iaski15/ShibaPlayer/releases) and run it. It installs just for you, with no admin needed, and adds a Start menu entry.

Prefer not to install? `ShibaPlayer.exe` from the same release is the portable version. It needs Windows 10 or 11 (64-bit), and nothing else.

## Use

1. Your Music folder opens automatically. To use another one, press **Choose folder** or drop a folder on the window. Subfolders are included, and the folder is remembered for next time.
2. Double-click a song (or press Enter) to play it. The bottom bar has shuffle, previous, play/pause, next, speed, effects, a seek bar and volume. The speed button has Faster, Faster (fine), Normal speed, Slower (fine) and Slower.
   - **Effects tab** (changes are heard within about 0.2 s; double-click a slider to reset it; everything except speed and pitch is remembered):
     - **Equalizer**: on/off, preamp, 10 bands (31 Hz to 16 kHz, ±12 dB) and 20 presets. Boosts automatically turn the overall level down to match, so loud songs don't distort.
     - **Compressor**: RMS/peak, attack, release, threshold, ratio, knee and makeup gain, with presets (Gentle, Music, Vocals, Punchy drums, Loud, Night mode, Limiter).
     - **Spatializer**: room reverb with size, width, wet, dry and damp.
     - **Widener**: stereo widening with delay, feedback, crossfeed and dry mix.
     - **Speed & pitch**: speed 0.25x to 4x keeps the pitch, and pitch ±12 semitones keeps the speed.
   - **Visualizer** in the sidebar: Spectrometer, Scope, Spectrum, VU meter, 3D spectrum or off. Click it to switch, or right-click it for the list.
3. The tabs:
   - **Songs**: everything, sorted by artist, album and track number.
   - **Albums**: one tile per album, showing its cover. Open an album to play it or to **Save cover** as an image file.
   - **Genres**: genre matching. The many genre spellings found in tags (`Hip-Hop/Rap`, ID3v1 numbers like `(17)`, `Synthpop`, …) are sorted onto a small set of shelves: Rock, Pop, Hip-Hop, Electronic, R&B, Jazz, Classical, Metal, Country, Folk, Blues, Reggae, Soundtrack, Other and Unknown. Hover over a song's genre to see the original tag.
   - **Playlists**: type a name and press **Create playlist**. To add songs, right-click them (Ctrl/Shift-click selects several) and choose **Add to playlist**. Playlists are stored in `%APPDATA%\ShibaPlayer\playlists.json` as file paths.
4. **Metadata completes itself.** After the folder loads, songs missing an artist, album, genre or cover are looked up in the background on iTunes, then Deezer (far more Russian and niche music), then SoundCloud (the most songs, the messiest data), all free with no account, and what's found is written into the files without asking.
   - Only **empty** tags are filled in. Existing tags are never overwritten.
   - A result is only used when both the artist and the title match. Untagged files are searched by their `Artist - Title` or `01 - Artist - Title` file name (ShibaDownloader's naming); a file with no artist in its tags or name is left alone. Video-title extras like `- Lyrics`, `with lyrics` and `| Official Video` are ignored when matching.
   - There's one lookup every 3 seconds, because iTunes allows about 20 a minute. If iTunes says to slow down, ShibaPlayer waits a minute and carries on. If you're offline, it tries again next launch.
   - Each song is looked up once, even if iTunes has nothing (remembered in `%APPDATA%\ShibaPlayer\looked-up-4.json`). To try one again, right-click it and choose **Complete metadata now**.
   - **Find the right song...** (right-click a song) is for when the automatic match misses or gets it wrong. Edit the search until the right song shows up among the iTunes, Deezer and SoundCloud results, then press **Use this**. Your pick replaces the song's existing tags and cover. This works on the playing song too: it reopens the song at the same spot.
   - Automatic lookups skip the song that is currently playing until next launch, because the player has the file open.
5. **Deleting:** right-click a song for **Delete song** or **Delete album**, or right-click an album tile for **Delete album**. It asks first, and files go to the Recycle Bin, never straight to deletion.
   - The album's folder goes too, but only if it's inside the music folder, has no subfolders, and holds nothing but leftovers like cover art. The music folder itself never goes.
   - "Unknown album" can't be deleted as a group, since it's just whatever has no album tag. Deleted songs also leave your playlists. If the playing song is deleted, the next one starts.
6. **YouTube tab:** search for a song and press **Download**. The audio is saved into your music folder as `Artist - Title` in the quality chosen in Settings (the gear at the top right):
   - **M4A**: AAC about 128 kbps, exactly as YouTube stores it.
   - **MP3 192** or **MP3 320**: converted with Windows' MP3 encoder from YouTube's best stream. 320 is a bigger file, but can't sound better than the source.
   - **Highest**: the highest-bitrate stream YouTube offers, untouched, normally Opus about 130 to 160 kbps in `.webm`. If Windows can't decode it, you get M4A instead. Other players won't see its tags or cover, because they're kept in `%APPDATA%\ShibaPlayer` rather than in the file (writing tags into WebM makes it unplayable); moving the file loses them.

   It's tagged straight away, and added to your library. It starts playing unless something else already is; the button then becomes **Play**.
   - **Playlists and links:** paste a YouTube link into the same box. A song link lists that song. A playlist or album link lists every available song and shows **Download all**, which saves them one at a time into the music folder and adds them to a ShibaPlayer playlist with the same name. Press it again to stop; what's done stays. Songs you already have aren't downloaded again, and videos that can't be downloaded are skipped and counted. Tags and covers follow in the background, one song every 3 seconds. Private playlists can't be read, and Mix links give only the couple of dozen songs YouTube generates at a time.
7. **Dark mode:** the sun/moon button at the top right switches themes, and your choice is remembered. Until you pick one, ShibaPlayer follows the Windows setting.

## Build

```powershell
.\build.ps1
```

This needs the .NET 10 SDK. It produces `release\ShibaPlayer.exe` (portable, includes the runtime) and `release\ShibaPlayer-Setup-<version>.exe` (the installer, a copy of ShibaTag's). The version is set in `Directory.Build.props`. Playback is ShibaPlayer's own engine in `Audio.cs`, with no player library. Windows' built-in codecs (Media Foundation, called directly) decode the audio to samples and encode MP3 downloads (`Encoder.cs`). Our own code does the rest: speed and pitch, equalizer, compressor, widener and reverb (`Effects.cs`), the visualizer (`Visualizer.cs`), and sending the sound to the speakers through the Windows `waveOut` API in 50 ms buffers. TagLibSharp is only used to read and write tags. `YouTube.cs` is a trimmed copy of ShibaDownloader's client, so if every video starts failing, apply ShibaDownloader's fix there too.

## Self-test

```powershell
.\release\ShibaPlayer.exe --selftest | Write-Output
```

It checks genre matching, Deezer and iTunes result picking against canned responses, the file-name search terms, the playlist store (which it writes to `%TEMP%`), the effects (equalizer, compressor, widener, reverb, speed and pitch) and the visualizer's math. It also decodes the first MP3 and M4A in `%USERPROFILE%\Music` and plays each one silently (volume 0) through the sound card, checking that the position advances and that seeking works. Music files are only read. It also does one live metadata lookup, searches YouTube, downloads one song to `%TEMP%` as M4A, Highest and MP3 320, tags and decodes each, printing `SKIP` if YouTube or the network says no. Exit code 0 means everything passed.

## A note on the online parts

This is a personal project, not affiliated with YouTube, SoundCloud, Deezer or Apple. The YouTube and SoundCloud parts use those sites' unofficial web interfaces, so they can break whenever the sites change, and their terms may restrict this kind of use. Only download what you have the right to, and it's on you to check. SoundCloud is only read for metadata, never audio.

## Not included

- Repeat modes, gapless playback, the equalizer's "2 pass" option.
- Audio with more than 2 channels (e.g. 5.1 AAC). The engine sends the stream to the sound card as it is, and older drivers may refuse it.
- ALAC (Apple Lossless) `.m4a` files: these play only if Windows has an ALAC decoder installed. AAC `.m4a` files always play.
- Renaming playlists or reordering their songs.
