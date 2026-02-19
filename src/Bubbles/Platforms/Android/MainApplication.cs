using Android.App;
using Android.Runtime;

namespace Bubbles;

// Designed to work offline in local network environments, so we need to allow cleartext traffic for the API calls to work
[Application(UsesCleartextTraffic = true)]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
