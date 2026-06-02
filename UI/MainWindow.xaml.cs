using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Navigation;
using System.Windows.Documents;
using System.Windows.Input;
using Voxie.Models;
using Voxie.Services;
using Microsoft.Win32;

namespace Voxie;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _galleryLayoutTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly AppSettings _settings;
    private readonly MicrophoneCaptureService _microphoneCapture = new();
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly VrChatOscChatboxService _vrChatOsc = new();
    private readonly WhisperTranscriptionService _transcriptionService = new();
    private UpdateCheckResult? _latestUpdate;
    private IReadOnlyList<AudioInputDevice> _audioDevices = [];
    private IReadOnlyList<(ArtPiece Piece, double AspectRatio)> _recentGalleryArt = [];
    private IReadOnlyList<(ArtPiece Piece, double AspectRatio)> _bannerGalleryArt = [];
    private IReadOnlyList<(ArtPiece Piece, double AspectRatio)> _galleryArt = [];
    private readonly Dictionary<string, BitmapImage> _galleryPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private double _galleryViewportWidth;
    private bool _galleryLoaded;
    private string? _capturePath;
    private bool _isCapturingKeybind;
    private bool _isRecording;
    private bool _isTranscribing;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettingsService.Load();
        _hotkey.Pressed += (_, _) => BeginPhraseCapture();
        _microphoneCapture.SilenceDetected += (_, _) => Dispatcher.InvokeAsync(CompletePhraseCaptureAsync);
        _galleryLayoutTimer.Tick += (_, _) =>
        {
            _galleryLayoutTimer.Stop();
            RebuildGallery();
        };
        Closed += (_, _) =>
        {
            _hotkey.Dispose();
            _microphoneCapture.Dispose();
            _vrChatOsc.Dispose();
            Application.Current.Shutdown();
        };
        Loaded += async (_, _) =>
        {
            RegisterPhraseHotkey();
            await CheckForUpdatesAsync(showWhenCurrent: false);
        };
        GalleryScrollViewer.SizeChanged += (_, _) => ScheduleGalleryRebuild();
        LoadSettings();
        RefreshEngineStatus();
    }

    private void BeginPhraseCapture()
    {
        if (_isCapturingKeybind || _isRecording || _isTranscribing)
            return;
        if (AudioSourceComboBox.SelectedItem is not AudioInputDevice device)
        {
            SetStatus("No microphone is available. Connect one and reopen Voxie.");
            return;
        }

        try
        {
            TranscriptTextBox.Clear();
            _capturePath = Path.Combine(Path.GetTempPath(), "Voxie", $"phrase-{DateTime.Now:yyyyMMdd-HHmmssfff}.wav");
            _microphoneCapture.Start(device.DeviceNumber, _capturePath, TimeSpan.FromSeconds(_settings.SilenceDurationSeconds));
            _isRecording = true;
            CaptureStatusText.Text = "Listening...";
            CaptureHintText.Text = $"Phrase completes after {_settings.SilenceDurationSeconds:0.0} seconds of silence.";
            SetStatus($"Recording phrase from {device.Name}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start phrase capture: {ex.Message}");
        }
    }

    private async Task CompletePhraseCaptureAsync()
    {
        if (!_isRecording || _capturePath is null)
            return;

        _isRecording = false;
        _microphoneCapture.Stop();
        CaptureStatusText.Text = "Transcribing...";
        CaptureHintText.Text = "Running local Whisper.";
        SetStatus("Silence detected. Transcribing completed phrase.");
        await TranscribeAsync(_capturePath);
        CaptureHintText.Text = $"Press {_settings.ActivationKey} to record another phrase.";
        if (!_settings.DisableVrChatOsc && TranscriptTextBox.Text != "[No speech detected]")
            await SendTranscriptToChatboxAsync("Sent completed phrase");
        if (_settings.AutoCopyTranscript)
            CopyTranscript();
    }

    private void StartRecording_Click(object sender, RoutedEventArgs e) => BeginPhraseCapture();

    private void CopyTranscript_Click(object sender, RoutedEventArgs e) => CopyTranscript();

    private void CopyTranscript()
    {
        Clipboard.SetText(TranscriptTextBox.Text);
        SetStatus("Transcript copied to clipboard.");
    }

    private void ClearTranscript_Click(object sender, RoutedEventArgs e)
    {
        TranscriptTextBox.Clear();
        SetStatus("Transcript cleared.");
    }

    private void RepeatChatbox_Click(object sender, RoutedEventArgs e)
    {
        _ = SendTranscriptToChatboxAsync("Repeated transcript");
    }

    private async Task SendTranscriptToChatboxAsync(string successMessage)
    {
        if (_settings.DisableVrChatOsc)
        {
            SetStatus("VRChat OSC chatbox sending is disabled in Settings.");
            return;
        }

        var text = TranscriptTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("There is no transcript text to send.");
            return;
        }

        try
        {
            var chunkCount = await _vrChatOsc.SendAsync(text);
            SetStatus($"{successMessage} to VRChat as {chunkCount} message(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not send to VRChat OSC: {ex.Message}");
        }
    }

    private async Task RefreshGalleryAsync()
    {
        SetStatus("Loading hosted gallery previews...");
        IReadOnlyList<ArtPiece> artPieces;
        try
        {
            artPieces = await HostedGalleryService.LoadAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load the hosted gallery: {ex.Message}");
            artPieces = [];
        }

        _recentGalleryArt = artPieces
            .OrderByDescending(piece => piece.AddedAt)
            .Take(5)
            .Select(piece => (piece, piece.AspectRatio))
            .ToList();
        var recentUrls = _recentGalleryArt.Select(entry => entry.Piece.ImageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _bannerGalleryArt = artPieces
            .Where(piece => string.Equals(piece.Category, "Banners", StringComparison.OrdinalIgnoreCase)
                && !recentUrls.Contains(piece.ImageUrl))
            .OrderBy(_ => Random.Shared.Next())
            .Select(piece => (piece, piece.AspectRatio))
            .ToList();
        _galleryArt = artPieces
            .Where(piece => !recentUrls.Contains(piece.ImageUrl)
                && !string.Equals(piece.Category, "Banners", StringComparison.OrdinalIgnoreCase))
            .OrderBy(_ => Random.Shared.Next())
            .Select(piece => (piece, piece.AspectRatio))
            .ToList();
        _galleryLoaded = true;
        RebuildGallery();
        if (artPieces.Count > 0)
            SetStatus($"Loaded {artPieces.Count} hosted gallery previews.");
    }

    private void ScheduleGalleryRebuild()
    {
        if (!_galleryLoaded)
            return;

        _galleryLayoutTimer.Stop();
        _galleryLayoutTimer.Start();
    }

    private void RebuildGallery()
    {
        var availableWidth = GalleryScrollViewer.ViewportWidth > 0
            ? GalleryScrollViewer.ViewportWidth - 8
            : GalleryScrollViewer.ActualWidth - 8;
        if (availableWidth < 240 || Math.Abs(availableWidth - _galleryViewportWidth) < 16)
            return;

        _galleryViewportWidth = availableWidth;
        GalleryPanel.Children.Clear();
        if (_recentGalleryArt.Count == 0 && _bannerGalleryArt.Count == 0 && _galleryArt.Count == 0)
        {
            GalleryPanel.Children.Add(new TextBlock
            {
                Text = "The hosted gallery is empty or could not be reached.",
                Foreground = new SolidColorBrush(Color.FromRgb(168, 173, 178)),
                Margin = new Thickness(2, 4, 0, 0)
            });
            return;
        }

        AddRecentGalleryArt(availableWidth);
        AddBannerGalleryArt(availableWidth);

        const double targetHeight = 190;
        const double minimumHeight = 145;
        const double maximumHeight = 245;
        const double gap = 16;
        var index = 0;

        while (index < _galleryArt.Count)
        {
            var row = new List<(ArtPiece Piece, double AspectRatio)>();
            var aspectRatioTotal = 0d;
            while (index < _galleryArt.Count)
            {
                var entry = _galleryArt[index++];
                row.Add(entry);
                aspectRatioTotal += entry.AspectRatio;
                if (aspectRatioTotal * targetHeight + gap * (row.Count - 1) >= availableWidth)
                    break;
            }

            var isLastRow = index >= _galleryArt.Count;
            var justifiedHeight = (availableWidth - gap * (row.Count - 1)) / aspectRatioTotal;
            var rowHeight = isLastRow
                ? Math.Min(targetHeight, justifiedHeight)
                : Math.Clamp(justifiedHeight, minimumHeight, maximumHeight);
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            for (var rowIndex = 0; rowIndex < row.Count; rowIndex++)
            {
                var entry = row[rowIndex];
                var previewWidth = rowHeight * entry.AspectRatio;
                rowPanel.Children.Add(BuildArtTile(entry.Piece, previewWidth, rowHeight, rowIndex < row.Count - 1 ? gap : 0));
            }
            GalleryPanel.Children.Add(rowPanel);
        }
    }

    private void AddBannerGalleryArt(double availableWidth)
    {
        if (_bannerGalleryArt.Count == 0)
            return;

        GalleryPanel.Children.Add(BuildGallerySectionLabel("BANNERS"));
        foreach (var entry in _bannerGalleryArt)
        {
            var width = Math.Min(availableWidth * 0.78, 760);
            var height = width / entry.AspectRatio;
            GalleryPanel.Children.Add(BuildArtTile(entry.Piece, width, height, 0, new Thickness(0, 0, 0, 22)));
        }
    }

    private void AddRecentGalleryArt(double availableWidth)
    {
        if (_recentGalleryArt.Count == 0)
            return;

        GalleryPanel.Children.Add(BuildGallerySectionLabel("RECENTLY ADDED"));
        const double gap = 16;
        const double targetHeight = 150;
        var index = 0;
        while (index < _recentGalleryArt.Count)
        {
            var row = new List<(ArtPiece Piece, double AspectRatio)>();
            var aspectRatioTotal = 0d;
            while (index < _recentGalleryArt.Count)
            {
                var entry = _recentGalleryArt[index++];
                row.Add(entry);
                aspectRatioTotal += entry.AspectRatio;
                if (aspectRatioTotal * targetHeight + gap * (row.Count - 1) >= availableWidth)
                    break;
            }

            var height = Math.Min(targetHeight, (availableWidth - gap * (row.Count - 1)) / aspectRatioTotal);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            for (var rowIndex = 0; rowIndex < row.Count; rowIndex++)
            {
                var entry = row[rowIndex];
                panel.Children.Add(BuildArtTile(entry.Piece, height * entry.AspectRatio, height, rowIndex < row.Count - 1 ? gap : 0));
            }
            GalleryPanel.Children.Add(panel);
        }
    }

    private static TextBlock BuildGallerySectionLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 217)),
        FontWeight = FontWeights.Bold,
        FontSize = 11,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private Border BuildArtTile(ArtPiece art, double previewWidth, double previewHeight, double gap, Thickness? margin = null)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        try
        {
            var bitmap = GetGalleryPreview(art.ImageUrl);
            image.Source = bitmap;
        }
        catch
        {
            image.Source = null;
        }

        var panel = new StackPanel();
        image.Width = previewWidth;
        image.Height = previewHeight;
        panel.Children.Add(image);
        panel.Children.Add(new TextBlock { Text = art.Title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = art.Category, Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 217)), FontSize = 11 });
        if (!string.IsNullOrWhiteSpace(art.Artist))
        {
            var artistLink = new Hyperlink(new Run($"Artist: @{art.Artist}"))
            {
                NavigateUri = new Uri($"https://vgen.co/{Uri.EscapeDataString(art.Artist)}")
            };
            artistLink.RequestNavigate += DiscordLink_RequestNavigate;
            var artistText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(115, 215, 255)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            };
            artistText.Inlines.Add(artistLink);
            panel.Children.Add(artistText);
        }

        return new Border
        {
            Width = previewWidth,
            Margin = margin ?? new Thickness(0, 0, gap, 0),
            Child = panel
        };
    }

    private BitmapImage GetGalleryPreview(string imageUrl)
    {
        if (_galleryPreviewCache.TryGetValue(imageUrl, out var cached))
            return cached;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnDemand;
        bitmap.DecodePixelHeight = 540;
        bitmap.UriSource = new Uri(imageUrl);
        bitmap.EndInit();
        _galleryPreviewCache[imageUrl] = bitmap;
        return bitmap;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AudioSource = (AudioSourceComboBox.SelectedItem as AudioInputDevice)?.Name ?? "";
        _settings.Language = SelectedText(LanguageComboBox);
        _settings.Model = SelectedText(ModelComboBox);
        _settings.AutoCopyTranscript = AutoCopyCheckBox.IsChecked == true;
        _settings.SilenceDurationSeconds = SilenceDurationSlider.Value;
        _settings.DisableVrChatOsc = DisableVrChatOscCheckBox.IsChecked == true;
        AppSettingsService.Save(_settings);
        SetStatus("Preferences saved.");
    }

    private void ChangeKeybind_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingKeybind = true;
        _hotkey.Unregister();
        ChangeKeybindButton.Content = "Press a key...";
        CaptureStatusText.Text = "Choosing shortcut";
        SetStatus("Press any non-modifier keyboard key. Press Escape to cancel.");
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingKeybind)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            FinishKeybindCapture();
            RegisterPhraseHotkey();
            SetStatus("Keybind change cancelled.");
            return;
        }
        if (IsModifierKey(key))
        {
            SetStatus("Choose a non-modifier key.");
            return;
        }

        var previousKey = _settings.ActivationKey;
        _settings.ActivationKey = key.ToString();
        try
        {
            FinishKeybindCapture();
            if (!RegisterPhraseHotkey())
                throw new InvalidOperationException($"{key} is already reserved by another app.");
            AppSettingsService.Save(_settings);
            SetStatus($"Phrase shortcut changed to {_settings.ActivationKey}.");
        }
        catch (Exception ex)
        {
            _settings.ActivationKey = previousKey;
            FinishKeybindCapture();
            RegisterPhraseHotkey();
            SetStatus($"Could not use {key}: {ex.Message}");
        }
    }

    private void FinishKeybindCapture()
    {
        _isCapturingKeybind = false;
        ChangeKeybindButton.Content = "Change keybind";
    }

    private bool RegisterPhraseHotkey()
    {
        try
        {
            _hotkey.Register(this, GetActivationKey());
            ActivationKeyText.Text = _settings.ActivationKey;
            CaptureStatusText.Text = "Shortcut ready";
            CaptureHintText.Text = $"Press {_settings.ActivationKey} to record a phrase.";
            return true;
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = "Shortcut unavailable";
            CaptureHintText.Text = $"Could not register {_settings.ActivationKey}.";
            SetStatus(ex.Message);
            return false;
        }
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void SilenceDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SilenceDurationText is not null)
            SilenceDurationText.Text = $"{e.NewValue:0.0} seconds";
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        DownloadModelButton.IsEnabled = false;
        SidebarDownloadModelButton.IsEnabled = false;
        SetStatus("Downloading Whisper base.en model...");
        try
        {
            await _transcriptionService.DownloadModelAsync();
            SetStatus("Whisper base.en model downloaded. Local transcription is ready.");
            RefreshEngineStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not download the Whisper model: {ex.Message}");
        }
        finally
        {
            DownloadModelButton.IsEnabled = true;
            SidebarDownloadModelButton.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showWhenCurrent: true);

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.ReleasesPageUrl);

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate is null || !_latestUpdate.HasPortablePackage)
        {
            SetStatus("Check for updates first. The latest release needs a portable ZIP package.");
            return;
        }

        try
        {
            InstallUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = $"Installing {_latestUpdate.PortablePackageName}...";
            await UpdateService.PrepareAndLaunchPortableUpdateAsync(_latestUpdate);
            UpdateStatusText.Text = "Installing update. Voxie will restart when it is done.";
            SetStatus("Installing update. Voxie will close and restart.");
            await Task.Delay(1000);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            InstallUpdateButton.IsEnabled = _latestUpdate.HasPortablePackage;
            UpdateStatusText.Text = "Update failed.";
            SetStatus($"Could not install the update: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesAsync(bool showWhenCurrent)
    {
        try
        {
            var result = await UpdateService.CheckForUpdatesAsync();
            _latestUpdate = result;
            InstallUpdateButton.IsEnabled = result.UpdateAvailable && result.HasPortablePackage;
            UpdateStatusText.Text = result.UpdateAvailable
                ? result.HasPortablePackage
                    ? $"Version {result.LatestVersion} is available. Install it here without keeping setup files."
                    : $"Version {result.LatestVersion} is available, but no portable ZIP package was found."
                : $"You are on the latest release: {result.CurrentVersion}.";
            if (result.UpdateAvailable || showWhenCurrent)
                SetStatus(UpdateStatusText.Text);
        }
        catch (Exception ex)
        {
            _latestUpdate = null;
            InstallUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "Could not check GitHub Releases. If the repo is private, update checks only work after it is public.";
            if (showWhenCurrent)
                SetStatus($"Could not check GitHub Releases: {ex.Message}");
        }
    }

    private void DiscordLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void LoadSettings()
    {
        _audioDevices = MicrophoneCaptureService.GetDevices();
        AudioSourceComboBox.ItemsSource = _audioDevices;
        AudioSourceComboBox.SelectedItem = _audioDevices.FirstOrDefault(device =>
            string.Equals(device.Name, _settings.AudioSource, StringComparison.OrdinalIgnoreCase))
            ?? _audioDevices.FirstOrDefault();
        SelectByText(LanguageComboBox, _settings.Language);
        SelectByText(ModelComboBox, _settings.Model);
        ActivationKeyText.Text = _settings.ActivationKey;
        SilenceDurationSlider.Value = Math.Clamp(_settings.SilenceDurationSeconds, 1, 12);
        DisableVrChatOscCheckBox.IsChecked = _settings.DisableVrChatOsc;
        CaptureHintText.Text = $"Press {_settings.ActivationKey} to record a phrase.";
        AutoCopyCheckBox.IsChecked = _settings.AutoCopyTranscript;
    }

    private void ShowTranscript_Click(object sender, RoutedEventArgs e) => ShowPage(TranscriptPage, "Transcript workspace", "Capture live phrases with a button or global shortcut.");
    private async void ShowGallery_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(GalleryPage, "Commissioned art", "Keep your emotes, banners, and favorite pieces close.");
        if (!_galleryLoaded)
            await RefreshGalleryAsync();
    }
    private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, "Settings", "Choose how your transcription workspace behaves.");

    private void ShowPage(UIElement page, string title, string subtitle)
    {
        TranscriptPage.Visibility = Visibility.Collapsed;
        GalleryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void SetStatus(string message) => FooterStatusText.Text = message;

    private async Task TranscribeAsync(string filePath)
    {
        _isTranscribing = true;
        StartRecordingButton.IsEnabled = false;
        try
        {
            var transcript = await _transcriptionService.TranscribeFileAsync(filePath);
            TranscriptTextBox.Text = transcript.Length == 0 ? "[No speech detected]" : transcript;
            CaptureStatusText.Text = "Standing by";
            SetStatus("Local transcription complete.");
        }
        catch (Exception ex)
        {
            CaptureStatusText.Text = "Transcription unavailable";
            SetStatus(ex.Message);
        }
        finally
        {
            _isTranscribing = false;
            StartRecordingButton.IsEnabled = true;
        }
    }

    private Key GetActivationKey() =>
        Enum.TryParse<Key>(_settings.ActivationKey, ignoreCase: true, out var key)
            ? key
            : Key.F8;

    private void RefreshEngineStatus()
    {
        ModelDownloadPrompt.Visibility = _transcriptionService.IsConfigured
            ? Visibility.Collapsed
            : Visibility.Visible;
        EngineStatusText.Text = _transcriptionService.IsConfigured
            ? "Local Whisper base.en ready"
            : $"Model needed: %APPDATA%\\Voxie\\Models\\{WhisperTranscriptionService.ModelFileName}";
    }

    private static string SelectedText(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static void SelectByText(ComboBox comboBox, string text)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }
}
