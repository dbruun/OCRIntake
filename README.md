# OCRIntake

A local product-intake demo with an **ASP.NET Core / .NET 10 backend** and a plain browser UI. No Python or external NuGet packages are required.

## Run the demo

Install the .NET 10 SDK, then from the repository root:

```sh
dotnet build OCRIntake.slnx
dotnet run --project OCRIntake.Web --launch-profile http
```

Open `http://localhost:5184`. The default **Sample** mode returns clearly marked synthetic values; it does **not** inspect your image or contact Azure.

1. Choose an extraction profile, then upload one JPEG or PNG product-label image (maximum 10 MB).
2. Compare the image with all detected fields and their original confidence scores. Edit incorrect values or fill missing ones.
3. Select product category and jurisdiction. COA checkboxes record the inspector's assessment, not an AI determination of applicability.
4. Save corrections and re-screen. Review the issues, explanations and regulation links.
5. Enter the inspector's name, explicitly check the approval box, and approve. Only this action writes a durable record. Download the approved JSON if needed.

The workflow is upload → Content Understanding analysis → human review → deterministic compliance screening → explicit approval. It deliberately does not give an autonomous agent permission to approve or save on an inspector's behalf.

## Extraction settings and reusable profiles

Open **Extraction settings** from the intake page (opens a new tab so your current review remains available), or visit `http://localhost:5184/settings.html`.

- Select **New profile**, enter a profile name such as “Cannabis packaging,” and add the fields you need.
- Each field has a **stable key** (for example `ThcPercentage`), **display name** (“THC percentage”), and **extraction instructions** (“Extract the THC percentage printed on the label. Preserve the % sign. Leave blank if not visible.”).
- Keys start with a letter and contain only letters, numbers or underscores, up to 64 characters; they must be unique, ignoring case. Names are limited to 100 characters and instructions to 2,000 characters. Each profile supports 1–30 text fields; the demo supports up to 50 profiles.
- Save the profile. Use **Refresh profiles** on the intake page to select it. Sample mode produces visibly synthetic placeholders for custom fields, with unavailable confidence; it does not infer values from the uploaded image.
- Select an existing profile in settings to edit it. Saving appends a new version rather than modifying the previous one. Stale edits from another tab are rejected; reload settings to get the latest version.
- Every intake captures the selected profile's ID, version, name, field definitions and analyzer ID **at upload time**. The review UI and server validation use that snapshot, not the latest settings. Changing settings never alters an existing draft or approved record. Approved JSON includes the complete snapshot.

The original 13-field product-label schema is automatically seeded as **Product labels v1**. Profiles and all their versions persist under `Storage:Directory/profiles` (by default `OCRIntake.Web/App_Data/profiles`), separate from approved intakes. Neither is publicly served. Use private persistent storage; settings are not credentials storage. Legacy approved JSON without a profile snapshot remains readable, but does not gain a fabricated snapshot.

## Microsoft Foundry Content Understanding (live images)

Use a Microsoft Foundry resource with Content Understanding enabled and the required model deployments/default mappings configured. See Microsoft's [setup and REST quickstart](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/quickstart/use-rest-api) and [custom analyzer guide](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/tutorial/create-custom-analyzer). This application uses the stable `2025-11-01` REST API, not Azure Document Intelligence.

There is no manual analyzer creation step. On the first live upload for a profile version, the .NET backend creates or replaces that version's analyzer with `PUT /contentunderstanding/analyzers/{id}?api-version=2025-11-01`, using the saved field keys and extraction instructions. It waits for the creation operation to succeed before sending the image. Provisioning and analysis each have a 110-second deadline; the first upload can take roughly four minutes. Failed provisioning is retried on the next upload, not silently ignored.

Analyzer IDs are generated from profile ID, version and a field-schema hash, so a new settings version does not replace an older version's analyzer. Provisioning is serialized and successful versions are cached within the running process. After a restart, the backend provisions the same definition again on first use. Reserve the `ocrintake-` analyzer namespace for this app; do not edit these analyzers externally. Saving local settings does not contact Azure; provisioning happens when that profile version is actually used in live mode. Analyzer versions are retained in Azure; cleanup and service quotas are the resource owner's responsibility.

The generated analyzer uses `prebuilt-document`, which accepts label images and supports OCR-backed field confidence. All fields are strings to preserve label wording, units and ambiguous dates. Missing confidence is displayed as unavailable. QR extraction is best-effort: verify decoded contents; unreadable codes must not be treated as decoded URLs. `OCRIntake.Web/product-label-analyzer.json` supplies the initial product-label field instructions, not a fixed live analyzer.

Configure live mode through environment variables or .NET user secrets (never commit a key):

```sh
export ContentUnderstanding__Mode=Live
export ContentUnderstanding__Endpoint="https://YOUR-RESOURCE.services.ai.azure.com"
# Set ContentUnderstanding__ApiKey securely in your shell/secret manager.
dotnet run --project OCRIntake.Web --launch-profile http
```

The backend sends raw image bytes to `:analyzeBinary`, follows the service's same-origin `Operation-Location` for creation and analysis, and polls with bounded timeouts. Keys never reach the browser. Redirects are disabled to avoid forwarding credentials. Azure failures do not produce an approved record or silently fall back to sample data. `ContentUnderstanding:AnalyzerId` is no longer used: analyzer selection comes from the saved profile version. The configured API key needs permission to create/replace analyzers as well as analyze images.

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

Screening re-runs on corrected values, lists evaluated rules, and makes unassessed coverage explicit. Rule field names refer to **stable keys**, not display names, and may target custom profile fields. Rule keys must match the profile keys exactly, including case (for example `ProductName`, not `productname`). Rules are evaluated only when their needed fields are included in the intake's snapshot; `requiredAny` needs all alternatives in the schema. Rules with absent-schema fields are explicitly listed as unassessed, not reported as missing label values. Creating an extraction profile does not create compliance rules or establish legal coverage.

A flag means **potential** issue, not a confirmed violation. No findings does not mean compliance. Approval acknowledges review; it does not claim the product is lawful.

## Storage, API and limitations

- Unapproved drafts and source images remain in memory, capped at 20 retained intakes and evicted on new uploads after two hours. Restarting loses drafts.
- Approved records are atomically written to `OCRIntake.Web/App_Data/{id}.json` (ignored by Git). Override `Storage__Directory` with a persistent private directory. Write failures leave the draft unapproved.
- Records preserve original AI values/confidence, corrections, the extraction profile snapshot, screening context/findings, revision, source SHA-256, mode, timestamps and self-attested inspector name. Original images are **not** durably retained. Keep images separately if evidentiary retention is required.
- Approved records are immutable through the API and remain readable after restart at `GET /api/approved/{id}`. Keep the ID or downloaded JSON. There is no searchable record index.
- `GET /api/config`, `POST /api/intakes` (multipart image, optional `profileId` and `profileVersion`), `GET /api/intakes/{id}`, `GET /api/intakes/{id}/image`, `POST /api/intakes/{id}/review`, and `POST /api/intakes/{id}/approve` support the workflow. Upload without a profile uses the latest Product labels profile; specifying a version uses that exact immutable definition. Review and approval require the current revision; approval also requires a saved review, `approved: true`, and an inspector name.
- `GET /api/profiles` lists the latest version of each profile. `POST /api/profiles` creates a profile with `{name, fields: [{key, name, description}], version: 0}`. `POST /api/profiles/{id}` appends a version with the same payload and the current `version`. `GET /api/profiles/{id}/versions/{version}` retrieves an immutable version. Profile validation failures return 400; stale updates return 409.
- `/api/config.fields` is deprecated and retained only for legacy clients: it describes the original Product labels v1 fields, **not** the selected or current profile. New clients must use the profile definitions returned by `/api/profiles` and the intake's `profile` snapshot.
- Mutating requests require `X-Intake-Request: 1` and same-origin browser requests. This is a cross-origin request guard, **not authentication**.
- This is an **unauthenticated, single-instance localhost demo**, not production software. Do not expose it publicly or use sensitive agency data. Before deployment add authenticated inspector identity and authorization, HTTPS, rate/resource limits, a database, private image retention, tenant isolation, audit integrity, and agency-approved regulations. Local JSON and self-attested names are not a legally defensible audit system.
- Dataverse/D365 integration is intentionally deferred. Approved JSON records provide an integration boundary.

## Validation

```sh
dotnet build OCRIntake.slnx --configuration Release
```

No test infrastructure existed in the repository. Manually exercise profile creation/editing, invalid or duplicate keys, stale profile updates, refresh/selection, uploads against old and new versions, dynamic correction/approval, Food screening, unassessed rules, required explicit approval, stale-revision rejection, immutable approved records and persisted profile/approval retrieval after restart. Live Azure verification requires your configured resource, models and credentials; sample mode cannot establish extraction accuracy.