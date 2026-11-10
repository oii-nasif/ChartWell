using System.Globalization;
using HealthTracker.Constants;

namespace HealthTracker.Helpers;

public static class ValidationHelper
{
    public static bool IsMetricValueValid(string metricType, double value, double? secondValue, out string error)
    {
        error = string.Empty;

        if (!IsFinite(value) || (secondValue.HasValue && !IsFinite(secondValue.Value)))
        {
            error = "Enter a valid numeric value.";
            return false;
        }

        if (metricType == MetricTypes.BloodPressure)
        {
            var (sysMin, sysMax, diaMin, diaMax) = MetricTypes.GetBloodPressureRange();
            if (value < sysMin || value > sysMax)
            {
                error = $"Systolic BP must be between {sysMin} and {sysMax} mmHg.";
                return false;
            }
            if (secondValue == null)
            {
                error = "Diastolic BP value is required.";
                return false;
            }
            if (secondValue < diaMin || secondValue > diaMax)
            {
                error = $"Diastolic BP must be between {diaMin} and {diaMax} mmHg.";
                return false;
            }
            return true;
        }

        var (min, max) = MetricTypes.GetRange(metricType);
        if (value < min || value > max)
        {
            error = $"{metricType} value must be between {min} and {max} {MetricTypes.GetUnit(metricType)}.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses user-typed numbers regardless of device locale: accepts both "72.5" and "72,5",
    /// and rejects NaN/Infinity.
    /// </summary>
    public static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        return IsFinite(value);
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    public static bool IsValidFileType(string extension, out string error)
    {
        error = string.Empty;
        var ext = extension.ToLowerInvariant().TrimStart('.');
        if (ext is "jpg" or "jpeg" or "png" or "pdf")
            return true;
        error = "Unsupported file type. Only JPG, PNG and PDF are accepted.";
        return false;
    }

    public static bool IsValidPin(string pin, out string error)
    {
        error = string.Empty;
        if (pin.Length < AppConstants.PinMinLength || pin.Length > AppConstants.PinMaxLength)
        {
            error = $"PIN must be {AppConstants.PinMinLength}–{AppConstants.PinMaxLength} digits.";
            return false;
        }
        if (!pin.All(char.IsDigit))
        {
            error = "PIN must contain digits only.";
            return false;
        }
        return true;
    }
}
