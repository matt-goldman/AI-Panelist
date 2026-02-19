using System.Collections.ObjectModel;
using System.Windows.Input;
using Shared;
using UI_Common.Services;

namespace ModeratorApp.Pages;

public partial class DevicesPage : ContentPage
{
    private readonly DeviceService _deviceService;
    public ObservableCollection<AudioDeviceInfo> Devices { get; set; } = [];

    public ICommand RenameDeviceCommand => new Command<AudioDeviceInfo>(async device => await SaveDisplayName(device));

    public DevicesPage(DeviceService deviceService)
    {
        InitializeComponent();
        _deviceService = deviceService;
        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadDevicesAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _deviceService.GetDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(device);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to load devices: {ex.Message}", "OK");
        }
    }

    private async Task SaveDisplayName(AudioDeviceInfo device)
    {
        if (string.IsNullOrEmpty(device.DisplayName))
        {
            await DisplayAlertAsync("Not Renamed", "No new device name provided", "OK");
            return;
        }

        var success = await _deviceService.RenameDeviceAsync(device.Id, device.DisplayName);

        if (success)
        {
            await DisplayAlertAsync("Success", "Device display name updated", "OK");
            // Reload devices to reflect the updated state
            await LoadDevicesAsync();
        }
        else
        {
            await DisplayAlertAsync("Error", "Failed to update device display name", "OK");
        }
    }
}
