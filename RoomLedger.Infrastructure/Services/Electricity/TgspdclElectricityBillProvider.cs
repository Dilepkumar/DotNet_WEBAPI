using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Application.Common.Models;
using RoomLedger.Domain.Entities;
using RoomLedger.Infrastructure.Configuration;

namespace RoomLedger.Infrastructure.Services.Electricity;

/// <summary>
/// Official TGSPDCL Online Bill Enquiry implementation connecting directly to 
/// TGSPDCL's public REST service.
/// </summary>
public class TgspdclElectricityBillProvider : IElectricityBillProvider
{
    private readonly HttpClient _httpClient;
    private readonly TgspdclSettings _settings;
    private readonly ILogger<TgspdclElectricityBillProvider> _logger;

    public TgspdclElectricityBillProvider(HttpClient httpClient, IOptions<TgspdclSettings> options, ILogger<TgspdclElectricityBillProvider> logger)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds > 0 ? _settings.TimeoutSeconds : 15);
    }

    public async Task<NormalizedElectricityBillResult> FetchBillAsync(ElectricityAccount account, CancellationToken cancellationToken = default)
    {
        var result = new NormalizedElectricityBillResult
        {
            BillerId = "TGSPDCL00TEL01",
            BillerName = "Southern Power Distribution Company of Telangana Ltd (TGSPDCL)",
            ConsumerNumber = account.ConsumerNumber
        };

        // 1. Resolve Unique Service Number (USCNO) from parameters or consumer number
        var uscno = ResolveUscno(account);
        result.UniqueServiceNumber = uscno;

        if (string.IsNullOrWhiteSpace(uscno))
        {
            result.Status = "INVALID_CONSUMER";
            result.ErrorMessage = "Unique Service Number (USCNO) is required for TGSPDCL bill enquiry.";
            return result;
        }

        try
        {
            _logger.LogInformation("Calling TGSPDCL Bill Enquiry for USCNO {Uscno}", MaskIdentifier(uscno));

            using var req = new HttpRequestMessage(HttpMethod.Get, $"api/public/bill?uscno={Uri.EscapeDataString(uscno)}");
            req.Headers.Add("X-API-Key", _settings.ApiKey);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var res = await _httpClient.SendAsync(req, cancellationToken);
            var rawJson = await res.Content.ReadAsStringAsync(cancellationToken);
            result.RawResponse = rawJson;

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                result.Status = "INVALID_CONSUMER";
                result.ErrorMessage = "No consumer found for given Unique Service Number. Please check your USCNO.";
                return result;
            }

            if (!res.IsSuccessStatusCode)
            {
                result.Status = "PROVIDER_ERROR";
                result.ErrorCode = $"HTTP_{(int)res.StatusCode}";
                result.ErrorMessage = "TGSPDCL bill enquiry service is temporarily unavailable. Please try again later.";
                return result;
            }

            // 2. Parse JSON Response
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Check for error indicator inside response
            if (root.TryGetProperty("ERROR", out var errProp))
            {
                var errStr = errProp.GetString();
                if (!string.IsNullOrWhiteSpace(errStr) && !errStr.Equals("N", StringComparison.OrdinalIgnoreCase))
                {
                    if (errStr.Contains("NO DATA FOUND", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "INVALID_CONSUMER";
                        result.ErrorCode = "INVALID_CONSUMER";
                        result.ErrorMessage = "No consumer found for given Unique Service Number. Please check the USCNO and try again.";
                    }
                    else
                    {
                        result.Status = "PROVIDER_ERROR";
                        result.ErrorCode = "PROVIDER_ERROR";
                        result.ErrorMessage = $"TGSPDCL provider error: {errStr}";
                    }
                    return result;
                }
            }

            // Check success flag if wrapped in generic response
            if (root.TryGetProperty("success", out var succProp) && !succProp.GetBoolean())
            {
                var msg = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : "No consumer found.";
                result.Status = "INVALID_CONSUMER";
                result.ErrorMessage = msg ?? "No consumer found for given Unique Service Number.";
                return result;
            }

            // Extract Target Element (either root or root.data)
            var dataEl = root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object
                ? dEl
                : root;

            // Map Fields
            var rawCustName = GetStringProp(dataEl, "CUSTOMERNAME", "consName")?.Trim();
            result.CustomerName = rawCustName != null ? System.Text.RegularExpressions.Regex.Replace(rawCustName, @"\s+", " ") : null;
            result.ConsumerNumber = GetStringProp(dataEl, "SERVICENO", "scno") ?? account.ConsumerNumber;
            result.UniqueServiceNumber = GetStringProp(dataEl, "UKSCNO", "consumerNumber") ?? uscno;
            result.ERO = GetStringProp(dataEl, "ERO", "eroName");
            result.District = GetStringProp(dataEl, "circle");

            // Parse Dates
            var billDateStr = GetStringProp(dataEl, "BILLDATE", "billDate");
            if (!string.IsNullOrWhiteSpace(billDateStr) && TryParseTgspdclDate(billDateStr, out var bDate))
            {
                result.BillDate = bDate;
                result.BillPeriod = bDate.ToString("MMM yyyy");
            }

            var dueDateStr = GetStringProp(dataEl, "DUEDATE", "dueDate");
            if (!string.IsNullOrWhiteSpace(dueDateStr) && TryParseTgspdclDate(dueDateStr, out var dDate))
            {
                result.DueDate = dDate;
            }

            // Parse Amounts
            result.BillAmount = ParseDecimalProp(dataEl, "BILLAMT", "billAmount");
            result.ACDAmount = ParseDecimalProp(dataEl, "ACD");
            result.Arrears = ParseDecimalProp(dataEl, "ARRAMT", "totalArrears");
            result.LateFee = ParseDecimalProp(dataEl, "RECONFEE");

            var explicitTot = TryParseDecimalProp(dataEl, "TOTAMT", "AMOUNTPAYABLE", "totalPayable");
            result.TotalAmount = explicitTot.HasValue
                ? explicitTot.Value
                : (result.BillAmount + result.Arrears + result.LateFee + result.ACDAmount);

            result.ProviderReference = $"TGSPDCL-{result.UniqueServiceNumber}-{(result.BillDate?.ToString("yyyyMM") ?? DateTime.UtcNow.ToString("yyyyMM"))}";

            // Determine Status
            if (result.TotalAmount > 0)
            {
                result.Status = "BILL_GENERATED";
                result.BillGenerated = true;
            }
            else
            {
                result.Status = "NO_BILL";
                result.BillGenerated = false;
                result.ErrorMessage = "No active bill pending for the current cycle. All previous bills are settled at TGSPDCL (₹0.00 payable).";
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP exception calling TGSPDCL for USCNO {Uscno}", MaskIdentifier(uscno));
            result.Status = "PROVIDER_ERROR";
            result.ErrorMessage = "Unable to connect to TGSPDCL bill enquiry service. Please try again later.";
            return result;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout calling TGSPDCL for USCNO {Uscno}", MaskIdentifier(uscno));
            result.Status = "PROVIDER_ERROR";
            result.ErrorMessage = "TGSPDCL bill enquiry service request timed out. Please try again.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing TGSPDCL bill for USCNO {Uscno}", MaskIdentifier(uscno));
            result.Status = "PROVIDER_ERROR";
            result.ErrorMessage = "Unable to retrieve the bill at this time.";
            return result;
        }
    }

    private static string? ResolveUscno(ElectricityAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.CustomerParametersJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(account.CustomerParametersJson);
                if (dict != null)
                {
                    if (dict.TryGetValue("UniqueServiceNumber", out var v1) && !string.IsNullOrWhiteSpace(v1)) return v1.Trim();
                    if (dict.TryGetValue("USCNO", out var v2) && !string.IsNullOrWhiteSpace(v2)) return v2.Trim();
                    if (dict.TryGetValue("UKSCNO", out var v3) && !string.IsNullOrWhiteSpace(v3)) return v3.Trim();
                    if (dict.TryGetValue("ConsumerNumber", out var v4) && !string.IsNullOrWhiteSpace(v4)) return v4.Trim();
                }
            }
            catch { }
        }

        return !string.IsNullOrWhiteSpace(account.ConsumerNumber) ? account.ConsumerNumber.Trim() : null;
    }

    private static string? GetStringProp(JsonElement el, params string[] propNames)
    {
        foreach (var name in propNames)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    private static decimal ParseDecimalProp(JsonElement el, params string[] propNames)
    {
        foreach (var name in propNames)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var decVal))
                {
                    return decVal;
                }
                if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var strVal))
                {
                    return strVal;
                }
            }
        }
        return 0m;
    }

    private static decimal? TryParseDecimalProp(JsonElement el, params string[] propNames)
    {
        foreach (var name in propNames)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var decVal))
                {
                    return decVal;
                }
                if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var strVal))
                {
                    return strVal;
                }
            }
        }
        return null;
    }

    private static bool TryParseTgspdclDate(string dateStr, out DateTime parsed)
    {
        var formats = new[] { "dd-MMM-yy", "dd-MMM-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" };
        return DateTime.TryParseExact(dateStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
               || DateTime.TryParse(dateStr.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private static string MaskIdentifier(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length <= 4) return "****";
        return id[..2] + new string('*', id.Length - 4) + id[^2..];
    }
}
