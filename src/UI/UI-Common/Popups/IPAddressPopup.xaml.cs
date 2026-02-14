using CommunityToolkit.Maui.Views;

namespace UI_Common.Popups;

public partial class IPAddressPopup : Popup<string>
{
	public IPAddressPopup()
	{
		InitializeComponent();
	}

    private async void OkButton_Clicked(object sender, EventArgs e)
    {
        var ipAddress = IPAddressEntry.Text;

        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            // Handle empty IP address case
            ErrorLabel.Text = "Please enter a valid IP address.";
            ErrorLabel.IsVisible = true;
            return;
        }

        // validate IP address format (basic validation)
        if (!Uri.IsWellFormedUriString($"http://{ipAddress}", UriKind.Absolute))
        {
            // Handle invalid IP address case
            ErrorLabel.Text = "Invalid IP address format. Please enter a valid IP address.";
            ErrorLabel.IsVisible = true;
            return;
        }

        // validate octets are between 0 and 255
        var octets = ipAddress.Split('.');
        if (octets.Length != 4 || octets.Any(o => !int.TryParse(o, out int octetValue) || octetValue < 0 || octetValue > 255))
        {
            // Handle invalid IP address case
            ErrorLabel.Text = "Invalid IP address format. Each octet must be between 0 and 255.";
            ErrorLabel.IsVisible = true;
            return;
        }

        // Handle valid IP address case
        await CloseAsync(ipAddress);
    }
}