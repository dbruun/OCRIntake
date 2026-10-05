using System.Net.Http.Headers;
using System.Text.Json;

namespace OCRIntake.Web;

public class ContentUnderstandingService(HttpClient client, IConfiguration configuration)
{
    public string Mode => configuration["ContentUnderstanding:Mode"] ?? "Live";

    public async Task<Dictionary<string, DetectedField>> Extract(byte[] image, string mediaType, CancellationToken cancellation)
    {
        if (Mode == "Sample")
        {
            var sample = LabelSchema.Fields.Keys.ToDictionary(key => key, _ => new DetectedField(null, null));
            sample["ProductName"] = new("DEMO Herbal Tea — synthetic sample", 0.96);
            sample["Brand"] = new("Sample Brand", 0.93);
            sample["LotBatchNumber"] = new("DEMO-001", 0.78);
            sample["PackagingInformation"] = new("Illustrative cardboard box", 0.88);
            sample["OtherLabelInformation"] = new("Synthetic demo values; not extracted from the uploaded image.", null);
            return sample;
        }
        if (Mode != "Live")
            throw new InvalidOperationException("ContentUnderstanding mode must be Live or Sample.");
        var endpointText = configuration["ContentUnderstanding:Endpoint"];
        var key = configuration["ContentUnderstanding:ApiKey"];
        var analyzer = configuration["ContentUnderstanding:AnalyzerId"];
        var version = configuration["ContentUnderstanding:ApiVersion"] ?? "2025-11-01";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(analyzer))
            throw new InvalidOperationException("Configure the Content Understanding HTTPS endpoint, analyzer and API key.");
        var analyzeUrl = new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/contentunderstanding/analyzers/" +
            $"{Uri.EscapeDataString(analyzer)}:analyzeBinary?api-version={Uri.EscapeDataString(version)}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        using var request = new HttpRequestMessage(HttpMethod.Post, analyzeUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new ByteArrayContent(image);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await client.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (!response.Headers.TryGetValues("Operation-Location", out var locations) ||
            !Uri.TryCreate(analyzeUrl, locations.Single(), out var operation) ||
            operation.Scheme != endpoint.Scheme || operation.Host != endpoint.Host || operation.Port != endpoint.Port ||
            !string.IsNullOrEmpty(operation.UserInfo))
            throw new InvalidOperationException("Content Understanding did not return a trusted operation URL.");
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operation);
            poll.Headers.Add("Ocp-Apim-Subscription-Key", key);
            using var result = await client.SendAsync(poll, deadline.Token);
            result.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await result.Content.ReadAsStringAsync(deadline.Token));
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString();
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Content Understanding analysis failed.");
            if (!string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                continue;
            var contents = root.GetProperty("result").GetProperty("contents");
            if (contents.GetArrayLength() == 0)
                throw new InvalidOperationException("Content Understanding returned no content.");
            var fields = contents[0].GetProperty("fields");
            var extracted = new Dictionary<string, DetectedField>();
            foreach (var name in LabelSchema.Fields.Keys)
            {
                if (!fields.TryGetProperty(name, out var field))
                {
                    extracted[name] = new(null, null);
                    continue;
                }
                string? value = field.TryGetProperty("valueString", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() : null;
                double? confidence = field.TryGetProperty("confidence", out var score) &&
                    score.TryGetDouble(out var number) && number is >= 0 and <= 1 ? number : null;
                if (value?.Length > 10000)
                    throw new InvalidOperationException("Extracted field exceeds the review limit.");
                extracted[name] = new(value, confidence);
            }
            return extracted;
        }
    }
}
