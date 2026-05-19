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

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        AlwaysOnTopBox.IsChecked = vm.AlwaysOnTop;
        LaunchAtBootBox.IsChecked = vm.LaunchAtBoot;
        VerticalBox.IsChecked = vm.IsVertical;
        ScaleSlider.Value = vm.ScaleSteps;
        UpdateScaleLabel(vm.ScaleSteps);

        VersionLabel.Text = $"Version {UpdateService.CurrentVersion().ToString(3)}";
    }

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
        _vm.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _vm.IsVertical = VerticalBox.IsChecked == true;
        _vm.ScaleSteps = (int)ScaleSlider.Value;

        bool bootEnabled = LaunchAtBootBox.IsChecked == true;
        _vm.LaunchAtBoot = bootEnabled;
        AutoStartService.Apply(bootEnabled);

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
