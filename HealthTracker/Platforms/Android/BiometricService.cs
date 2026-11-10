using Android.OS;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using HealthTracker.Services.Security;

namespace HealthTracker.Platforms.Android;

/// <summary>
/// AndroidX BiometricPrompt wrapper. Allows fingerprint/face (weak class) and, on Android 11+,
/// the device credential (PIN/pattern/password) as a fallback.
/// </summary>
public class BiometricService : IBiometricService
{
    /// <summary>
    /// A BiometricPrompt whose callback never fires (activity torn down mid-prompt, system UI killed)
    /// would otherwise leave the caller awaiting forever with the lock screen stuck.
    /// </summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    private static int Authenticators =>
        Build.VERSION.SdkInt >= BuildVersionCodes.R
            ? BiometricManager.Authenticators.BiometricWeak | BiometricManager.Authenticators.DeviceCredential
            : BiometricManager.Authenticators.BiometricWeak;

    public Task<bool> IsAvailableAsync()
    {
        var manager = BiometricManager.From(global::Android.App.Application.Context);
        return Task.FromResult(manager.CanAuthenticate(Authenticators) == BiometricManager.BiometricSuccess);
    }

    public Task<BiometricResult> AuthenticateAsync(string title, string subtitle)
    {
        var manager = BiometricManager.From(global::Android.App.Application.Context);
        switch (manager.CanAuthenticate(Authenticators))
        {
            case BiometricManager.BiometricSuccess:
                break;
            case BiometricManager.BiometricErrorNoneEnrolled:
                return Task.FromResult(BiometricResult.NotEnrolled);
            default:
                return Task.FromResult(BiometricResult.NotAvailable);
        }

        if (Platform.CurrentActivity is not FragmentActivity activity)
            return Task.FromResult(BiometricResult.NotAvailable);
        // A prompt attached to a dying activity is never shown and its callback never runs.
        if (activity.IsFinishing || activity.IsDestroyed)
            return Task.FromResult(BiometricResult.NotAvailable);

        var tcs = new TaskCompletionSource<BiometricResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timeout = new CancellationTokenSource(PromptTimeout);
        timeout.Token.Register(() => tcs.TrySetResult(BiometricResult.Cancelled));
        tcs.Task.ContinueWith(_ => timeout.Dispose(), TaskScheduler.Default);

        // BiometricPrompt must be created and shown on the main thread.
        activity.RunOnUiThread(() =>
        {
            try
            {
                if (activity.IsFinishing || activity.IsDestroyed)
                {
                    tcs.TrySetResult(BiometricResult.NotAvailable);
                    return;
                }

                var builder = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle(title)
                    .SetSubtitle(subtitle)
                    .SetConfirmationRequired(false)
                    .SetAllowedAuthenticators(Authenticators);

                // The negative button is only allowed (and required) when device credential is not offered.
                if (Build.VERSION.SdkInt < BuildVersionCodes.R)
                    builder.SetNegativeButtonText("Use PIN");

                var executor = ContextCompat.GetMainExecutor(activity)!;
                var prompt = new BiometricPrompt(activity, executor, new Callback(tcs));
                prompt.Authenticate(builder.Build());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Biometric prompt failed: {ex}");
                tcs.TrySetResult(BiometricResult.NotAvailable);
            }
        });

        return tcs.Task;
    }

    private sealed class Callback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<BiometricResult> _tcs;

        public Callback(TaskCompletionSource<BiometricResult> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(BiometricResult.Succeeded);

        // Called when the system gives up (too many attempts, user cancelled, hardware error).
        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
        {
            var result = errorCode switch
            {
                BiometricPrompt.ErrorUserCanceled or BiometricPrompt.ErrorNegativeButton or BiometricPrompt.ErrorCanceled
                    => BiometricResult.Cancelled,
                BiometricPrompt.ErrorNoBiometrics or BiometricPrompt.ErrorNoDeviceCredential
                    => BiometricResult.NotEnrolled,
                BiometricPrompt.ErrorHwNotPresent or BiometricPrompt.ErrorHwUnavailable
                    => BiometricResult.NotAvailable,
                _ => BiometricResult.Failed
            };
            _tcs.TrySetResult(result);
        }

        // A single non-matching attempt; the prompt stays open so we do not complete the task here.
        public override void OnAuthenticationFailed() { }
    }
}
