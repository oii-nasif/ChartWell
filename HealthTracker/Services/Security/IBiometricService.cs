namespace HealthTracker.Services.Security;

public enum BiometricResult
{
    Succeeded,
    Failed,
    Cancelled,
    NotAvailable,
    NotEnrolled
}

/// <summary>Device biometric / screen-lock prompt, used as an alternative to the PIN.</summary>
public interface IBiometricService
{
    /// <summary>True when the device has biometrics or a screen lock the user can authenticate with.</summary>
    Task<bool> IsAvailableAsync();

    Task<BiometricResult> AuthenticateAsync(string title, string subtitle);
}
