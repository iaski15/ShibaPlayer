using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ShibaPlayer;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Player _player = new();      // our own engine (Audio.cs)
    private bool _settingsOpen;
    private Track? _fixTrack;          // the song whose metadata is being chosen by hand
    private CancellationTokenSource? _fixSearch;
    private int _failsInRow;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, List<string>> _playlists = Store.LoadPlaylists();
    private List<Track> _tracks = [];
    private List<Track> _shown = [];   // songs in the list right now; "Play all" and double-click play these
    private Track[] _queue = [];
    private int _qi = -1;
    private bool _playing, _shuffle, _seeking, _busy;
    private string? _root;
    private string? _open;             // opened album/genre/playlist; null = the tiles
    private int _unreadable;
    private bool _dark;
    private readonly List<Track> _lookups = [];  // waiting for an iTunes lookup, next one first
    private readonly HashSet<string> _lookedUp = Store.LoadLookedUp(); // never asked twice on their own, even if iTunes had nothing
    private bool _lookingUp;
    private CancellationTokenSource? _ytSearch;
    private string? _ytPlaylist;       // the pasted playlist's name while its songs are listed
    private bool _ytBatchRunning, _ytBatchStop;

    // key -> light, dark. Light values match the defaults in MainWindow.xaml. The sidebar stays fur-orange in both.
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("Bg", "#FFF6EA", "#1E1712"), ("Surface", "#FFFFFF", "#2A2019"), ("Surface2", "#FBEBD8", "#3A2C22"),
        ("Inset", "#FFFBF5", "#241B15"), ("Line", "#EFD9BF", "#4A392C"), ("Text", "#3A2418", "#F5E9DA"),
        ("Muted", "#9B7A60", "#B8A28A"), ("FurDeep", "#C4652A", "#F0A262"), ("Hover", "#FFF6EA", "#33271F"),
        ("Pressed", "#F5DEC4", "#4A392C"), ("Dash", "#E3C3A0", "#5C4838"), ("Hint", "#C8B39E", "#7D6A58"),
        ("Bubble", "#FFFFFF", "#2A2019"),
    ];

    /// <param name="folder">Scanned on open: `ShibaPlayer.exe "D:\Music"` or a folder dropped on the exe.</param>
    public MainWindow(string? folder = null)
    {
        InitializeComponent();
        ApplyTheme(Store.LoadDark() ?? WindowsUsesDarkMode());
        _player.Volume = (float)VolumeSlider.Value;
        _player.Ended += () => { _failsInRow = 0; if (_qi < _queue.Length - 1) Step(1); else Stop(); };
        _player.Failed += msg =>
        {
            SetStatus($"Can't play {Path.GetFileName(_queue[_qi].Path)}: {msg}", false);
            if (++_failsInRow < _queue.Length) Step(1); else { _failsInRow = 0; Stop(); } // don't spin on a queue of broken files
        };
        _tick.Tick += (_, _) => UpdatePosition();
        if (Store.LoadEq() is { Length: var n } gains && n == Equalizer.Bands.Length)
            for (int i = 0; i < n; i++) _player.Eq.Set(i, gains[i]);
        BuildEffects();
        Closing += (_, _) =>
        {
            SaveEffects();
            _player.Dispose();
        };
        if (folder == null) // last folder used, else this PC's Music folder
        {
            folder = Store.LoadRoot();
            if (folder == null || !Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        Loaded += (_, _) => { if (Directory.Exists(folder)) _ = ScanAsync(folder); else Render(); };
    }

    private string Mode => _fixTrack != null ? "fix" : _settingsOpen ? "settings" : EffectsTab.IsChecked == true ? "effects" : AlbumsTab.IsChecked == true ? "albums" : GenresTab.IsChecked == true ? "genres"
                         : PlaylistsTab.IsChecked == true ? "playlists" : YouTubeTab.IsChecked == true ? "youtube" : "songs";

    // ---- chrome ----

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- theme ----

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_dark);
        try { Store.SaveDark(_dark); } catch { /* the theme just won't be remembered */ }
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        foreach (var (key, light, night) in Palette)
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
        ThemeButton.Content = dark ? "\uE706" : "\uE708"; // sun, moon
        ThemeButton.ToolTip = dark ? "Light mode" : "Dark mode";
    }

    private static bool WindowsUsesDarkMode() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    // ---- folder ----

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog
        {
            Title = "Where's your music?",
            InitialDirectory = _root ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (pick.ShowDialog(this) == true) _ = ScanAsync(pick.FolderName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } p && Directory.Exists(p[0])
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } p && Directory.Exists(p[0]))
            _ = ScanAsync(p[0]);
    }

    private async Task ScanAsync(string root)
    {
        _root = root;
        FolderText.Text = root;
        FolderText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _lookups.Clear();
        SetBusy(true);
        SetMascot(ShibaMood.Neutral, "Sniffing through your music...");
        SetStatus("Scanning...", null);
        try
        {
            var (tracks, unreadable) = await Task.Run(() =>
            {
                var list = Library.Scan(root, out var bad);
                return (list.OrderBy(t => t.ArtistName).ThenBy(t => t.AlbumName).ThenBy(t => t.Number).ThenBy(t => t.Title).ToList(), bad);
            });
            _tracks = tracks;
            Store.SaveRoot(root);
            _unreadable = unreadable;
            UpdateSummary();
            SetStatus(_tracks.Count == 0 ? "No MP3 or M4A files in that folder." : $"Found {_tracks.Count} songs.", _tracks.Count > 0);
            SetMascot(_tracks.Count == 0 ? ShibaMood.Sad : ShibaMood.Happy,
                      _tracks.Count == 0 ? "Hmm, no music in there." : "Found your music! Double-click a song to play.");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't scan: " + ex.Message, false);
            SetMascot(ShibaMood.Sad, "Ruff. I couldn't read that folder.");
        }
        finally
        {
            SetBusy(false);
            _open = null;
            Render();
        }
        _lookups.AddRange(_tracks.Where(t => t.NeedsMetadata && !_lookedUp.Contains(t.Path)));
        _ = RunLookupsAsync();
    }

    private void UpdateSummary()
    {
        SummaryText.Text = $"{_tracks.Count} songs  ·  {_tracks.Select(t => t.AlbumName).Distinct().Count()} albums  ·  "
                         + $"{_tracks.Count(t => t.NeedsMetadata)} incomplete" + (_unreadable > 0 ? $"  ·  {_unreadable} unreadable" : "");
    }

    // ---- views ----

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        _settingsOpen = false;
        _fixTrack = null;
        _open = null;
        Render();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_fixTrack != null) // back to wherever the song was right-clicked
        {
            _fixTrack = null;
            Render();
            return;
        }
        _open = null;
        Render();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not Group g) return;
        if (Mouse.RightButton == MouseButtonState.Pressed) // right-click is for the menu, not for opening
        {
            GroupList.SelectedItem = null;
            return;
        }
        _open = g.Name;
        Render();
    }

    private List<Group>? Groups() => Mode switch
    {
        "albums" => _tracks.GroupBy(t => t.AlbumName).OrderBy(g => g.Key)
            .Select(g => new Group(g.Key, $"{g.First().ArtistName}  ·  {g.Count()} songs", g.OrderBy(t => t.Number).ThenBy(t => t.Title).ToList()))
            .ToList(),
        "genres" => _tracks.GroupBy(t => t.GenreGroup).OrderBy(g => g.Key)
            .Select(g => new Group(g.Key, $"{g.Count()} songs  ·  " + string.Join(", ", g.Select(t => t.Genre).Where(s => s.Length > 0).Distinct()), g.ToList()))
            .ToList(),
        "playlists" => _playlists.OrderBy(p => p.Key).Select(p =>
        {
            var list = p.Value.Select(path => _tracks.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                .OfType<Track>().ToList();
            var missing = p.Value.Count - list.Count;
            return new Group(p.Key, $"{list.Count} songs" + (missing > 0 ? $"  ·  {missing} not in this folder" : ""), list);
        }).ToList(),
        _ => null,
    };

    private void Render()
    {
        if (SongsView is null) return; // fires during InitializeComponent
        Show(YouTubeView, Mode == "youtube");
        Show(EffectsView, Mode == "effects");
        Show(SettingsView, Mode == "settings");
        Show(FixView, Mode == "fix");
        var fullPage = Mode is "youtube" or "effects" or "settings" or "fix"; // these need the room more than the folder box does
        foreach (UIElement e in new UIElement[] { HeadTitle, HeadNote, DropZone }) Show(e, !fullPage);
        if (fullPage)
        {
            foreach (UIElement e in new UIElement[] { GroupList, SongsView, EmptyText, BackButton, PlayAllButton, SaveCoverButton, DeletePlaylistButton, NewPlaylistBox, NewPlaylistButton })
                Show(e, false);
            HeaderText.Text = Mode switch { "youtube" => "Find songs on YouTube", "effects" => "Effects", "fix" => "Find the right song", _ => "Settings" };
            Show(BackButton, Mode == "fix");
            if (Mode == "youtube") YtBox.Focus();
            return;
        }
        var groups = Groups();
        var open = groups?.FirstOrDefault(g => g.Name == _open);
        if (open == null) _open = null;
        var showTiles = groups != null && open == null;

        // A copy: WPF crashes if a bound list changes behind its back, and downloads and deletes change _tracks.
        _shown = open?.Tracks ?? (groups == null ? [.. _tracks] : []);
        GroupList.SelectionChanged -= GroupList_SelectionChanged;
        GroupList.ItemsSource = showTiles ? groups : null;
        GroupList.SelectionChanged += GroupList_SelectionChanged;
        SongList.ItemsSource = showTiles ? null : _shown;

        GroupList.Visibility = showTiles ? Visibility.Visible : Visibility.Collapsed;
        SongsView.Visibility = showTiles ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Text = open?.Name ?? Mode switch { "albums" => "Albums", "genres" => "Genres", "playlists" => "Playlists", _ => "All songs" };
        Show(BackButton, open != null);
        Show(PlayAllButton, !showTiles && _shown.Count > 0);
        Show(SaveCoverButton, Mode == "albums" && open?.Cover != null);
        Show(DeletePlaylistButton, Mode == "playlists" && open != null);
        Show(NewPlaylistBox, Mode == "playlists" && open == null);
        Show(NewPlaylistButton, Mode == "playlists" && open == null);

        EmptyText.Text = _tracks.Count == 0 && Mode != "playlists" ? "Much empty. Choose a folder with MP3 or M4A files."
                       : showTiles && groups!.Count == 0 ? "No playlists yet. Type a name and press Create playlist."
                       : !showTiles && _shown.Count == 0 ? (Mode == "playlists" ? "Empty playlist. Right-click songs to add them here." : "")
                       : "";
        Show(EmptyText, EmptyText.Text.Length > 0);

        static void Show(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- playback ----

    private void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongList.SelectedItem is Track t) PlayFrom(_shown, _shown.IndexOf(t));
    }

    private void SongList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SongList.SelectedItem is Track t) PlayFrom(_shown, _shown.IndexOf(t));
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e) => PlayFrom(_shown, _shuffle ? Random.Shared.Next(_shown.Count) : 0);

    private void PlayFrom(IList<Track> list, int start)
    {
        if (list.Count == 0 || start < 0) return;
        _queue = list.ToArray();
        if (_shuffle) ShuffleQueue(start); else _qi = start;
        Load();
    }

    /// <summary>Puts queue[start] first and shuffles everything after it.</summary>
    private void ShuffleQueue(int start)
    {
        (_queue[0], _queue[start]) = (_queue[start], _queue[0]);
        Random.Shared.Shuffle(_queue.AsSpan(1));
        _qi = 0;
    }

    private void Load()
    {
        var t = _queue[_qi];
        foreach (var x in _tracks) x.IsPlaying = x == t;
        _player.Open(t.Path);
        _playing = true;
        _tick.Start();
        NowCover.Content = null; // same song with a new cover wouldn't redraw otherwise
        NowCover.Content = t;
        NowTitle.Text = t.Title;
        NowArtist.Text = string.Join("  ·  ", new[] { t.ArtistName, t.AlbumName, t.GenreGroup });
        PlayButton.Content = "\uE769";
        Title =$"{t.Title} - ShibaPlayer";
        SetMascot(ShibaMood.Happy, $"Now playing: {t.Title}");
        SongList.Items.Refresh();
    }

    private void Step(int d)
    {
        if (_queue.Length == 0) return;
        _qi = (_qi + d + _queue.Length) % _queue.Length;
        Load();
    }

    private void Stop()
    {
        _player.Stop();
        _playing = false;
        PlayButton.Content = "\uE768";
        SetMascot(ShibaMood.Sleepy, "That's the end of the queue. Nap time?");
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_qi < 0)
        {
            var sel = SongList.SelectedItem as Track;
            PlayFrom(_shown, sel != null ? _shown.IndexOf(sel) : 0);
            return;
        }
        if (!_player.IsOpen) { Load(); return; } // the queue ran out; play its current song again
        if (_playing) { _player.Pause(); SetMascot(ShibaMood.Sleepy, "Paused. Zzz..."); }
        else { _player.Resume(); SetMascot(ShibaMood.Happy, $"Now playing: {_queue[_qi].Title}"); }
        _playing = !_playing;
        PlayButton.Content = _playing ? "\uE769" : "\uE768";
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Position.TotalSeconds > 3) _player.Seek(TimeSpan.Zero); else Step(-1);
    }

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        ShuffleButton.SetResourceReference(BackgroundProperty, _shuffle ? "Fur" : "Surface2");
        if (_shuffle) ShuffleButton.Foreground = Brushes.White;
        else ShuffleButton.SetResourceReference(ForegroundProperty, "Text");
        // shuffle what's left; turning it off keeps the shuffled order (like most players)
        if (_shuffle && _qi >= 0) ShuffleQueue(_qi);
        SetStatus(_shuffle ? "Shuffle on." : "Shuffle off.", null);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => _player.Volume = (float)e.NewValue;

    private void UpdatePosition()
    {
        var len = _player.Duration;
        if (len == TimeSpan.Zero) return;
        PosSlider.Maximum = len.TotalSeconds;
        if (!_seeking) PosSlider.Value = _player.Position.TotalSeconds;
        TimeText.Text = $"{Clock(TimeSpan.FromSeconds(PosSlider.Value))} / {Clock(len)}";
        static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void Pos_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Pos_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _player.Seek(TimeSpan.FromSeconds(PosSlider.Value));
        _seeking = false;
    }

    // ---- playlists ----

    private void SongList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count == 0) { e.Handled = true; return; }
        var menu = SongList.ContextMenu!;
        menu.Items.Clear();

        var add = new MenuItem { Header = "Add to playlist" };
        foreach (var name in _playlists.Keys.Order()) add.Items.Add(Item(name, () => AddTo(name, sel)));
        if (_playlists.Count > 0) add.Items.Add(new Separator());
        add.Items.Add(Item("New playlist", () =>
        {
            var n = 1;
            while (_playlists.ContainsKey($"Playlist {n}")) n++;
            AddTo($"Playlist {n}", sel);
        }));
        menu.Items.Add(add);
        if (Mode == "playlists" && _open != null)
            menu.Items.Add(Item("Remove from playlist", () =>
            {
                _playlists[_open].RemoveAll(p => sel.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase)));
                SavePlaylists();
            }));
        if (sel.Count == 1) menu.Items.Add(Item("Find the right song...", () => OpenFix(sel[0])));
        menu.Items.Add(Item("Complete metadata now", () =>
        {
            _lookups.RemoveAll(sel.Contains);
            _lookups.InsertRange(0, sel);
            _ = RunLookupsAsync();
        }));
        menu.Items.Add(Item("Show in Explorer", () => Process.Start("explorer.exe", $"/select,\"{sel[0].Path}\"")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(sel.Count == 1 ? "Delete song" : $"Delete {sel.Count} songs", () => DeleteSongs(sel)));
        if (sel[0].Album.Length > 0)
            menu.Items.Add(Item("Delete album", () => DeleteAlbum(_tracks.Where(t => t.Album == sel[0].Album).ToList())));
    }

    private void GroupList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Only albums can be deleted: genres aren't folders, and "Unknown album" is just whatever has no album tag.
        var item = e.OriginalSource is DependencyObject src ? ItemsControl.ContainerFromElement(GroupList, src) : null;
        if (Mode != "albums" || item is not ListBoxItem { DataContext: Group g } || g.Tracks[0].Album.Length == 0)
        {
            e.Handled = true;
            return;
        }
        var menu = GroupList.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item("Play album", () => PlayFrom(g.Tracks, 0)));
        menu.Items.Add(Item("Delete album", () => DeleteAlbum(g.Tracks)));
    }

    private static MenuItem Item(string header, Action act)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => act();
        return m;
    }

    private void AddTo(string name, List<Track> tracks)
    {
        var list = _playlists.TryGetValue(name, out var l) ? l : _playlists[name] = [];
        list.AddRange(tracks.Select(t => t.Path).Where(p => !list.Contains(p, StringComparer.OrdinalIgnoreCase)));
        SavePlaylists();
        SetStatus($"Added {tracks.Count} song(s) to {name}.", true);
    }

    private void NewPlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NewPlaylist_Click(sender, e);
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = NewPlaylistBox.Text.Trim();
        if (name.Length == 0) return;
        if (_playlists.ContainsKey(name)) { SetStatus($"There's already a playlist called {name}.", false); return; }
        _playlists[name] = [];
        NewPlaylistBox.Clear();
        _open = name;
        SavePlaylists();
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_open == null) return;
        if (MessageBox.Show(this, $"Delete the playlist \"{_open}\"? The songs themselves stay.", "ShibaPlayer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _playlists.Remove(_open);
        _open = null;
        SavePlaylists();
    }

    private void SavePlaylists()
    {
        try { Store.SavePlaylists(_playlists); }
        catch (Exception ex) { SetStatus("Couldn't save playlists: " + ex.Message, false); }
        Render();
    }

    // ---- metadata + covers ----

    private bool IsCurrent(Track t) =>
        _player.IsOpen && _qi >= 0 && _queue[_qi].Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Works through _lookups in the background, filling empty tags and covers. Only one runs at a time.</summary>
    private async Task RunLookupsAsync()
    {
        if (_lookingUp) return;
        _lookingUp = true;
        int found = 0, tried = 0;
        var reachable = true;
        while (_lookups.Count > 0)
        {
            var t = _lookups[0];
            if (!File.Exists(t.Path)) { _lookups.RemoveAt(0); continue; }
            if (IsCurrent(t)) { _lookups.RemoveAt(0); continue; } // the player has the file open; picked up next launch
            SetStatus($"Fetching tags and covers: {t.Title}  ({_lookups.Count} to go)", null);
            try
            {
                if (await Library.CompleteAsync(Http, t))
                {
                    found++;
                    SongList.Items.Refresh();
                    UpdateSummary();
                }
                tried++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                SetStatus("iTunes says slow down. Continuing in a minute.", null);
                await Task.Delay(60_000);
                continue; // same song again
            }
            catch (HttpRequestException)
            {
                reachable = false; // offline: everything left is tried again next launch
                _lookups.Clear();
                break;
            }
            catch (Exception) { tried++; } // read-only or locked file; "Complete metadata now" can retry it
            _lookups.Remove(t);
            _lookedUp.Add(t.Path);
            try { Store.SaveLookedUp(_lookedUp); } catch { /* it just gets looked up again next launch */ }
            // ponytail: fixed 3 s gap keeps us under iTunes' ~20/min; no adaptive backoff
            if (_lookups.Count > 0) await Task.Delay(3000);
        }
        _lookingUp = false;
        if (!reachable) SetStatus("Can't reach iTunes, so tags and covers will be fetched next time.", null);
        else if (tried > 0) SetStatus($"Metadata done: filled in {found} of {tried} songs.", true);
        if (found > 0)
        {
            UpdateSummary();
            if (SongsView.Visibility != Visibility.Visible) Render(); // album and genre tiles changed too
        }
    }

    // ---- choosing a song's metadata by hand ----

    private void OpenFix(Track t)
    {
        _fixTrack = t;
        FixFile.Text = $"{Path.GetFileName(t.Path)}  ·  your pick replaces this song's current tags";
        var (artist, title) = Library.SearchTerms(t);
        FixBox.Text = Library.SearchTerm(artist, title);
        FixList.ItemsSource = null;
        Render();
        FixBox.Focus();
        FixBox.SelectAll();
        FixSearch_Click(this, new RoutedEventArgs());
    }

    private void FixBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FixSearch_Click(sender, e);
    }

    private async void FixSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = FixBox.Text.Trim();
        if (query.Length == 0) return;
        _fixSearch?.Cancel();
        var cts = _fixSearch = new CancellationTokenSource();
        SetStatus($"Searching for \"{query}\"...", null);
        try
        {
            var hits = await Library.SearchEverywhereAsync(Http, query);
            if (cts.IsCancellationRequested) return; // a newer search took over
            FixList.ItemsSource = hits;
            SetStatus(hits.Count == 0 ? "Nothing found. Try fewer words, or just the song name." : $"Found {hits.Count} songs.", hits.Count > 0);
        }
        catch (Exception ex) { if (!cts.IsCancellationRequested) SetStatus("Search failed: " + ex.Message, false); }
    }

    private async void FixUse_Click(object sender, RoutedEventArgs e)
    {
        if (_fixTrack is not { } t || sender is not Button { DataContext: SongHit hit } button) return;
        button.IsEnabled = false;
        // the player has the file open, so let go of it and come back to the same spot
        var reopen = IsCurrent(t) && !WebmTags.Applies(t.Path);
        var position = _player.Position;
        var wasPlaying = _playing;
        if (reopen) _player.Stop();
        try
        {
            await Library.ApplyAsync(Http, t, hit);
            _lookups.Remove(t);
            _lookedUp.Add(t.Path);
            try { Store.SaveLookedUp(_lookedUp); } catch { /* it just gets looked up again, which only fills empty tags */ }
            SetStatus($"Updated: {t.ArtistName} - {t.Title}", true);
            _fixTrack = null;
            UpdateSummary();
            Render();
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            SetStatus($"Couldn't update the song: {ex.Message}", false);
        }
        finally
        {
            if (reopen)
            {
                Load();
                _player.Seek(position);
                if (!wasPlaying) Play_Click(this, new RoutedEventArgs());
            }
        }
    }

    // ---- YouTube ----

    private void YtBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) YtSearch_Click(sender, e);
    }

    private async void YtSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = YtBox.Text.Trim();
        if (query.Length == 0) return;
        _ytSearch?.Cancel();
        var cts = _ytSearch = new CancellationTokenSource();
        SetStatus($"Searching YouTube for \"{query}\"...", null);
        try
        {
            IReadOnlyList<YouTubeHit> hits;
            string? playlist = null;
            if (query.Contains("youtu", StringComparison.OrdinalIgnoreCase) && YouTube.ParsePlaylistId(query) != null)
                (playlist, hits) = await YouTube.GetPlaylistAsync(query, cts.Token);
            else if (query.Contains("youtu", StringComparison.OrdinalIgnoreCase) && YouTube.ParseVideoId(query) is { } id)
            {
                var info = await YouTube.GetVideoAsync(id, cts.Token);
                hits = [new YouTubeHit(id, info.Title, info.Uploader, $"{info.LengthSeconds / 60}:{info.LengthSeconds % 60:D2}")];
            }
            else hits = await YouTube.SearchAsync(query, cts.Token);
            _ytPlaylist = playlist;
            YtList.ItemsSource = hits.Select(h => new YouTubeRow(h, YouTubeRow.Download)).Select(r =>
            {
                if (_root != null && ExistingSong(_root, r) != null) r.Action = YouTubeRow.Play;
                return r;
            }).ToList();
            UpdateDownloadAllButton();
            SetStatus(hits.Count == 0 ? "Nothing found on YouTube."
                    : playlist != null ? $"{playlist}: {hits.Count} songs." : $"Found {hits.Count} videos.", hits.Count > 0);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { /* a newer search took over */ }
        catch (Exception ex) { SetStatus("YouTube search failed: " + ex.Message, false); }
    }

    private async void YtRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not YouTubeRow row) return;
        var root = _root;
        if (root == null) { SetStatus("Choose a music folder first.", false); return; }
        var path = ExistingSong(root, row);
        var downloading = path == null;
        if (path == null && (path = await DownloadSongAsync(row, root)) == null) return;
        row.Action = YouTubeRow.Play;
        if (downloading && _playing) return; // a finished download doesn't cut off what's playing
        var track = TrackFor(path, root);
        if (track != null) PlayFrom([track], 0);
    }

    private void UpdateDownloadAllButton()
    {
        if (_ytBatchRunning) return; // it's showing progress and Stop
        YtAllButton.Visibility = _ytPlaylist != null ? Visibility.Visible : Visibility.Collapsed;
        YtAllButton.IsEnabled = true;
        YtAllButton.Content = $"Download all ({YtList.Items.Count})";
    }

    /// <summary>Downloads a whole playlist into the music folder and makes a ShibaPlayer playlist of it.
    /// ponytail: one song at a time; a few at once would be quicker, but risks YouTube's rate limit.</summary>
    private async void YtAll_Click(object sender, RoutedEventArgs e)
    {
        if (_ytBatchRunning)
        {
            _ytBatchStop = true;
            YtAllButton.Content = "Stopping...";
            YtAllButton.IsEnabled = false;
            return;
        }
        var root = _root;
        if (root == null) { SetStatus("Choose a music folder first.", false); return; }
        var rows = YtList.Items.Cast<YouTubeRow>().ToList();
        var name = _ytPlaylist ?? "YouTube playlist";
        var songs = new List<Track>();
        int failed = 0, skipped = 0, done = 0;
        _ytBatchRunning = true;
        _ytBatchStop = false;
        try
        {
            foreach (var row in rows)
            {
                if (_ytBatchStop) break;
                YtAllButton.Content = $"Stop ({++done}/{rows.Count})";
                if (!row.CanClick) { skipped++; continue; } // already downloading by hand
                var path = ExistingSong(root, row) ?? await DownloadSongAsync(row, root, tagNow: false);
                if (path == null) { failed++; continue; } // deleted or blocked videos shouldn't end the whole list
                row.Action = YouTubeRow.Play;
                if (TrackFor(path, root) is { } track)
                {
                    songs.Add(track);
                    // saved per song: closing the app halfway keeps the playlist so far
                    var list = _playlists.TryGetValue(name, out var existing) ? existing : _playlists[name] = [];
                    if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
                    try { Store.SavePlaylists(_playlists); } catch { /* the save at the end reports any problem */ }
                }
            }
        }
        finally
        {
            _ytBatchRunning = false;
            if (songs.Count > 0) SavePlaylists();
            UpdateDownloadAllButton();
            UpdateSummary();
            var left = rows.Count - done;
            SetStatus($"{name}: {songs.Count} of {rows.Count} songs in your library and in the playlist"
                + (failed > 0 ? $", {failed} couldn't be downloaded" : "")
                + (left + skipped > 0 ? $", {left + skipped} not done" : "") + ".", failed == 0 && left == 0);
        }
    }

    /// <summary>The song's file if it's already in the folder, in any of the download formats.</summary>
    private static string? ExistingSong(string root, YouTubeRow row) =>
        Library.Extensions.Select(ext => Path.Combine(root, Path.ChangeExtension(row.FileName, ext))).FirstOrDefault(File.Exists);

    /// <summary>Saves the song's audio in the chosen quality, then tags it right away so it's complete before it can start
    /// playing. Returns where it went, or null if it failed.</summary>
    private async Task<string?> DownloadSongAsync(YouTubeRow row, string root, bool tagNow = true)
    {
        var tempBase = Path.Combine(Path.GetTempPath(), $"ShibaPlayer-{Guid.NewGuid():N}");
        var quality = _settings.Quality;
        row.CanClick = false;
        try
        {
            row.Action = "Starting...";
            SetStatus($"Downloading {row.Hit.Title}", null);
            var info = await YouTube.GetVideoAsync(row.Hit.Id, CancellationToken.None);
            var temp = await FetchAudioAsync(row, info, quality, tempBase);
            if (quality.StartsWith("mp3"))
            {
                row.Action = "Converting...";
                var mp3 = tempBase + ".mp3";
                var source = temp;
                await Task.Run(() => Mp3Encoder.Encode(source, mp3, quality == "mp3320" ? 320 : 192));
                temp = mp3;
            }
            var path = Path.Combine(root, Path.ChangeExtension(row.FileName, Path.GetExtension(temp)));
            File.Move(temp, path);

            row.Action = "Tagging...";
            var track = TrackFor(path, root);
            if (track != null && !tagNow)
            {
                // a whole playlist at once would trip iTunes' limit, so these wait their turn in the paced queue
                if (!_lookups.Contains(track)) _lookups.Add(track);
                _ = RunLookupsAsync();
            }
            else if (track != null)
            {
                _lookups.Remove(track);
                try { await Library.CompleteAsync(Http, track); }
                catch (Exception) { /* no tags yet is fine: it plays, and "Complete metadata now" can retry */ }
                _lookedUp.Add(path);
                try { Store.SaveLookedUp(_lookedUp); } catch { /* it just gets looked up again next launch */ }
            }
            UpdateSummary();
            SetStatus($"Saved {Path.GetFileName(path)}", true);
            return path;
        }
        catch (Exception ex)
        {
            row.Action = "Retry";
            SetStatus($"Couldn't download {row.Hit.Title}: {ex.Message}", false);
            return null;
        }
        finally
        {
            row.CanClick = true;
            foreach (var ext in new[] { ".m4a", ".webm", ".mp3" })
                try { File.Delete(tempBase + ext); } catch { /* the temp folder cleans up after itself eventually */ }
        }
    }

    /// <summary>Downloads the stream this quality calls for and returns its temp file. If this PC can't decode the
    /// best stream (Windows has no Opus codec on some editions), falls back to AAC, which it always can.</summary>
    private async Task<string> FetchAudioAsync(YouTubeRow row, VideoInfo info, string quality, string tempBase)
    {
        var format = quality == "m4a" ? info.BestAac() : info.BestAudio();
        var temp = await DownloadStreamAsync(row, format, tempBase);
        if (format.Mime.StartsWith("audio/mp4") || CanDecode(temp)) return temp;
        SetStatus("This PC can't play Opus, so getting M4A instead.", null);
        return await DownloadStreamAsync(row, info.BestAac(), tempBase);

        static bool CanDecode(string path)
        {
            try
            {
                using var decoder = new Decoder(path);
                for (int i = 0; i < 20; i++)
                    if (decoder.Read() is not { Length: 0 }) return true;
                return false;
            }
            catch (Exception) { return false; }
        }
    }

    private static async Task<string> DownloadStreamAsync(YouTubeRow row, MediaFormat format, string tempBase)
    {
        var aac = format.Mime.StartsWith("audio/mp4");
        var temp = tempBase + (aac ? ".m4a" : ".webm");
        long done = 0;
        var last = -1;
        var progress = new Progress<int>(p => row.Action = $"{p}%");
        await Task.Run(() => YouTube.DownloadAsync(format, temp, n =>
        {
            var pct = (int)(Interlocked.Add(ref done, n) * 100 / Math.Max(1, format.Size));
            if (pct != last) ((IProgress<int>)progress).Report(last = pct);
        }, CancellationToken.None));
        if (aac) Mp4Fix.DropEditList(temp);
        return temp;
    }

    /// <summary>The library's entry for a file, adding it if it's new. Null if it's unreadable.</summary>
    private Track? TrackFor(string path, string root)
    {
        var known = _tracks.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known;
        try
        {
            var track = Library.Read(path);
            if (root == _root) _tracks.Add(track); // the user may have switched folders during the download
            return track;
        }
        catch (Exception ex)
        {
            SetStatus($"Can't read {Path.GetFileName(path)}: {ex.Message}", false);
            return null;
        }
    }

    // ---- deleting ----

    private void DeleteSongs(List<Track> songs)
    {
        var what = songs.Count == 1 ? $"\"{songs[0].Title}\"" : $"these {songs.Count} songs";
        if (Confirm($"Move {what} to the Recycle Bin?")) Delete(songs, removeFolders: false);
    }

    private void DeleteAlbum(List<Track> songs)
    {
        if (Confirm($"Move the album \"{songs[0].Album}\" ({songs.Count} songs) to the Recycle Bin?\n\n" +
                    "Its folder goes too if no other music is left in it.")) Delete(songs, removeFolders: true);
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "ShibaPlayer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Delete(List<Track> songs, bool removeFolders)
    {
        var wasPlaying = _playing;
        var hitCurrent = _qi >= 0 && songs.Any(IsCurrent);
        if (hitCurrent) _player.Stop(); // lets go of the file

        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in songs)
        {
            try { Library.Recycle(t.Path); gone.Add(t.Path); }
            catch (Exception) { /* counted below */ }
        }
        if (removeFolders)
            foreach (var dir in gone.Select(p => Path.GetDirectoryName(p)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                if (Library.CanRemoveFolder(dir, _root))
                    try { Library.RecycleFolder(dir); } catch (Exception) { /* the songs are gone; a leftover folder is harmless */ }

        _tracks.RemoveAll(t => gone.Contains(t.Path));
        _lookups.RemoveAll(t => gone.Contains(t.Path));
        foreach (var list in _playlists.Values) list.RemoveAll(gone.Contains);
        UpdateQueueAfterDelete(gone, hitCurrent, wasPlaying);
        UpdateSummary();
        SavePlaylists(); // also re-renders
        SetStatus(gone.Count == songs.Count ? $"Moved {gone.Count} song(s) to the Recycle Bin."
                                            : $"Moved {gone.Count} of {songs.Count} songs; the rest couldn't be deleted.", gone.Count == songs.Count);
    }

    private void UpdateQueueAfterDelete(HashSet<string> gone, bool hitCurrent, bool wasPlaying)
    {
        var before = _queue.Take(Math.Max(_qi, 0)).Count(t => !gone.Contains(t.Path));
        _queue = _queue.Where(t => !gone.Contains(t.Path)).ToArray();
        if (!hitCurrent)
        {
            if (_qi >= 0) _qi = before;
            return;
        }
        if (wasPlaying && _queue.Length > 0)
        {
            _qi = before % _queue.Length; // the song after the deleted one
            Load();
            return;
        }
        _queue = [];
        _qi = -1;
        _playing = false;
        NowCover.Content = null;
        NowTitle.Text = "Nothing playing";
        NowArtist.Text = "Such quiet. Wow.";
        PosSlider.Value = 0;
        TimeText.Text = "0:00 / 0:00";
        PlayButton.Content = "\uE768";
        Title = "ShibaPlayer";
    }

    private void SaveCover_Click(object sender, RoutedEventArgs e)
    {
        var withCover = _shown.FirstOrDefault(t => t.HasCover);
        if (withCover == null || Library.CoverBytes(withCover.Path) is not { } bytes) return;
        var png = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50; // PNG magic, else JPEG
        var dlg = new SaveFileDialog
        {
            FileName = string.Concat(withCover.AlbumName.Split(Path.GetInvalidFileNameChars())) + (png ? ".png" : ".jpg"),
            Filter = png ? "PNG image|*.png" : "JPEG image|*.jpg",
            InitialDirectory = Path.GetDirectoryName(withCover.Path),
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllBytes(dlg.FileName, bytes);
        SetStatus($"Saved cover to {dlg.FileName}", true);
    }

    // ---- bits ----

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ChooseButton.IsEnabled = !busy;
    }

    private void SetMascot(ShibaMood mood, string text)
    {
        Mascot.Mood = mood;
        BubbleText.Text = text;
    }

    /// <param name="ok">true = green dot, false = red, null = busy/neutral.</param>
    private void SetStatus(string text, bool? ok)
    {
        StatusText.Text = text;
        StatusDot.Fill = ok switch
        {
            true => new SolidColorBrush(Color.FromRgb(0x3F, 0xA3, 0x4D)),
            false => new SolidColorBrush(Color.FromRgb(0xD2, 0x28, 0x37)),
            null => (Brush)FindResource("Fur"),
        };
    }
}
