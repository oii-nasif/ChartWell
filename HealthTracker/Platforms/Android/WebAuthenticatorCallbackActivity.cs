using Android.App;
using Android.Content;
using Android.Content.PM;
using HealthTracker.Constants;

namespace HealthTracker;

/// <summary>
/// Receives the OAuth redirect (CloudConstants.RedirectUri) after Google sign-in in the system
/// browser / Custom Tab and hands it back to MAUI's WebAuthenticator, which completes the pending
/// AuthenticateAsync call in GoogleAuthService. The DataScheme must match the reversed client id;
/// see the setup notes on CloudConstants.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = CloudConstants.RedirectScheme)]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
