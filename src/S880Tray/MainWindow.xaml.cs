using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace S880Tray;

public partial class MainWindow : Window
{
    internal static readonly (string Key, string Label)[] Sources = [("usb", "USB"), ("bluetooth", "Bluetooth"), ("optical", "Optical"), ("coaxial", "Coaxial"), ("line-in-1", "Line In 1"), ("line-in-2", "Line In 2")];
    internal static readonly (string Key, string Label)[] Presets = [("classic", "Classic"), ("monitor", "Monitor"), ("dynamic", "Dynamic"), ("vocal", "Vocal"), ("custom", "Custom")];
    private readonly Slider[] _sliders = new Slider[6];
    private readonly TextBlock[] _gainLabels = new TextBlock[6];
    private readonly decimal[] _baseline = new decimal[6];
    private readonly bool[] _userGesture = new bool[6];
    private readonly Dictionary<string, Button> _sourceButtons = new();
    private readonly Dictionary<string, Button> _presetButtons = new();
    private readonly IControllerBackend? _backend;
    private readonly bool _preview;
    private TrayController? _tray;
    private bool _busy, _loadedGains, _updating, _exitRequested, _allowClose;
    private string? _operationNote;
    private bool _loadedVolume, _volumeUserGesture;
    private int _volumeBaseline;
    private bool _rendering;
    private string? _lastSourceReadText, _lastPresetReadText;
    private string? _selectedSourceKey, _selectedPresetKey;
    private bool _darkTheme;
    private readonly UserSettingsStore? _settings;
    private bool CanAnimate => IsLoaded && !_rendering && SystemParameters.ClientAreaAnimation;
    private Task _operation = Task.CompletedTask;
    private Task _initialRefresh = Task.CompletedTask;
    private bool _initialRefreshStarted;
    public bool LastOperationSucceeded { get; private set; }
    public string? LastError { get; private set; }
    internal double LifecycleDraft { get => _sliders[2].Value; set => _sliders[2].Value = value; }
    internal bool LifecycleBusy => _busy;
    internal Task WaitForInitialRefreshAsync() => _initialRefresh;

    internal MainWindow(IControllerBackend? backend, bool preview, string? startupError = null)
    {
        InitializeComponent();
        _backend = backend; _preview = preview;
        ThemeService.Apply(Resources, false);
        if (backend is Backend && !preview)
        {
            _settings = new UserSettingsStore(backend.DataDirectory);
            try { _darkTheme = _settings.ReadDarkTheme(); }
            catch (Exception error) { ShowSettingsError("The saved theme could not be read. Light is shown for this window. " + error.Message); }
            UpdateStartupState(StartupRegistration.Read());
        }
        else { StartupToggle.IsEnabled = false; StartupToggle.IsChecked = false; }
        ThemeService.Apply(Resources, _darkTheme);
        UpdateThemeButton();
        SourceInitialized += (_, _) => ThemeService.ApplyCaption(this, _darkTheme);
        CustomExpander.Expanded += (_, _) => UpdateDrawerPresentation();
        CustomExpander.Collapsed += (_, _) => UpdateDrawerPresentation();
        DetailsExpander.Expanded += (_, _) => ResizeForContent();
        DetailsExpander.Collapsed += (_, _) => ResizeForContent();
        AttachTactileMotion(RefreshButton); AttachTactileMotion(ApplyButton);
        VolumeSlider.ValueChanged += (_, _) => UpdateVolumeLabel();
        VolumeSlider.PreviewMouseLeftButtonDown += (_, _) => ArmVolumeGesture();
        VolumeSlider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitVolumeGestureAsync()), true);
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, args) => { if (args.Canceled) _volumeUserGesture = false; else await CommitVolumeGestureAsync(); }), true);
        VolumeSlider.PreviewKeyDown += (_, args) => { if (IsGainKey(args.Key)) ArmVolumeGesture(); };
        VolumeSlider.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(async (_, args) => { if (IsGainKey(args.Key)) await CommitVolumeGestureAsync(); }), true);
        foreach (var item in Sources)
        {
            var button = new Button { Content = item.Label };
            AttachTactileMotion(button);
            button.Click += async (_, _) => await OperateAsync(async () => { ApplySource(await Call("source", item.Key)); });
            SourceButtons.Children.Add(button); _sourceButtons.Add(item.Key, button);
        }
        foreach (var item in Presets)
        {
            var button = new Button { Content = item.Label, Style = (Style)FindResource("PresetRowStyle"), MinHeight = 32, HorizontalContentAlignment = HorizontalAlignment.Left };
            AttachTactileMotion(button);
            button.Click += async (_, _) => await SelectPresetAsync(item.Key);
            PresetButtons.Children.Add(button); _presetButtons.Add(item.Key, button);
        }
        var frequencies = new[] { "62", "250", "1000", "4000", "8000", "16000" };
        for (var index = 0; index < 6; index++)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = frequencies[index], FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
            var hertz = new TextBlock { Text = "Hz", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center }; hertz.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(hertz);
            var slider = new Slider { Style = (Style)FindResource("GainFader"), Orientation = Orientation.Vertical, Minimum = -3, Maximum = 3, TickFrequency = 0.5, SmallChange = 0.5, LargeChange = 0.5, IsSnapToTickEnabled = true, Height = 80, Width = 36, Margin = new Thickness(0, 4, 0, 5), TickPlacement = System.Windows.Controls.Primitives.TickPlacement.Both, IsEnabled = false };
            System.Windows.Automation.AutomationProperties.SetName(slider, frequencies[index] + " Hz gain in decibels");
            var gain = new TextBlock { Text = "— dB", FontFamily = new FontFamily("Consolas"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            _sliders[index] = slider; _gainLabels[index] = gain;
            slider.ValueChanged += (_, _) => UpdateDirty();
            var bandIndex = index;
            slider.PreviewMouseLeftButtonDown += (_, _) => ArmUserGesture(bandIndex);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitUserGestureAsync(bandIndex)), true);
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, args) => { if (args.Canceled) _userGesture[bandIndex] = false; else await CommitUserGestureAsync(bandIndex); }), true);
            slider.PreviewKeyDown += (_, args) => { if (IsGainKey(args.Key)) ArmUserGesture(bandIndex); };
            slider.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(async (_, args) => { if (IsGainKey(args.Key)) await CommitUserGestureAsync(bandIndex); }), true);
            panel.Children.Add(slider); panel.Children.Add(gain); BandControls.Children.Add(panel);
        }
        Closing += WindowClosing;
        if (preview)
        {
            InputText.Text = "USB"; ReadTimeText.Text = "Preview · sample data"; PresetStatus.Text = "Classic · preview";
            _selectedSourceKey = "usb"; _selectedPresetKey = "classic";
            Highlight(_sourceButtons, "usb"); Highlight(_presetButtons, "classic");
            GainTimeText.Text = "Preview · speaker not accessed"; MessageText.Text = "Preview mode: this window shows sample data and does not connect to the speaker.";
            GainDescription.Text = "Classic selected · Showing stored Custom gains. Release a slider to enable Custom.";
            PresetExplanation.Text = "Classic factory preset selected · preview. Saved Custom gains are independent.";
            _loadedGains = true; UpdateDirty();
            _loadedVolume = true; _volumeBaseline = 12; VolumeSlider.Value = 12; VolumeTimeText.Text = "Preview · example level";
        }
        else if (startupError is not null) { MessageText.Text = startupError; ConnectionText.Text = "Unavailable"; DetailsExpander.IsExpanded = true; }
        SetControls();
        UpdateDrawerPresentation();
        Loaded += (_, _) => ResizeForContent();
    }

    private void ThemeClick(object sender, RoutedEventArgs e) => ChangeTheme(!_darkTheme, persist: true);
    private bool ChangeTheme(bool dark, bool persist)
    {
        try
        {
            if (persist && _settings is not null) _settings.SaveDarkTheme(dark);
            _darkTheme = dark; ThemeService.Apply(Resources, dark); ThemeService.ApplyCaption(this, dark); UpdateThemeButton();
            Highlight(_sourceButtons, _selectedSourceKey); Highlight(_presetButtons, _selectedPresetKey); UpdateBusyVisual();
            if (persist && _settings is null && !_preview && _backend is not FixtureBackend)
                ShowSettingsError("Theme changed for this window only; the settings directory is unavailable.");
            return true;
        }
        catch (Exception error) { ShowSettingsError("The theme could not be saved. " + error.Message); return false; }
    }
    private void UpdateThemeButton()
    {
        ThemeButton.Content = _darkTheme ? "Light" : "Dark";
        ThemeButton.ToolTip = _darkTheme ? "Current theme: Dark. Switch to Light." : "Current theme: Light. Switch to Dark.";
    }
    private void StartupClick(object sender, RoutedEventArgs e)
    {
        if (_preview || _backend is not Backend) { StartupToggle.IsChecked = false; return; }
        UpdateStartupState(StartupRegistration.SetEnabled(StartupToggle.IsChecked == true));
    }
    private void UpdateStartupState(StartupRegistrationState state)
    {
        StartupToggle.IsChecked = state.Enabled;
        StartupToggle.ToolTip = state.Error ?? (state.Enabled ? "Launch to the tray at Windows sign-in is enabled." : "Launch to the tray at Windows sign-in is disabled.");
        if (state.Error is not null) ShowSettingsError(state.Error);
    }
    private void ShowSettingsError(string message) { MessageText.Text = message; DetailsExpander.IsExpanded = true; }

    internal void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show control panel", null, (_, _) => ShowPanel());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var source in Sources) menu.Items.Add(source.Label, null, async (_, _) => await OperateAsync(async () => ApplySource(await Call("source", source.Key))));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Refresh status", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Exit", null, async (_, _) => await RequestExitAsync());
        menu.Opening += (_, _) => { foreach (Forms.ToolStripItem item in menu.Items) if (item.Text != "Show control panel" && item.Text != "Exit") item.Enabled = !_busy && _backend is not null; };
        _tray = new TrayController(Dispatcher, menu, SelectSourceUsbAsync, ShowPanel, () => LastError);
    }
    internal void ShowPanel()
    {
        Show(); WindowState = WindowState.Normal;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // A second explicit native show also handles hidden STARTUPINFO on the first show.
        NativeWindow.ShowWindow(handle, 9);
        NativeWindow.ShowWindow(handle, 5);
        Activate(); NativeWindow.SetForegroundWindow(handle);
        StartInitialRefresh();
    }
    private void StartInitialRefresh()
    {
        if (_initialRefreshStarted || _preview || _backend is null || _exitRequested) return;
        _initialRefreshStarted = true;
        _initialRefresh = RunInitialRefreshAsync();
    }
    private async Task RunInitialRefreshAsync()
    {
        // A tray or activation request may already own the backend. Join its operation,
        // then perform the one read that belongs to the first visible panel.
        while (_busy && !_exitRequested) await _operation;
        if (!_exitRequested) await RefreshAsync();
    }
    private void WindowClosing(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Hide(); } }
    internal async Task RequestExitAsync()
    {
        _exitRequested = true; SetControls();
        if (_busy) { ShowPanel(); MessageText.Text = "Waiting for the current operation to finish before exiting, so an in-flight write is not interrupted."; await _operation; }
        _tray?.Dispose(); _tray = null; _allowClose = true; Close(); Application.Current.Shutdown();
    }
    private Task<JsonElement> Call(params string[] command) => (_backend ?? throw new InvalidOperationException("The control program or configuration is unavailable.")).RunAsync(command);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    internal async Task<bool> SelectSourceUsbAsync()
    {
        while (_busy && !_exitRequested) await _operation;
        return await OperateAsync(async () =>
        {
            var response = await Call("source", "usb");
            ApplySource(response);
            if (response.GetProperty("result").GetProperty("currentAfter").GetProperty("name").GetString() != "usb")
                throw new InvalidDataException("The speaker did not confirm USB as the input source.");
        });
    }
    internal Task RefreshAsync() => OperateAsync(async () =>
    {
        ApplySource(await Call("source", "get"));
        if (_exitRequested) return;
        var preset = await Call("eq", "get"); SetPreset(preset.GetProperty("result").GetProperty("presetName").GetString(), Timestamp(preset));
        if (_exitRequested) return;
        await ReadCustomGainsAsync();
        if (_exitRequested) return;
        ApplyVolume(await Call("volume", "get"));
    });
    private void ApplyVolume(JsonElement report)
    {
        var result = report.GetProperty("result");
        if (!result.TryGetProperty("maximum", out var maximum) || !maximum.TryGetInt32(out var max) || max != 30 ||
            !result.TryGetProperty("currentAfter", out var current) || !current.TryGetInt32(out var level) || level is < 0 or > 30)
            throw new InvalidDataException("The speaker returned an unsupported volume range. Refresh before adjusting volume.");
        _volumeBaseline = level; _loadedVolume = true; VolumeSlider.Value = level;
        VolumeTimeText.Text = "Read at " + Timestamp(report); UpdateVolumeLabel();
    }
    private void UpdateVolumeLabel()
    {
        VolumeValueText.Text = _loadedVolume ? ((int)VolumeSlider.Value).ToString(CultureInfo.InvariantCulture) + " / 30" : "— / 30";
        VolumeHintText.Text = _loadedVolume && (int)VolumeSlider.Value != _volumeBaseline ? (_busy ? "Setting level…" : "Pending · release to set") : "0–30 · release to set";
    }
    private void ArmVolumeGesture() { if (!_busy && !_preview && _loadedVolume && !_exitRequested) _volumeUserGesture = true; }
    private async Task CommitVolumeGestureAsync()
    {
        if (!_volumeUserGesture) return;
        _volumeUserGesture = false;
        if (_busy || !_loadedVolume || _preview || _exitRequested) return;
        var level = VolumeSlider.Value;
        if (level == _volumeBaseline) return;
        if (level < 0 || level > 30 || level != Math.Truncate(level))
        {
            VolumeSlider.Value = _volumeBaseline;
            MessageText.Text = "Choose a whole volume level from 0 to 30.";
            return;
        }
        await OperateAsync(async () => ApplyVolume(await Call("volume", "set", ((int)level).ToString(CultureInfo.InvariantCulture))));
    }
    private Task SelectPresetAsync(string key) => OperateAsync(async () =>
    {
        ApplyPresetSet(await Call("eq", "set", key));
        _loadedGains = false;
        if (_exitRequested) return;
        await ReadCustomGainsAsync();
    });
    private async Task ReadCustomGainsAsync()
    {
        var gains = await Call("eq", "custom-get");
        var result = gains.GetProperty("result");
        if (!result.TryGetProperty("semanticDecoded", out var decoded) || decoded.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("customEq", out var custom) || custom.ValueKind != JsonValueKind.Object ||
            !custom.TryGetProperty("bands", out var bands) || bands.ValueKind != JsonValueKind.Array)
        {
            _loadedGains = false;
            GainTimeText.Text = "Custom format not supported";
            _operationNote = "The speaker returned an unsupported custom format, so gain adjustment is disabled. Input and preset controls remain available.";
            return;
        }
        ApplyBands(bands); GainTimeText.Text = "Read at " + Timestamp(gains);
    }
    private static bool IsGainKey(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End;
    private void ArmUserGesture(int index) { if (!_busy && !_preview && _loadedGains && !_exitRequested) _userGesture[index] = true; }
    private async Task CommitUserGestureAsync(int index)
    {
        if (!_userGesture[index]) return;
        _userGesture[index] = false;
        if (_busy || _updating || !_loadedGains || _preview || _exitRequested) return;
        var gain = (decimal)_sliders[index].Value;
        if (gain == _baseline[index]) return;
        await ApplyBandChangesAsync([(index + 1, gain)]);
    }
    private async void ApplyClick(object sender, RoutedEventArgs e) => await ApplyChangesAsync();
    private async Task ApplyChangesAsync()
    {
        var changes = _sliders.Select((slider, index) => (Band: index + 1, Gain: (decimal)slider.Value)).Where(item => item.Gain != _baseline[item.Band - 1]).ToArray();
        await ApplyBandChangesAsync(changes);
    }
    private Task ApplyBandChangesAsync((int Band, decimal Gain)[] changes) => OperateAsync(async () =>
        {
            foreach (var change in changes)
            {
                if (_exitRequested) break;
                var response = await Call("eq", "custom-set", "--band", change.Band.ToString(CultureInfo.InvariantCulture), "--gain-db", change.Gain.ToString(CultureInfo.InvariantCulture));
                var result = response.GetProperty("result");
                // Preserve not-yet-applied drafts while advancing the verified baseline after every band.
                ApplyBands(result.GetProperty("after").GetProperty("bands"), preserveDrafts: true, appliedBand: change.Band);
                SetPreset(result.GetProperty("presetAfter").GetProperty("name").GetString(), Timestamp(response));
                GainTimeText.Text = "Read at " + Timestamp(response);
            }
        });
    private async Task<bool> OperateAsync(Func<Task> action)
    {
        if (_busy || _preview || _backend is null || _exitRequested) return false;
        var succeeded = false;
        _busy = true; LastOperationSucceeded = false; LastError = null; _operationNote = null; ConnectionText.Text = "Working"; MessageText.Text = "Reading or updating the speaker…"; SetControls();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
        try { await action(); succeeded = true; LastOperationSucceeded = true; ConnectionText.Text = "Idle"; MessageText.Text = _operationNote ?? "Operation complete. The display shows the latest read result."; DetailsExpander.IsExpanded = false; }
        catch (Exception error)
        {
            LastError = error.Message; ConnectionText.Text = "Unconfirmed"; MessageText.Text = error.Message; DetailsExpander.IsExpanded = true;
            _loadedGains = false; GainTimeText.Text = "State unconfirmed · refresh required"; ReadTimeText.Text = _lastSourceReadText is null ? "Not read yet" : _lastSourceReadText + " · may be stale";
            _loadedVolume = false; VolumeTimeText.Text = "State unconfirmed · refresh required"; UpdateVolumeLabel();
            PresetStatus.Text = _lastPresetReadText is null ? "Not read yet" : _lastPresetReadText + " · last confirmed"; _selectedPresetKey = null; Highlight(_presetButtons, null);
            PresetExplanation.Text = "Preset state is unconfirmed. Refresh to read it.";
            if (!IsVisible) _tray?.ShowError(error.Message);
        }
        finally { _busy = false; SetControls(); completion.TrySetResult(); }
        return succeeded;
    }
    private void SetControls()
    {
        var available = !_busy && !_exitRequested && (_preview || _backend is not null);
        SourceButtons.IsEnabled = available; PresetButtons.IsEnabled = available; RefreshButton.IsEnabled = available;
        foreach (var slider in _sliders) if (slider is not null) slider.IsEnabled = available && _loadedGains;
        VolumeSlider.IsEnabled = available && _loadedVolume;
        UpdateVolumeLabel();
        UpdateBusyVisual();
        UpdateDirty();
    }
    private void UpdateDirty()
    {
        if (_updating || _sliders.Any(slider => slider is null)) return;
        var dirty = 0;
        for (var i = 0; i < 6; i++) { if ((decimal)_sliders[i].Value != _baseline[i]) dirty++; _gainLabels[i].Text = _loadedGains ? _sliders[i].Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB" : "— dB"; }
        DirtyText.Text = !_loadedGains ? "Refresh to adjust" : dirty == 0 ? "Release a slider to save its gain" : $"{dirty} pending band{(dirty == 1 ? "" : "s")}";
        ApplyButton.Visibility = dirty > 0 && _loadedGains ? Visibility.Visible : Visibility.Hidden;
        ApplyButton.IsEnabled = _loadedGains && dirty > 0 && !_busy && !_preview && !_exitRequested;
    }
    private void ApplySource(JsonElement report)
    {
        var key = report.GetProperty("result").GetProperty("currentAfter").GetProperty("name").GetString();
        InputText.Text = Sources.FirstOrDefault(item => item.Key == key).Label ?? "Unknown input";
        ReadTimeText.Text = "Read at " + Timestamp(report); _lastSourceReadText = ReadTimeText.Text; _selectedSourceKey = key; Highlight(_sourceButtons, key);
    }
    private void ApplyPresetSet(JsonElement report) => SetPreset(report.GetProperty("result").GetProperty("currentAfter").GetProperty("name").GetString(), Timestamp(report));
    private void SetPreset(string? key, string time)
    {
        var name = Presets.FirstOrDefault(item => item.Key == key).Label ?? "Unknown preset";
        PresetStatus.Text = name + " · " + time; _lastPresetReadText = PresetStatus.Text; _selectedPresetKey = key; Highlight(_presetButtons, key);
        CustomExpander.IsExpanded = key == "custom";
        PresetExplanation.Text = key == "custom" ? "Custom gains are active." : name + " factory preset is selected. Saved Custom gains are independent.";
        GainDescription.Text = key == "custom" ? "Custom selected. Release a slider to save its gain." : "These are your saved Custom settings. Editing a gain will select Custom.";
    }
    private void Highlight(Dictionary<string, Button> buttons, string? key)
    {
        foreach (var pair in buttons)
        {
            System.Windows.Automation.AutomationProperties.SetItemStatus(pair.Value, pair.Key == key ? "Selected" : "Not selected");
            pair.Value.Tag = pair.Key == key ? "selected" : "unselected";
            var previous = (pair.Value.Background as SolidColorBrush)?.Color ?? Colors.White;
            var target = ThemeService.GetColor(Resources, pair.Key == key ? "SelectedBrush" : ReferenceEquals(buttons, _presetButtons) ? "PanelBrush" : "ControlBrush");
            var brush = new SolidColorBrush(target); pair.Value.Background = brush;
            pair.Value.SetResourceReference(Control.BorderBrushProperty, pair.Key == key ? "SelectionBorderBrush" : "ControlBorderBrush");
            if (CanAnimate) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, target, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        }
    }
    private void AttachTactileMotion(Button button)
    {
        var transform = new TranslateTransform(); button.RenderTransform = transform;
        void Move(double value)
        {
            if (!CanAnimate) { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0; return; }
            var from = transform.Y; transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = value;
            if (CanAnimate) transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
        }
        button.MouseEnter += (_, _) => Move(-0.5); button.MouseLeave += (_, _) => Move(0);
        button.PreviewMouseLeftButtonDown += (_, _) => Move(1); button.PreviewMouseLeftButtonUp += (_, _) => Move(0);
    }
    private void UpdateBusyVisual()
    {
        var rotation = (RotateTransform)RefreshGlyph.RenderTransform;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0;
        StatusDot.BeginAnimation(OpacityProperty, null); StatusDot.Opacity = 1;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _busy ? "StatusBusyBrush" : LastError is not null ? "StatusErrorBrush" : "StatusIdleBrush");
        if (_busy && CanAnimate)
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }
    private void UpdateDrawerPresentation()
    {
        var expanded = CustomExpander.IsExpanded;
        CustomDrawer.IsHitTestVisible = expanded;
        KeyboardNavigation.SetTabNavigation(CustomDrawer, expanded ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        CustomDrawer.BeginAnimation(OpacityProperty, null); CustomDrawer.Opacity = 1;
        var transform = (TranslateTransform)CustomDrawer.RenderTransform;
        transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0;
        if (expanded && CanAnimate)
        {
            CustomDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(180)));
        }
        ResizeForContent();
    }
    private void ResizeForContent()
    {
        if (!IsLoaded || _rendering || WindowState != WindowState.Normal) return;
        var target = 410d + (CustomExpander.IsExpanded ? 190 : 0) + (DetailsExpander.IsExpanded ? 155 : 0);
        target = Math.Max(MinHeight, Math.Min(target, SystemParameters.WorkArea.Height - 32));
        var oldHeight = ActualHeight > 0 ? ActualHeight : Height;
        BeginAnimation(HeightProperty, null); Height = target;
        if (CanAnimate) BeginAnimation(HeightProperty, new DoubleAnimation(oldHeight, target, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }
    private void ApplyBands(JsonElement bands, bool preserveDrafts = false, int appliedBand = 0)
    {
        var values = bands.EnumerateArray().Select(band => (Index: band.GetProperty("band").GetInt32(), Gain: band.GetProperty("gainDb").GetDecimal())).ToArray();
        if (values.Length != 6 || !values.Select(value => value.Index).SequenceEqual(Enumerable.Range(1, 6)) || values.Any(value => value.Gain < -3 || value.Gain > 3 || value.Gain * 2 != decimal.Truncate(value.Gain * 2))) throw new InvalidDataException("The speaker returned unsupported gain data. Check the operation logs.");
        _updating = true;
        foreach (var value in values) { var i = value.Index - 1; var dirty = (decimal)_sliders[i].Value != _baseline[i]; _baseline[i] = value.Gain; if (!preserveDrafts || !dirty || value.Index == appliedBand) _sliders[i].Value = (double)value.Gain; }
        _loadedGains = true; _updating = false; UpdateDirty();
    }
    private static string Timestamp(JsonElement report) => DateTimeOffset.Parse(report.GetProperty("completedAtUtc").GetString()!, CultureInfo.InvariantCulture).ToLocalTime().ToString("HH:mm:ss");
    private void LogsClick(object sender, RoutedEventArgs e) { if (_backend is not null) Process.Start(new ProcessStartInfo("explorer.exe", _backend.DataDirectory) { UseShellExecute = true }); }
    internal void Render(string path)
    {
        _rendering = true;
        try
        {
            RenderSurface(path);
            if (!_preview) return;
            if (Path.GetFileName(path).StartsWith("minimum-size", StringComparison.OrdinalIgnoreCase))
            {
                var minimumOriginalDark = _darkTheme;
                try
                {
                    ChangeTheme(true, persist: false);
                    RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-dark.png"));
                }
                finally { ChangeTheme(minimumOriginalDark, persist: false); }
                return;
            }
            var originalHeight = Height;
            var originalCustom = CustomExpander.IsExpanded;
            var originalDetails = DetailsExpander.IsExpanded;
            var labels = new[] { ConnectionText, PresetStatus, PresetExplanation, GainDescription, MessageText }.ToDictionary(label => label, label => label.Text);
            var statusFill = StatusDot.Fill;
            var originalDark = _darkTheme;
            try
            {
                ChangeTheme(true, persist: false);
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-dark.png"));
                Highlight(_presetButtons, "dynamic"); PresetStatus.Text = "Dynamic · preview";
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-dark-preset.png"));
                ChangeTheme(originalDark, persist: false); PresetStatus.Text = labels[PresetStatus];
                var previousVolume = VolumeSlider.Value;
                VolumeSlider.Value = _volumeBaseline == 30 ? 29 : _volumeBaseline + 1;
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-pending.png"));
                VolumeSlider.Value = previousVolume;
                CustomExpander.IsExpanded = true; DetailsExpander.IsExpanded = false; Height = 600;
                PresetStatus.Text = "Custom · preview"; PresetExplanation.Text = "Custom gains active · preview only";
                GainDescription.Text = "Saved Custom gains · release to save. Preview only.";
                Highlight(_presetButtons, "custom");
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-custom.png"));
                ChangeTheme(true, persist: false); Highlight(_presetButtons, "custom");
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-dark-custom.png"));
                ChangeTheme(originalDark, persist: false); Highlight(_presetButtons, "custom");
                CustomExpander.IsExpanded = false; DetailsExpander.IsExpanded = true; Height = 565;
                ConnectionText.Text = "Unconfirmed"; PresetStatus.Text = "Classic · last confirmed";
                PresetExplanation.Text = "Preview error · state unconfirmed"; Highlight(_presetButtons, null);
                StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusErrorBrush");
                MessageText.Text = "Preview failure: Windows could not reach the speaker's Bluetooth control service. No setting command was sent. If EDIFIER Connect is running, force stop it in your phone settings. Switch the speaker to Bluetooth with its remote, then refresh.\nDetails: service discovery failed: Unreachable\nOpen Logs for the full operation report.";
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-error.png"));
                ChangeTheme(true, persist: false); Highlight(_presetButtons, null); StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusErrorBrush");
                RenderSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-dark-error.png"));
            }
            finally
            {
                ChangeTheme(originalDark, persist: false); Height = originalHeight; CustomExpander.IsExpanded = originalCustom; DetailsExpander.IsExpanded = originalDetails;
                foreach (var label in labels) label.Key.Text = label.Value;
                StatusDot.Fill = statusFill; Highlight(_presetButtons, "classic");
            }
        }
        finally { _rendering = false; }
    }
    private void RenderSurface(string path)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        GetWindowRect(handle, out var outer); GetClientRect(handle, out var client);
        var dpi = VisualTreeHelper.GetDpi(this);
        var chromeWidth = (outer.Right - outer.Left - client.Right + client.Left) / dpi.DpiScaleX;
        var chromeHeight = (outer.Bottom - outer.Top - client.Bottom + client.Top) / dpi.DpiScaleY;
        var clientWidth = Math.Max(1, Width - chromeWidth); var clientHeight = Math.Max(1, Height - chromeHeight);
        var surface = (FrameworkElement)Content;
        surface.Measure(new Size(clientWidth, clientHeight)); surface.Arrange(new Rect(0, 0, clientWidth, clientHeight)); surface.UpdateLayout();
        surface.InvalidateMeasure(); surface.Measure(new Size(clientWidth, clientHeight)); surface.Arrange(new Rect(0, 0, clientWidth, clientHeight)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(clientWidth), (int)Math.Ceiling(clientHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    internal async Task<object> RunOfflineChecksAsync(FixtureBackend fixture)
    {
        await RefreshAsync();
        var refreshOnlyQueries = fixture.Commands.SequenceEqual(new[] { "source get", "eq get", "eq custom-get", "volume get" });
        var beforeVolume = fixture.Commands.Count;
        VolumeSlider.Value = 13;
        var programmaticVolumeDoesNotWrite = fixture.Commands.Count == beforeVolume;
        ArmVolumeGesture(); VolumeSlider.Value = 14; VolumeSlider.Value = 13;
        var volumeDragDoesNotWrite = fixture.Commands.Count == beforeVolume;
        fixture.DelayNext = true;
        var volumeRelease = CommitVolumeGestureAsync();
        var volumeNotConfirmedBeforeReply = _volumeBaseline == 12;
        await volumeRelease; await CommitVolumeGestureAsync();
        var volumeReleaseWritesOnce = fixture.Commands.Skip(beforeVolume).SequenceEqual(new[] { "volume set 13" }) && _volumeBaseline == 13;
        var beforeZero = fixture.Commands.Count; ArmVolumeGesture(); VolumeSlider.Value = 0; await CommitVolumeGestureAsync();
        var zeroWriteVerified = fixture.Commands.Skip(beforeZero).SequenceEqual(new[] { "volume set 0" }) && VolumeSlider.Value == 0 && _volumeBaseline == 0;
        fixture.VolumeForTest = 0; await RefreshAsync();
        var zeroReadTruthful = _volumeBaseline == 0 && VolumeSlider.Value == 0 && VolumeValueText.Text == "0 / 30";
        var beforeDrafts = fixture.Commands.Count;
        _sliders[1].Value = 0.5; _sliders[3].Value = -0.5;
        var programmaticDraftsDoNotWrite = fixture.Commands.Count == beforeDrafts;
        var draftSnapshot = _sliders.Select(slider => slider.Value).ToArray();
        var sourceBeforeTheme = _selectedSourceKey; var presetBeforeTheme = _selectedPresetKey;
        var themeCalls = fixture.Commands.Count; var lightText = MessageText.Foreground;
        ChangeTheme(true, persist: false);
        var darkDetailsThemed = ((SolidColorBrush)MessageText.Foreground).Color == ThemeService.GetColor(Resources, "DetailsTextBrush");
        ChangeTheme(false, persist: false);
        var themePreservesState = fixture.Commands.Count == themeCalls && draftSnapshot.SequenceEqual(_sliders.Select(slider => slider.Value)) && sourceBeforeTheme == _selectedSourceKey && presetBeforeTheme == _selectedPresetKey && darkDetailsThemed;
        await ApplyChangesAsync();
        var onlyChangedBands = fixture.Commands.Skip(beforeDrafts).SequenceEqual(new[] { "eq custom-set --band 2 --gain-db 0.5", "eq custom-set --band 4 --gain-db -0.5" });
        var verifiedDraftsCleared = !ApplyButton.IsEnabled && _baseline[1] == 0.5m && _baseline[3] == -0.5m;
        _sliders[0].Value = 1;
        var beforePreset = fixture.Commands.Count;
        await SelectPresetAsync("dynamic");
        var presetRefreshesStoredGains = fixture.Commands.Skip(beforePreset).SequenceEqual(new[] { "eq set dynamic", "eq custom-get" }) && _sliders[0].Value == 0 && _sliders[1].Value == 0.5 && !ApplyButton.IsEnabled && PresetStatus.Text.StartsWith("Dynamic", StringComparison.Ordinal) && !CustomExpander.IsExpanded;
        var beforeExpand = fixture.Commands.Count; CustomExpander.IsExpanded = true;
        var explicitCustomEditDoesNotWrite = fixture.Commands.Count == beforeExpand;
        var beforeGesture = fixture.Commands.Count;
        ArmUserGesture(2); _sliders[2].Value = 0.5; _sliders[2].Value = 1; _sliders[2].Value = 0.5;
        var dragChangesDoNotWrite = fixture.Commands.Count == beforeGesture;
        fixture.DelayNext = true;
        var release = CommitUserGestureAsync(2);
        var customNotClaimedBeforeReply = PresetStatus.Text.StartsWith("Dynamic", StringComparison.Ordinal);
        await release; await CommitUserGestureAsync(2);
        var releaseCommitsOnlyChangedBand = fixture.Commands.Skip(beforeGesture).SequenceEqual(new[] { "eq custom-set --band 3 --gain-db 0.5" }) && PresetStatus.Text.StartsWith("Custom", StringComparison.Ordinal) && _baseline[2] == 0.5m && CustomExpander.IsExpanded;
        await SelectPresetAsync("classic");
        _sliders[0].Value = 0.5; _sliders[5].Value = 0.5;
        fixture.FailNext = true; var beforeFailure = fixture.Commands.Count;
        await ApplyChangesAsync();
        var stopsOnFailure = fixture.Commands.Count == beforeFailure + 1 && !LastOperationSucceeded && !_loadedGains && !_loadedVolume && PresetStatus.Text.StartsWith("Classic", StringComparison.Ordinal) && PresetStatus.Text.Contains("last confirmed", StringComparison.Ordinal);
        fixture.DelayNext = true;
        var first = RefreshAsync(); var second = RefreshAsync();
        await Task.WhenAll(first, second);
        var oneOperationAtATime = fixture.MaximumConcurrent == 1;
        fixture.DelayNext = true; var beforeUsb = fixture.Commands.Count;
        var priorOperation = RefreshAsync(); var usbRequest = SelectSourceUsbAsync();
        await Task.WhenAll(priorOperation, usbRequest);
        var usbRequestQueuedAndVerified = usbRequest.Result && fixture.Commands.Skip(beforeUsb).SequenceEqual(new[] { "source get", "eq get", "eq custom-get", "volume get", "source usb" }) && InputText.Text == "USB" && fixture.MaximumConcurrent == 1;
        fixture.UnsupportedCustom = true; await RefreshAsync();
        var unsupportedCustomKeepsSourceAndPreset = LastOperationSucceeded && !_loadedGains && SourceButtons.IsEnabled && PresetButtons.IsEnabled && MessageText.Text.Contains("unsupported custom format", StringComparison.OrdinalIgnoreCase);
        var unreadFixture = new FixtureBackend { FailNext = true };
        var unreadWindow = new MainWindow(unreadFixture, false);
        await unreadWindow.RefreshAsync();
        var neverReadTruthful = unreadWindow.PresetStatus.Text == "Not read yet" && unreadWindow.ReadTimeText.Text == "Not read yet";
        fixture.FailNext = true; await RefreshAsync(); var onceFailed = PresetStatus.Text;
        fixture.FailNext = true; await RefreshAsync();
        var repeatFailureStable = PresetStatus.Text == onceFailed;
        await RefreshAsync();
        var selectedAutomationState = System.Windows.Automation.AutomationProperties.GetItemStatus(_sourceButtons["usb"]) == "Selected";
        CustomExpander.IsExpanded = false; DetailsExpander.IsExpanded = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; ShowActivated = false; ShowInTaskbar = false;
        var commandsBeforeShow = fixture.Commands.Count;
        Show(); await Task.Delay(240); UpdateLayout();
        var scroll = (ScrollViewer)Content;
        var compactViewportFits = scroll.ExtentHeight <= scroll.ViewportHeight + 0.5 && scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible;
        var viewport = new { windowWidth = ActualWidth, windowHeight = ActualHeight, clientWidth = scroll.ActualWidth, clientHeight = scroll.ActualHeight, extentHeight = scroll.ExtentHeight, viewportHeight = scroll.ViewportHeight, verticalScrollVisible = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, customVisible = CustomDrawer.IsVisible, noCommandsFromShowing = fixture.Commands.Count == commandsBeforeShow };
        Hide();
        var failureMessages = JsonSerializer.SerializeToElement(FixtureBackend.CheckFailureMessages());
        var themeStorage = JsonSerializer.SerializeToElement(FixtureBackend.CheckThemeStorage());
        return new { passed = themePreservesState && themeStorage.GetProperty("passed").GetBoolean() && compactViewportFits && neverReadTruthful && repeatFailureStable && selectedAutomationState && refreshOnlyQueries && programmaticVolumeDoesNotWrite && volumeDragDoesNotWrite && volumeNotConfirmedBeforeReply && volumeReleaseWritesOnce && zeroWriteVerified && zeroReadTruthful && usbRequestQueuedAndVerified && programmaticDraftsDoNotWrite && onlyChangedBands && verifiedDraftsCleared && presetRefreshesStoredGains && explicitCustomEditDoesNotWrite && dragChangesDoNotWrite && customNotClaimedBeforeReply && releaseCommitsOnlyChangedBand && stopsOnFailure && oneOperationAtATime && unsupportedCustomKeepsSourceAndPreset && failureMessages.GetProperty("passed").GetBoolean(), themePreservesState, themeStorage, compactViewportFits, neverReadTruthful, repeatFailureStable, selectedAutomationState, viewport, refreshOnlyQueries, programmaticVolumeDoesNotWrite, volumeDragDoesNotWrite, volumeNotConfirmedBeforeReply, volumeReleaseWritesOnce, zeroWriteVerified, zeroReadTruthful, usbRequestQueuedAndVerified, programmaticDraftsDoNotWrite, onlyChangedBands, verifiedDraftsCleared, presetRefreshesStoredGains, explicitCustomEditDoesNotWrite, dragChangesDoNotWrite, customNotClaimedBeforeReply, releaseCommitsOnlyChangedBand, stopsOnFailure, oneOperationAtATime, unsupportedCustomKeepsSourceAndPreset, failureMessages, fixtureCommands = fixture.Commands, hardwareAccess = false };
    }
}
