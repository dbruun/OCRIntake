# OCRIntake

A local product-intake demo with an **ASP.NET Core / .NET 10 backend** and a plain browser UI. No Python or external NuGet packages are required.

## Run the demo

Install the .NET 10 SDK, then from the repository root:

```sh
dotnet build OCRIntake.slnx
dotnet run --project OCRIntake.Web --launch-profile http
```

Open `http://localhost:5184`. The default **Sample** mode returns clearly marked synthetic values; it does **not** inspect your image or contact Azure.

1. Upload one JPEG or PNG product-label image (maximum 10 MB).
2. Compare the image with all detected fields and their original confidence scores. Edit incorrect values or fill missing ones.
3. Select product category and jurisdiction. COA checkboxes record the inspector's assessment, not an AI determination of applicability.
4. Save corrections and re-screen. Review the issues, explanations and regulation links.
5. Enter the inspector's name, explicitly check the approval box, and approve. Only this action writes a durable record. Download the approved JSON if needed.

The workflow is upload → Content Understanding analysis → human review → deterministic compliance screening → explicit approval. It deliberately does not give an autonomous agent permission to approve or save on an inspector's behalf.

## Microsoft Foundry Content Understanding (live images)

Use a Microsoft Foundry resource with Content Understanding enabled and the required model deployments/default mappings configured. See Microsoft's [setup and REST quickstart](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/quickstart/use-rest-api) and [custom analyzer guide](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/tutorial/create-custom-analyzer). This application uses the stable `2025-11-01` REST API, not Azure Document Intelligence.

Create the custom `product-label` analyzer once, using `OCRIntake.Web/product-label-analyzer.json`. For example, from the repository root with environment variables set privately:

```sh
curl --fail-with-body -i -X PUT \
  "$ContentUnderstanding__Endpoint/contentunderstanding/analyzers/product-label?api-version=2025-11-01" \
  -H "Ocp-Apim-Subscription-Key: $ContentUnderstanding__ApiKey" \
  -H "Content-Type: application/json" \
  --data-binary @OCRIntake.Web/product-label-analyzer.json
```

Poll the returned `Operation-Location` with an authenticated GET until creation succeeds. The analyzer uses `prebuilt-document`, which accepts label images and supports OCR-backed field confidence. All fields are strings to preserve label wording, units and ambiguous dates. Missing confidence is displayed as unavailable. QR extraction is best-effort: verify decoded contents; unreadable codes must not be treated as decoded URLs.

Configure live mode through environment variables or .NET user secrets (never commit a key):

```sh
export ContentUnderstanding__Mode=Live
export ContentUnderstanding__Endpoint="https://YOUR-RESOURCE.services.ai.azure.com"
export ContentUnderstanding__AnalyzerId=product-label
# Set ContentUnderstanding__ApiKey securely in your shell/secret manager.
dotnet run --project OCRIntake.Web --launch-profile http
```

The backend sends raw image bytes to `:analyzeBinary`, follows the service's same-origin `Operation-Location`, and polls with a bounded timeout. Keys never reach the browser. Redirects are disabled to avoid forwarding credentials. Azure failures do not produce an approved record or silently fall back to sample data. Custom analyzers must use the field names and string types in the supplied schema.

## Compliance rules

`Compliance:Rules` in `OCRIntake.Web/appsettings.json` is a configurable, deterministic checklist, not an AI legal opinion. The supplied **Food** rules cover presence checks for identity, net quantity, and manufacturer/distributor information with references to 21 CFR 101.3, 101.105 and 101.5. They do not verify formatting, accuracy, placement, exemptions or complete legal compliance. **Other** has no default rules.

The agency must supply and validate rules for its regulated products, applicable states, warning wording, disclosures, restrictions and COA obligations. Do not invent jurisdiction-specific rules. Supported rule kinds:

| Kind | Configuration | Potential issue |
| --- | --- | --- |
| `required` | `Field` | Captured value is missing |
| `requiredAny` | `Fields` (array of field names) | All alternatives are missing |
| `missingText` | `Field`, `Text` | Required text is absent (case-insensitive substring) |
| `prohibitedText` | `Field`, `Text` | Restricted text is present (case-insensitive substring) |
| `coa` | No field | Inspector marked COA required but not provided |

Each rule needs a unique `Id`, `Explanation`, `Reference`, and HTTPS `ReferenceUrl`; `Severity` is optional. `Category` and `State` restrict applicability by exact, case-insensitive match; omit either to apply to all contexts. The UI supports `Food` and `Other` categories. Restart after changing configuration.

Screening re-runs on corrected values, lists evaluated rules, and makes unassessed coverage explicit. A flag means **potential** issue, not a confirmed violation. No findings does not mean compliance. Approval acknowledges review; it does not claim the product is lawful.

## Storage, API and limitations

- Unapproved drafts and source images remain in memory, capped at 20 retained intakes and evicted on new uploads after two hours. Restarting loses drafts.
- Approved records are atomically written to `OCRIntake.Web/App_Data/{id}.json` (ignored by Git). Override `Storage__Directory` with a persistent private directory. Write failures leave the draft unapproved.
- Records preserve original AI values/confidence, corrections, screening context/findings, revision, source SHA-256, mode, timestamps and self-attested inspector name. Original images are **not** durably retained. Keep images separately if evidentiary retention is required.
- Approved records are immutable through the API and remain readable after restart at `GET /api/approved/{id}`. Keep the ID or downloaded JSON. There is no searchable record index.
- `GET /api/config`, `POST /api/intakes` (multipart image), `GET /api/intakes/{id}`, `GET /api/intakes/{id}/image`, `POST /api/intakes/{id}/review`, and `POST /api/intakes/{id}/approve` support the workflow. Review and approval require the current revision; approval also requires a saved review, `approved: true`, and an inspector name.
- Mutating requests require `X-Intake-Request: 1` and same-origin browser requests. This is a cross-origin request guard, **not authentication**.
- This is an **unauthenticated, single-instance localhost demo**, not production software. Do not expose it publicly or use sensitive agency data. Before deployment add authenticated inspector identity and authorization, HTTPS, rate/resource limits, a database, private image retention, tenant isolation, audit integrity, and agency-approved regulations. Local JSON and self-attested names are not a legally defensible audit system.
- Dataverse/D365 integration is intentionally deferred. Approved JSON records provide an integration boundary.

## Validation

```sh
dotnet build OCRIntake.slnx --configuration Release
```

No test infrastructure existed in the repository. Manually exercise sample upload, corrections, Food screening, required explicit approval, stale-revision rejection, immutable approved records and persisted retrieval after restart. Live Azure verification requires your configured resource, models, analyzer and credentials; sample mode cannot establish extraction accuracy.