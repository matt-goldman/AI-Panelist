using Shared;
using UI_Common.Services;

namespace UI_Common.Pages;

public partial class DevicesPage : ContentPage
{
    private readonly DeviceService _deviceService;
    public List<AudioDeviceInfo> Devices { get; set; } = new();

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
            Devices = await _deviceService.GetDevicesAsync();
            OnPropertyChanged(nameof(Devices));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to load devices: {ex.Message}", "OK");
        }
    }

    private async void OnSaveDisplayNameClicked(object? sender, EventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AudioDeviceInfo device)
        {
            // Find the Entry in the same row to get the updated text
            if (button.Parent is HorizontalStackLayout stackLayout)
            {
                var entry = stackLayout.Children.OfType<Entry>().FirstOrDefault();
                if (entry != null)
                {
                    var displayName = entry.Text ?? string.Empty;
                    var success = await _deviceService.RenameDeviceAsync(device.Id, displayName);
                    
                    if (success)
                    {
                        await DisplayAlert("Success", "Device display name updated", "OK");
                        // Reload devices to reflect the updated state
                        await LoadDevicesAsync();
                    }
                    else
                    {
                        await DisplayAlert("Error", "Failed to update device display name", "OK");
                    }
                }
            }
        }
    }
}
