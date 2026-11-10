using System.Text.RegularExpressions;
using HealthTracker.Services.Ai;
using Plugin.Maui.OCR;

namespace HealthTracker.Services;

public class ParsedMedicine
{
    public string Name { get; set; } = string.Empty;
    public string? Dosage { get; set; }
    /// <summary>Human-readable frequency as written/derived, e.g. "1-0-1", "BD", "twice daily".</summary>
    public string? Frequency { get; set; }
    /// <summary>Form ("Tablet", "Syrup", ...) plus any timing notes, e.g. "Tablet; after food".</summary>
    public string? Instructions { get; set; }

    // Richer fields (populated by the AI path; the regex path fills what it can).
    public string? Form { get; set; }
    public int? FrequencyPerDay { get; set; }
    /// <summary>Suggested times, "HH:mm".</summary>
    public List<string> Times { get; set; } = [];
    public int? DurationDays { get; set; }
}

/// <summary>
/// OCR + medicine extraction for the "Scan prescription" flow.
///
/// OCR: <see cref="IOcrService.InitAsync"/> is called once, lazily and thread-safely, before the
/// first <c>RecognizeTextAsync</c>; recognition runs with <c>tryHard: true</c>.
///
/// Parsing: <see cref="ParseMedicinesAsync"/> uses Gemini (<see cref="PrescriptionAiParser"/>) when
/// an API key is stored and falls back to the regex heuristics in <see cref="ParseMedicines"/>.
/// </summary>
public partial class PrescriptionParserService
{
    private readonly IOcrService _ocrService;
    private readonly PrescriptionAiParser? _aiParser;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _ocrInitialised;

    public PrescriptionParserService(IOcrService ocrService, PrescriptionAiParser? aiParser = null)
    {
        _ocrService = ocrService;
        _aiParser = aiParser;
    }

    // ----- OCR ---------------------------------------------------------------------------------

    public async Task EnsureOcrInitialisedAsync(CancellationToken ct = default)
    {
        if (_ocrInitialised) return;
        await _initGate.WaitAsync(ct);
        try
        {
            if (_ocrInitialised) return;
            await _ocrService.InitAsync(ct);
            _ocrInitialised = true;
        }
        finally { _initGate.Release(); }
    }

    public async Task<string> ExtractTextFromImage(byte[] imageData)
        => await ExtractTextFromImageAsync(imageData);

    public async Task<string> ExtractTextFromImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        if (imageData == null || imageData.Length == 0) return string.Empty;
        await EnsureOcrInitialisedAsync(ct);
        var result = await _ocrService.RecognizeTextAsync(imageData, tryHard: true, ct);
        return result.Success ? (result.AllText ?? string.Empty) : string.Empty;
    }

    // ----- Parsing: AI first, regex fallback ----------------------------------------------------

    /// <summary>
    /// Parses medicines using Gemini when configured (sending the image when given), otherwise the regex
    /// heuristics. Returns the raw <see cref="GeminiResult{T}"/> too so the UI can explain AI failures.
    /// </summary>
    public async Task<(List<ParsedMedicine> Medicines, GeminiResult<AiPrescriptionExtraction>? AiResult)> ParseMedicinesWithAiAsync(
        string ocrText, byte[]? imageData = null, CancellationToken ct = default)
    {
        GeminiResult<AiPrescriptionExtraction>? aiResult = null;
        if (_aiParser != null && await GeminiApiKeyStore.HasKeyAsync())
        {
            var image = imageData is { Length: > 0 } ? GeminiImage.FromBytes(imageData) : null;
            aiResult = await _aiParser.ParseAsync(ocrText, image, ct);
            if (aiResult.Succeeded && aiResult.Value != null && aiResult.Value.Medicines.Count > 0)
                return (aiResult.Value.Medicines.Select(FromAi).ToList(), aiResult);
        }
        return (ParseMedicines(ocrText), aiResult);
    }

    /// <summary>Convenience overload of <see cref="ParseMedicinesWithAiAsync"/> that drops the AI diagnostics.</summary>
    public async Task<List<ParsedMedicine>> ParseMedicinesAsync(string ocrText, byte[]? imageData = null, CancellationToken ct = default)
        => (await ParseMedicinesWithAiAsync(ocrText, imageData, ct)).Medicines;

    public static ParsedMedicine FromAi(AiPrescribedMedicine m)
    {
        string? frequency = m.FrequencyPerDay switch
        {
            1 => "Once daily",
            2 => "Twice daily",
            3 => "Three times daily",
            4 => "Four times daily",
            { } n => $"{n} times daily",
            null => null,
        };
        var instructions = string.Join("; ", new[] { m.Form, m.Instructions }.Where(s => !string.IsNullOrEmpty(s)));
        return new ParsedMedicine
        {
            Name = m.Name,
            Dosage = m.Dosage,
            Frequency = frequency,
            Instructions = instructions.Length > 0 ? instructions : null,
            Form = m.Form,
            FrequencyPerDay = m.FrequencyPerDay,
            Times = m.Times,
            DurationDays = m.DurationDays,
        };
    }

    // ----- Regex heuristics ----------------------------------------------------------------------

    public List<ParsedMedicine> ParseMedicines(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return [];

        var medicines = new List<ParsedMedicine>();
        var lines = ocrText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 3) continue;

            var parsed = TryParseMedicineLine(line);
            if (parsed != null && parsed.Name != "Unknown")
                medicines.Add(parsed);
        }

        return medicines;
    }

    private static ParsedMedicine? TryParseMedicineLine(string line)
    {
        // Strip a leading list marker ("1.", "2)", "-", "•") so "1. Tab. Metformin" still hits the prefix rule.
        var numbered = NumberedPattern().Match(line);
        var wasNumbered = numbered.Success;
        if (wasNumbered) line = line[numbered.Length..].Trim();

        // "Tab.", "Cap.", "Syp.", "Inj.", "Tablet", "Capsule", "Rx" at the start of the line
        var prefixMatch = PrefixPattern().Match(line);
        if (prefixMatch.Success)
        {
            var remainder = line[prefixMatch.Length..].Trim();
            return ParseRemainderAsMedicine(remainder, prefixMatch.Groups["form"].Value);
        }

        // Numbered line without a form prefix: "1. Metformin 500mg 1-0-1"
        if (wasNumbered) return ParseRemainderAsMedicine(line, null);

        // A form keyword anywhere ("Metformin 500 mg tablet"), or a dose + frequency pattern on the same line
        if (DrugFormKeyword().IsMatch(line) ||
            (DosagePattern().IsMatch(line) && (FrequencyPattern().IsMatch(line) || DoseGridPattern().IsMatch(line))))
        {
            return ParseRemainderAsMedicine(line, null);
        }

        return null;
    }

    private static ParsedMedicine ParseRemainderAsMedicine(string text, string? prefix)
    {
        var medicine = new ParsedMedicine();
        var notes = new List<string>();

        // Duration: "x 7 days", "for 5 days", "for 2 weeks", "1 month"
        var durationMatch = DurationPattern().Match(text);
        if (durationMatch.Success)
        {
            var n = int.Parse(durationMatch.Groups["n"].Value);
            var unit = durationMatch.Groups["unit"].Value.ToLowerInvariant();
            medicine.DurationDays = unit.StartsWith('w') ? n * 7 : unit.StartsWith('m') ? n * 30 : n;
            text = RemoveSpan(text, durationMatch);
        }

        // Dose grid: "1-0-1", "1+0+1", "1-1-1-1", "½-0-½"
        string? frequency = null;
        var gridMatch = DoseGridPattern().Match(text);
        if (gridMatch.Success)
        {
            frequency = gridMatch.Value.Trim();
            var slots = Regex.Split(gridMatch.Value, @"[\-\+]")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            var perDay = slots.Count(s => s != "0");
            medicine.FrequencyPerDay = perDay > 0 ? perDay : null;
            medicine.Times = TimesFromGrid(slots);
            text = RemoveSpan(text, gridMatch);
        }

        // Word/abbreviation frequency: "twice daily", "BD", "TDS", "3 times a day", "HS", "SOS"
        var freqMatch = FrequencyPattern().Match(text);
        if (freqMatch.Success)
        {
            var token = freqMatch.Value.Trim();
            frequency ??= token;
            medicine.FrequencyPerDay ??= FrequencyPerDayFromToken(token);
            if (medicine.Times.Count == 0 && medicine.FrequencyPerDay is { } fpd)
                medicine.Times = DefaultTimes(fpd, token);
            if (SosPattern().IsMatch(token))
            {
                // "SOS for fever" / "PRN if pain": the short tail after the token is the reason, not the name.
                var tail = text[(freqMatch.Index + freqMatch.Length)..].Trim();
                if (tail.Length is > 0 and <= 40 && !tail.Any(char.IsDigit))
                {
                    notes.Add($"As needed ({tail.Trim('-', ',', '.', ':', ';', ' ')})");
                    text = text[..freqMatch.Index];
                }
                else notes.Add("As needed");
            }
            text = RemoveSpan(text, freqMatch);
        }

        // Food/timing instructions: "after food", "before meals", "AC", "PC", "at bedtime"
        var foodMatch = FoodTimingPattern().Match(text);
        if (foodMatch.Success)
        {
            notes.Add(NormaliseFoodTiming(foodMatch.Value));
            text = RemoveSpan(text, foodMatch);
        }

        // Dosage: "500mg", "250 mg", "10ml", "5 mg/5 ml", "1000 IU"
        // A strength plus a per-dose quantity ("100mg/5ml ... 5 ml") both belong in Dosage.
        string? dosage = null;
        for (var dosageMatch = DosagePattern().Match(text); dosageMatch.Success; dosageMatch = DosagePattern().Match(text))
        {
            var token = Regex.Replace(dosageMatch.Value.Trim(), @"\s+", " ");
            dosage = dosage == null ? token : $"{dosage}, {token}";
            text = RemoveSpan(text, dosageMatch);
        }

        // Form keyword embedded in the line ("... tablet") when there was no prefix
        var form = prefix != null ? NormaliseForm(prefix) : null;
        var formInline = DrugFormKeyword().Match(text);
        if (formInline.Success)
        {
            form ??= NormaliseForm(formInline.Value);
            text = RemoveSpan(text, formInline);
        }

        // Whatever is left is the name; collapse whitespace and strip trailing punctuation.
        var name = Regex.Replace(text, @"\s{2,}", " ").Trim().Trim('-', ',', '–', '.', ':', ';').Trim();
        if (string.IsNullOrWhiteSpace(name) || !name.Any(char.IsLetter))
            return new ParsedMedicine { Name = "Unknown" };

        medicine.Name = name;
        medicine.Dosage = dosage;
        medicine.Frequency = frequency;
        medicine.Form = form;

        var instructionParts = new List<string>();
        if (form != null) instructionParts.Add(form);
        instructionParts.AddRange(notes);
        medicine.Instructions = instructionParts.Count > 0 ? string.Join("; ", instructionParts) : null;

        return medicine;
    }

    private static string RemoveSpan(string text, Match m)
    {
        if (m.Index >= text.Length) return text.Trim();
        var end = Math.Min(text.Length, m.Index + m.Length);
        return (text[..m.Index] + " " + text[end..]).Trim();
    }

    /// <summary>Maps a prefix/keyword to a display form; "Rx" is a list marker, not a form, so it yields null.</summary>
    private static string? NormaliseForm(string raw) => raw.Trim().TrimEnd('.', ' ').ToUpperInvariant() switch
    {
        "TAB" or "TABS" or "TABLET" or "TABLETS" or "T" => "Tablet",
        "CAP" or "CAPS" or "CAPSULE" or "CAPSULES" or "C" => "Capsule",
        "SYP" or "SYR" or "SYRUP" or "SUSP" or "SUSPENSION" => "Syrup",
        "INJ" or "INJECTION" => "Injection",
        "DROP" or "DROPS" or "E/D" or "EYE DROPS" => "Drops",
        "OINT" or "OINTMENT" or "CREAM" or "GEL" => "Cream",
        "INH" or "INHALER" or "PUFF" => "Inhaler",
        "RX" => null,
        var other => char.ToUpperInvariant(other[0]) + other[1..].ToLowerInvariant(),
    };

    /// <summary>Maps OD/BD/TDS/QID/HS/"twice daily"/"3 times a day" to a count; null for SOS/PRN/unknown.</summary>
    private static int? FrequencyPerDayFromToken(string token)
    {
        var t = token.ToLowerInvariant();
        if (SosPattern().IsMatch(t)) return null;
        var every = Regex.Match(t, @"(?:every|q)\s*(\d+)\s*h");
        if (every.Success && int.TryParse(every.Groups[1].Value, out var hours) && hours is > 0 and <= 24)
            return Math.Max(1, 24 / hours);
        if (Regex.IsMatch(t, @"\b(?:od|once|one|qd|daily|hs|nocte)\b")) return 1;
        if (Regex.IsMatch(t, @"\b(?:bd|bid|twice|two)\b")) return 2;
        if (Regex.IsMatch(t, @"\b(?:tds|tid|thrice|three)\b")) return 3;
        if (Regex.IsMatch(t, @"\b(?:qid|qds|four)\b")) return 4;
        var n = Regex.Match(t, @"(\d+)\s*(?:time|x)");
        if (n.Success && int.TryParse(n.Groups[1].Value, out var count) && count is > 0 and <= 6) return count;
        return null;
    }

    /// <summary>Default clock times for a frequency; HS/night tokens map to 21:00.</summary>
    private static List<string> DefaultTimes(int perDay, string token)
    {
        var lower = token.ToLowerInvariant();
        if (perDay == 1 && Regex.IsMatch(lower, @"\b(?:hs|nocte|night|bedtime)\b")) return ["21:00"];
        return perDay switch
        {
            1 => ["08:00"],
            2 => ["08:00", "20:00"],
            3 => ["08:00", "14:00", "20:00"],
            4 => ["08:00", "12:00", "16:00", "20:00"],
            _ => [],
        };
    }

    /// <summary>"1-0-1" → morning + night; "1-1-1" → morning, noon, night; 4 slots adds an afternoon dose.</summary>
    private static List<string> TimesFromGrid(List<string> slots)
    {
        string[] three = ["08:00", "13:00", "20:00"];
        string[] four = ["08:00", "12:00", "16:00", "20:00"];
        var map = slots.Count == 4 ? four : three;
        var times = new List<string>();
        for (var i = 0; i < slots.Count && i < map.Length; i++)
            if (slots[i] != "0") times.Add(map[i]);
        return times;
    }

    private static string NormaliseFoodTiming(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        if (t is "ac" || t.Contains("before")) return "Before food";
        if (t is "pc" || t.Contains("after")) return "After food";
        if (t.Contains("with")) return "With food";
        if (t.Contains("empty")) return "Empty stomach";
        if (t.Contains("bed") || t.Contains("night")) return "At bedtime";
        return raw.Trim();
    }

    // Leading list marker: "1.", "2)", "3-", "-", "•", "*"
    [GeneratedRegex(@"^(?:\d{1,2}[\.\)\-:]|[-•*])\s*")]
    private static partial Regex NumberedPattern();

    // Form prefix at line start, with or without a trailing dot: "Tab.", "Cap", "Syp.", "Inj.", "Tablet", "Rx"
    [GeneratedRegex(@"^(?<form>Tabs?\.?|Tablets?|Caps?\.?|Capsules?|Syp\.?|Syr\.?|Syrup|Susp\.?|Inj\.?|Injection|Drops?\.?|E/D|Oint\.?|Ointment|Cream|Gel|Inh\.?|Inhaler|Rx\.?)(?=\s|$)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"\b(?:tablets?|tabs?|capsules?|caps?|syrup|suspension|injection|drops|inhaler|ointment|cream|gel|patch|sachet)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DrugFormKeyword();

    // Dose grid: 1-0-1, 1+0+1, 1-1-1-1, ½-0-½, 1/2-0-1/2, 0-0-1
    [GeneratedRegex(@"(?<![\d/])(?:[01½]|1/2|2)(?:\s*[\-\+]\s*(?:[01½]|1/2|2)){2,3}(?![\d/])")]
    private static partial Regex DoseGridPattern();

    // Word/abbreviation frequency tokens
    [GeneratedRegex(@"\b(?:(?:once|twice|thrice|(?:one|two|three|four|\d+)\s*times?)(?:\s*(?:a|per|/)?\s*(?:day|daily))?|\d+\s*x\s*(?:a|per|/)?\s*(?:day|daily)|(?:every|q)\s*\d+\s*(?:h|hrs?|hours?)|daily|od|bd|bid|tds|tid|qid|qds|hs|nocte|sos|prn|stat)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex FrequencyPattern();

    [GeneratedRegex(@"\b(?:sos|prn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SosPattern();

    // "x 7 days", "× 5 days", "for 10 days", "for 2 weeks", "1 month"
    // Second alternative: a bare "x 14" at the end of a line, which prescriptions use to mean days.
    [GeneratedRegex(@"(?:\b(?:x|×|for)\s*)?(?<n>\d{1,3})\s*(?<unit>days?|d|weeks?|wks?|months?|mo)\b\.?|(?:\bx|×)\s*(?<n>\d{1,3})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    // "after food", "before meals", "with food", "empty stomach", "at bedtime", "AC", "PC"
    [GeneratedRegex(@"\b(?:(?:after|before|with)\s+(?:food|meals?|breakfast|dinner|lunch)|empty\s+stomach|at\s+(?:bed\s*time|night)|a\.?c\.?|p\.?c\.?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FoodTimingPattern();

    // "500mg", "250 mg", "10 ml", "5mg/5ml", "1000 IU", "0.5 g", "100 mcg"
    [GeneratedRegex(@"\b\d+(?:[\.,]\d+)?\s*(?:mg|mcg|µg|g|gm|ml|iu|units?|%)(?:\s*/\s*\d+(?:[\.,]\d+)?\s*(?:mg|mcg|ml|g))?(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DosagePattern();
}
