using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShibaPlayer;

// The Effects tab, the speed menu, the visualizer card and the settings page.
public partial class MainWindow
{
    private readonly EffectSettings _fx = Store.Load<EffectSettings>("effects.json") ?? new();
    private readonly AppSettings _settings = Store.Load<AppSettings>("settings.json") ?? new();
    private readonly List<Slider> _eqSliders = [];
    private readonly DispatcherTimer _vizTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Slider _speedSlider = null!;

    private void BuildEffects()
    {
        _player.Fx = _fx;
        _player.Eq.On = _fx.EqOn;
        _player.Eq.Preamp = _fx.Preamp;
        AddEffectPage("Equalizer", "", EqualizerPage());
        AddEffectPage("Compressor", "", CompressorPage());
        AddEffectPage("Spatializer", "", SpatializerPage());
        AddEffectPage("Widener", "", WidenerPage());
        AddEffectPage("Speed & pitch", "", SpeedPage());

        BuildSettings();
        _vizTimer.Tick += (_, _) => { if (WindowState != WindowState.Minimized) Viz.Tick(_player, _playing && _player.IsOpen); };
        SetVisualizer(_settings.Viz);
    }

    private void SaveEffects()
    {
        try
        {
            Store.SaveEq(_player.Eq.Gains);
            Store.Save("effects.json", _fx);
        }
        catch { /* losing effect settings isn't worth a crash on exit */ }
    }

    private void AddEffectPage(string name, string glyph, UIElement page)
    {
        page.Visibility = EffectsHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EffectsHost.Children.Add(page);
        var tab = new RadioButton { Content = name, Tag = glyph, GroupName = "fx", Style = (Style)FindResource("Segment"), IsChecked = EffectsTabs.Children.Count == 0 };
        tab.Checked += (_, _) =>
        {
            foreach (UIElement p in EffectsHost.Children) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        };
        EffectsTabs.Children.Add(tab);
    }

    // ---- pages ----

    private UIElement EqualizerPage()
    {
        var knobs = new List<UIElement>
        {
            Knob("Preamp", -20, 20, _fx.Preamp, 0, 0.5, Db, v => { _fx.Preamp = (float)v; _player.Eq.Preamp = (float)v; }).Column,
        };
        for (int b = 0; b < Equalizer.Bands.Length; b++)
        {
            var band = b;
            var f = Equalizer.Bands[b];
            var (column, slider) = Knob(f >= 1000 ? $"{f / 1000} kHz" : $"{f} Hz", -12, 12, _player.Eq.Gains[b], 0, 0.5, Db, v => _player.Eq.Set(band, (float)v));
            knobs.Add(column);
            _eqSliders.Add(slider);
        }
        var presets = PresetButton(Equalizer.Presets.Select(p => (p.Name, (Action)(() =>
        {
            for (int i = 0; i < p.Gains.Length; i++) _eqSliders[i].Value = p.Gains[i];
            SetStatus($"Equalizer: {p.Name}", true);
        }))));
        return Page(Enable(_fx.EqOn, on => { _fx.EqOn = on; _player.Eq.On = on; }), presets, knobs);
    }

    private UIElement CompressorPage()
    {
        var sliders = new[]
        {
            Knob("RMS/peak", 0, 1, _fx.RmsPeak, 0.2, 0.05, v => $"{v:0.00}", v => _fx.RmsPeak = (float)v),
            Knob("Attack", 1.5, 400, _fx.Attack, 25, 0.5, v => $"{v:0.#} ms", v => _fx.Attack = (float)v),
            Knob("Release", 2, 800, _fx.Release, 100, 1, v => $"{v:0} ms", v => _fx.Release = (float)v),
            Knob("Threshold", -30, 0, _fx.Threshold, -11, 0.5, Db, v => _fx.Threshold = (float)v),
            Knob("Ratio", 1, 20, _fx.Ratio, 4, 0.5, v => $"{v:0.#}:1", v => _fx.Ratio = (float)v),
            Knob("Knee", 1, 10, _fx.Knee, 5, 0.5, Db, v => _fx.Knee = (float)v),
            Knob("Makeup gain", 0, 24, _fx.Makeup, 4, 0.5, Db, v => _fx.Makeup = (float)v),
        };
        var enable = Enable(_fx.CompOn, on => _fx.CompOn = on);
        var presets = PresetButton(EffectSettings.CompressorPresets.Select(p => (p.Name, (Action)(() =>
        {
            for (int i = 0; i < p.Values.Length; i++) sliders[i].Slider.Value = p.Values[i];
            enable.IsChecked = true; // choosing a preset means wanting to hear it
            SetStatus($"Compressor: {p.Name}", true);
        }))));
        return Page(enable, presets, sliders.Select(s => s.Column));
    }

    private UIElement SpatializerPage()
    {
        var sliders = new[]
        {
            Knob("Size", 0, 10, _fx.RoomSize, 8, 0.5, Plain, v => _fx.RoomSize = (float)v),
            Knob("Width", 0, 10, _fx.RoomWidth, 10, 0.5, Plain, v => _fx.RoomWidth = (float)v),
            Knob("Wet", 0, 10, _fx.Wet, 4, 0.5, Plain, v => _fx.Wet = (float)v),
            Knob("Dry", 0, 10, _fx.Dry, 5, 0.5, Plain, v => _fx.Dry = (float)v),
            Knob("Damp", 0, 10, _fx.Damp, 5, 0.5, Plain, v => _fx.Damp = (float)v),
        };
        return Page(Enable(_fx.SpatialOn, on => _fx.SpatialOn = on), null, sliders.Select(s => s.Column));
    }

    private UIElement WidenerPage()
    {
        var sliders = new[]
        {
            Knob("Delay time", 1, 100, _fx.WidenDelay, 20, 1, v => $"{v:0} ms", v => _fx.WidenDelay = (float)v),
            Knob("Feedback", 0, 0.9, _fx.WidenFeedback, 0.3, 0.05, Percent, v => _fx.WidenFeedback = (float)v),
            Knob("Crossfeed", 0, 0.8, _fx.WidenCross, 0.3, 0.05, Percent, v => _fx.WidenCross = (float)v),
            Knob("Dry mix", 0, 1, _fx.WidenDry, 0.8, 0.05, Percent, v => _fx.WidenDry = (float)v),
        };
        return Page(Enable(_fx.WidenOn, on => _fx.WidenOn = on), null, sliders.Select(s => s.Column));
    }

    private UIElement SpeedPage()
    {
        var speed = Knob("Speed", 0.25, 4, 1, 1, 0.05, v => $"{v:0.##}x", SetSpeed);
        _speedSlider = speed.Slider;
        var pitch = Knob("Pitch", -12, 12, 0, 0, 1, v => v == 0 ? "0" : $"{v:+0;-0} st", v => _fx.Pitch = v);
        var note = new TextBlock
        {
            Text = "Speed keeps the pitch, and pitch keeps the speed. Pitch is in semitones (12 = one octave). Both go back to normal on the next launch.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var knobs = new List<UIElement> { speed.Column, pitch.Column, note };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (name, action) in SpeedActions())
        {
            var button = new Button { Content = name, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => action();
            row.Children.Add(button);
        }
        return Page(null, row, knobs);
    }

    // ---- speed ----

    private (string Name, Action Act)[] SpeedActions() =>
    [
        ("Slower", () => _speedSlider.Value -= 0.25),
        ("Slower (fine)", () => _speedSlider.Value -= 0.05),
        ("Normal speed", () => _speedSlider.Value = 1),
        ("Faster (fine)", () => _speedSlider.Value += 0.05),
        ("Faster", () => _speedSlider.Value += 0.25),
    ];

    private void SetSpeed(double speed)
    {
        _fx.Speed = speed;
        SpeedButton.Content = $"{speed:0.##}x";
    }

    private void Speed_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(SpeedButton, SpeedActions().Reverse());

    // ---- building blocks ----

    private static string Db(double v) => v == 0 ? "0 dB" : $"{v:+0.#;-0.#} dB";
    private static string Plain(double v) => $"{v:0.#}";
    private static string Percent(double v) => $"{v * 100:0}%";

    /// <summary>One upright slider: value on top, name below. Double-click puts it back to its default.</summary>
    private (FrameworkElement Column, Slider Slider) Knob(string label, double min, double max, double value, double fallback, double step, Func<double, string> format, Action<double> set)
    {
        var shown = new TextBlock { Style = (Style)FindResource("Num"), HorizontalAlignment = HorizontalAlignment.Center, Text = format(value) };
        var slider = new Slider
        {
            Style = (Style)FindResource("VSlider"), Orientation = Orientation.Vertical, Height = 170, Margin = new Thickness(0, 6, 0, 6),
            Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = step,
            SmallChange = step, LargeChange = step * 4, IsMoveToPointEnabled = true, HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = "Drag to change. Double-click to reset.",
        };
        slider.ValueChanged += (_, e) => { shown.Text = format(e.NewValue); set(e.NewValue); };
        slider.MouseDoubleClick += (_, _) => slider.Value = fallback;
        var column = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
        column.Children.Add(shown);
        column.Children.Add(slider);
        column.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Head"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        return (column, slider);
    }

    private static CheckBox Enable(bool on, Action<bool> set)
    {
        var box = new CheckBox { Content = "Enable", IsChecked = on, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        box.SetResourceReference(ForegroundProperty, "Text");
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    private Button PresetButton(IEnumerable<(string Name, Action Act)> presets)
    {
        var button = new Button { Content = "Presets ▾", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => ShowMenu(button, presets);
        return button;
    }

    private static void ShowMenu(UIElement target, IEnumerable<(string Name, Action Act)> items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var (name, act) in items) menu.Items.Add(Item(name, act));
        menu.IsOpen = true;
    }

    private static DockPanel Page(CheckBox? enable, FrameworkElement? right, IEnumerable<UIElement> knobs)
    {
        var page = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10), MinHeight = 34 };
        if (enable != null) top.Children.Add(enable);
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Insert(0, right);
        }
        DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(top);
        var items = knobs.ToList();
        var grid = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Top };
        foreach (var k in items) grid.Children.Add(k);
        page.Children.Add(grid);
        return page;
    }

    // ---- visualizer ----

    private void SetVisualizer(int mode)
    {
        mode = Math.Clamp(mode, 0, Visualizer.Modes.Length - 1);
        Viz.Mode = mode;
        Viz.InvalidateVisual();
        VizCaption.Text = mode == 0 ? "Visualizer off. Click to turn on." : Visualizer.Modes[mode];
        if (mode == 0) _vizTimer.Stop(); else _vizTimer.Start();
        if (_settings.Viz != mode)
        {
            _settings.Viz = mode;
            SaveSettings();
        }
        if (_vizRadios.Count > mode) _vizRadios[mode].IsChecked = true;
    }

    private void Viz_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // the sidebar would start dragging the window
        SetVisualizer((Viz.Mode + 1) % Visualizer.Modes.Length);
    }

    private void Viz_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowMenu(VizCard, Visualizer.Modes.Select((name, i) => (name, (Action)(() => SetVisualizer(i)))));
    }

    // ---- settings ----

    private readonly List<RadioButton> _vizRadios = [];

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tab in new[] { SongsTab, AlbumsTab, GenresTab, PlaylistsTab, YouTubeTab, EffectsTab }) tab.IsChecked = false;
        _settingsOpen = true;
        Render();
    }

    private void Eq_Click(object sender, RoutedEventArgs e) => EffectsTab.IsChecked = true;

    private void BuildSettings()
    {
        Heading("Download quality", "For the YouTube tab. Songs you already have stay as they are.");
        foreach (var (key, name) in AppSettings.Qualities)
        {
            var radio = Choice(name, "quality", "");
            radio.IsChecked = key == _settings.Quality;
            radio.Checked += (_, _) => { _settings.Quality = key; SaveSettings(); };
            SettingsList.Children.Add(radio);
        }
        Heading("Visualizer", "Shown in the sidebar. You can also click it to switch, or right-click it for this list.");
        for (int i = 0; i < Visualizer.Modes.Length; i++)
        {
            var mode = i;
            var radio = Choice(Visualizer.Modes[i], "viz", "");
            radio.Checked += (_, _) => SetVisualizer(mode);
            _vizRadios.Add(radio);
            SettingsList.Children.Add(radio);
        }

        void Heading(string title, string note)
        {
            var head = new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, SettingsList.Children.Count == 0 ? 0 : 14, 0, 0) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var sub = new TextBlock { Text = note, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SettingsList.Children.Add(head);
            SettingsList.Children.Add(sub);
        }

        RadioButton Choice(string text, string group, string glyph) =>
            new() { Content = text, Tag = glyph, GroupName = group, Style = (Style)FindResource("Segment"), Margin = new Thickness(0, 0, 8, 3) };
    }

    private void SaveSettings()
    {
        try { Store.Save("settings.json", _settings); }
        catch (Exception ex) { SetStatus("Couldn't save settings: " + ex.Message, false); }
    }
}
