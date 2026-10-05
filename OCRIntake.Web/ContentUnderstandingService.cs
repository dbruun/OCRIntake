using System.Net.Http.Headers;
using System.Text.Json;

namespace OCRIntake.Web;

public class ContentUnderstandingService(HttpClient client, IConfiguration configuration)
{
    private static readonly SemaphoreSlim ProvisionGate = new(1, 1);
    private static readonly HashSet<string> Provisioned = [];
    public string Mode => configuration["ContentUnderstanding:Mode"] ?? "Live";

    public async Task<Dictionary<string, DetectedField>> Extract(byte[] image, string mediaType,
        ExtractionProfile profile, CancellationToken cancellation)
    {
        if (Mode == "Sample")
        {
            var sample = profile.Fields.ToDictionary(field => field.Key, field =>
                new DetectedField($"DEMO: {field.Name} (synthetic; not read from image)", null));
            if (sample.ContainsKey("ProductName")) sample["ProductName"] = new("DEMO Herbal Tea — synthetic sample", 0.96);
            if (sample.ContainsKey("Brand")) sample["Brand"] = new("Sample Brand", 0.93);
            if (sample.ContainsKey("LotBatchNumber")) sample["LotBatchNumber"] = new("DEMO-001", 0.78);
            if (sample.ContainsKey("PackagingInformation")) sample["PackagingInformation"] = new("Illustrative cardboard box", 0.88);
            foreach (var field in new[] { "Manufacturer", "Distributor", "ExpirationDate", "ProductSize",
                         "WeightVolume", "RegulatoryStatements", "WarningLabels", "QrCodes" })
                if (sample.ContainsKey(field)) sample[field] = new(null, null);
            return sample;
        }
        if (Mode != "Live")
            throw new InvalidOperationException("ContentUnderstanding mode must be Live or Sample.");
        var endpointText = configuration["ContentUnderstanding:Endpoint"];
        var key = configuration["ContentUnderstanding:ApiKey"];
        var version = configuration["ContentUnderstanding:ApiVersion"] ?? "2025-11-01";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure the Content Understanding HTTPS endpoint and API key.");
        var analyzerUrl = $"{endpoint.AbsoluteUri.TrimEnd('/')}/contentunderstanding/analyzers/{profile.AnalyzerId}";
        await EnsureAnalyzer(new Uri($"{analyzerUrl}?api-version={Uri.EscapeDataString(version)}"),
            endpoint, key, profile, cancellation);
        var analyzeUrl = new Uri($"{analyzerUrl}:analyzeBinary?api-version={Uri.EscapeDataString(version)}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        using var request = new HttpRequestMessage(HttpMethod.Post, analyzeUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new ByteArrayContent(image);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await client.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var document = await Poll(response, analyzeUrl, endpoint, key, deadline.Token);
        var contents = document.RootElement.GetProperty("result").GetProperty("contents");
        if (contents.GetArrayLength() == 0)
            throw new InvalidOperationException("Content Understanding returned no content.");
        var fields = contents[0].GetProperty("fields");
        var extracted = new Dictionary<string, DetectedField>();
        foreach (var definition in profile.Fields)
        {
            var name = definition.Key;
            if (!fields.TryGetProperty(name, out var field))
            {
                extracted[name] = new(null, null);
                continue;
            }
            string? value = field.TryGetProperty("valueString", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() : null;
            double? confidence = field.TryGetProperty("confidence", out var score) && score.ValueKind == JsonValueKind.Number &&
                score.TryGetDouble(out var number) && number is >= 0 and <= 1 ? number : null;
            if (value?.Length > 10000)
                throw new InvalidOperationException("Extracted field exceeds the review limit.");
            extracted[name] = new(value, confidence);
        }
        return extracted;
    }

    private async Task EnsureAnalyzer(Uri url, Uri endpoint, string key, ExtractionProfile profile, CancellationToken cancellation)
    {
        await ProvisionGate.WaitAsync(cancellation);
        try
        {
            if (Provisioned.Contains(url.AbsoluteUri)) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(110));
            using var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            request.Content = JsonContent.Create(new
            {
                description = "Extract only visible product label information. Never infer missing values. Preserve printed wording and units.",
                baseAnalyzerId = "prebuilt-document",
                config = new { enableOcr = true, returnDetails = true, estimateFieldSourceAndConfidence = true },
                fieldSchema = new
                {
                    fields = profile.Fields.ToDictionary(field => field.Key, field => new
                    {
                        type = "string", method = "extract", estimateSourceAndConfidence = true,
                        description = field.Description
                    })
                }
            });
            using var response = await client.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var operation = await Poll(response, url, endpoint, key, deadline.Token);
            Provisioned.Add(url.AbsoluteUri);
        }
        finally { ProvisionGate.Release(); }
    }

    private async Task<JsonDocument> Poll(HttpResponseMessage response, Uri requestUrl, Uri endpoint,
        string key, CancellationToken cancellation)
    {
        if (!response.Headers.TryGetValues("Operation-Location", out var locations) ||
            !Uri.TryCreate(requestUrl, locations.Single(), out var operation) ||
            operation.Scheme != endpoint.Scheme || operation.Host != endpoint.Host || operation.Port != endpoint.Port ||
            !string.IsNullOrEmpty(operation.UserInfo))
            throw new InvalidOperationException("Content Understanding did not return a trusted operation URL.");
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operation);
            poll.Headers.Add("Ocp-Apim-Subscription-Key", key);
            using var result = await client.SendAsync(poll, cancellation);
            result.EnsureSuccessStatusCode();
            var document = JsonDocument.Parse(await result.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var statusProperty) ||
                statusProperty.ValueKind != JsonValueKind.String)
            {
                document.Dispose();
                throw new InvalidOperationException("Content Understanding returned no operation status.");
            }
            var status = statusProperty.GetString();
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return document;
            document.Dispose();
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Content Understanding operation failed.");
            if (!string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "NotStarted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected Content Understanding operation status.");
        }
    }
}
