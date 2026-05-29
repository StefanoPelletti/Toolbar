using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Toolbar.Services;
using Toolbar.ViewModels;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Toolbar;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    private enum UpdateUiState { Idle, Checking, UpToDate, UpdateAvailable, Downloading, Error }

    private UpdateUiState _updateState = UpdateUiState.Idle;
    private UpdateInfo? _pendingUpdate;

    private bool _capturingHotkey;
    private string _hotkeyGesture;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        AlwaysOnTopBox.IsChecked = vm.AlwaysOnTop;
        LaunchAtBootBox.IsChecked = vm.LaunchAtBoot;
        VerticalBox.IsChecked = vm.IsVertical;
        ScaleSlider.Value = vm.ScaleSteps;
        UpdateScaleLabel(vm.ScaleSteps);

        HotkeyBox.IsChecked = vm.HotkeyEnabled;
        _hotkeyGesture = vm.HotkeyGesture;
        HotkeyButton.Content = _hotkeyGesture;

        VersionLabel.Text = $"Version {UpdateService.CurrentVersion().ToString(3)}";
    }

    // ── Hotkey rebind capture ─────────────────────────────────────────────────

    private void OnRebindHotkey(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = "Press keys…";
        HotkeyButton.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingHotkey)
        {
            // Esc abandons capture and keeps the previous gesture.
            if (e.Key == Key.Escape)
            {
                EndCapture();
                e.Handled = true;
                return;
            }

            var gesture = BuildGesture(e);
            if (gesture is not null && HotKeyService.IsValid(gesture))
            {
                _hotkeyGesture = gesture;
                EndCapture();
            }
            // Swallow everything while capturing so the focused button doesn't
            // activate on Space/Enter and Esc doesn't close the dialog.
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void EndCapture()
    {
        _capturingHotkey = false;
        HotkeyButton.Content = _hotkeyGesture;
    }

    // Renders the current modifier set + key as "Ctrl+Alt+Space". Returns null
    // until a non-modifier key is pressed alongside at least one modifier.
    private static string? BuildGesture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key)) return null;

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return null;

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System;

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateScaleLabel((int)e.NewValue);

    private void UpdateScaleLabel(int steps)
        => ScaleLabel.Text = $"{100 + steps * 10}%";

    private void OnTitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CommitSettings();
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        base.OnKeyDown(e);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        switch (_updateState)
        {
            case UpdateUiState.Idle:
            case UpdateUiState.UpToDate:
            case UpdateUiState.Error:
                await CheckAsync();
                break;
            case UpdateUiState.UpdateAvailable when _pendingUpdate is not null:
                await DownloadAndApplyAsync(_pendingUpdate);
                break;
        }
    }

    private async Task CheckAsync()
    {
        SetState(UpdateUiState.Checking);
        try
        {
            var info = await UpdateService.CheckLatestAsync();
            if (info is null)
            {
                _pendingUpdate = null;
                SetState(UpdateUiState.UpToDate);
            }
            else
            {
                _pendingUpdate = info;
                SetState(UpdateUiState.UpdateAvailable);
            }
        }
        catch (HttpRequestException)
        {
            SetState(UpdateUiState.Error, "Couldn't reach GitHub. Check your connection.");
        }
        catch (TaskCanceledException)
        {
            SetState(UpdateUiState.Error, "Request timed out. Try again.");
        }
        catch (Exception ex)
        {
            SetState(UpdateUiState.Error, ex.Message);
        }
    }

    private async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        // Commit any pending settings changes synchronously before the swap —
        // the new process will read this config on startup, so a debounced save
        // would race the restart.
        CommitSettings();
        (Owner as MainWindow)?.PersistImmediate();

        SetState(UpdateUiState.Downloading);

        var progress = new Progress<double>(p =>
        {
            UpdateProgress.Value = p;
            UpdateButton.Content = $"Downloading… {(int)(p * 100)}%";
        });

        try
        {
            await UpdateService.DownloadAndApplyAsync(info, progress);

            UpdateStatus.Visibility = Visibility.Visible;
            UpdateStatus.Text = "Restarting…";
            Application.Current.Shutdown();
        }
        catch (UnauthorizedAccessException ex)
        {
            SetState(UpdateUiState.Error, ex.Message);
        }
        catch (System.IO.InvalidDataException ex)
        {
            SetState(UpdateUiState.Error, ex.Message);
        }
        catch (HttpRequestException)
        {
            SetState(UpdateUiState.Error, "Download failed. Check your connection.");
        }
        catch (Exception ex)
        {
            SetState(UpdateUiState.Error, ex.Message);
        }
    }

    private void CommitSettings()
    {
        _vm.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _vm.IsVertical = VerticalBox.IsChecked == true;
        _vm.ScaleSteps = (int)ScaleSlider.Value;
        _vm.HotkeyEnabled = HotkeyBox.IsChecked == true;
        _vm.HotkeyGesture = _hotkeyGesture;
        bool bootEnabled = LaunchAtBootBox.IsChecked == true;
        _vm.LaunchAtBoot = bootEnabled;
        AutoStartService.Apply(bootEnabled);
    }

    private void SetState(UpdateUiState state, string? message = null)
    {
        _updateState = state;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = false;
        UpdateStatus.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = true;

        switch (state)
        {
            case UpdateUiState.Idle:
                UpdateButton.Content = "Check for updates";
                break;

            case UpdateUiState.Checking:
                UpdateButton.Content = "Checking…";
                UpdateButton.IsEnabled = false;
                UpdateProgress.Visibility = Visibility.Visible;
                UpdateProgress.IsIndeterminate = true;
                break;

            case UpdateUiState.UpToDate:
                UpdateButton.Content = "Up to date";
                UpdateButton.IsEnabled = false;
                break;

            case UpdateUiState.UpdateAvailable when _pendingUpdate is not null:
                UpdateButton.Content = $"Update to {_pendingUpdate.Version.ToString(3)}";
                break;

            case UpdateUiState.Downloading:
                UpdateButton.Content = "Downloading…";
                UpdateButton.IsEnabled = false;
                UpdateProgress.Visibility = Visibility.Visible;
                UpdateProgress.Value = 0;
                break;

            case UpdateUiState.Error:
                UpdateButton.Content = "Try again";
                if (!string.IsNullOrEmpty(message))
                {
                    UpdateStatus.Text = message;
                    UpdateStatus.Visibility = Visibility.Visible;
                }
                break;
        }
    }
}
