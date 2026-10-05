let intake;
let labels;
let dirty = true;
const el = id => document.getElementById(id);
const status = text => { el("status").textContent = text; };
async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options, headers: { "X-Intake-Request": "1", ...options.headers }
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || data.detail || `Request failed (${response.status})`);
  return data;
}
const post = (path, body) => api(path, {
  method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
});
function renderScreening() {
  const container = el("screening");
  container.replaceChildren();
  const notice = document.createElement("p");
  notice.textContent = intake.screening.notice;
  container.append(notice);
  const coverage = document.createElement("p");
  coverage.textContent = `Evaluated rules: ${intake.screening.evaluatedRules.join(", ") || "None for this context"}`;
  container.append(coverage);
  for (const finding of intake.screening.findings) {
    const card = document.createElement("p");
    card.className = "notice";
    card.textContent = `${finding.severity}: ${finding.message} `;
    const link = document.createElement("a");
    link.textContent = finding.reference;
    link.href = finding.referenceUrl;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    card.append(link);
    container.append(card);
  }
  if (!intake.screening.findings.length) {
    const p = document.createElement("p");
    p.textContent = "No issues flagged by the evaluated rules. This is not a compliance clearance.";
    container.append(p);
  }
}
function markDirty() {
  dirty = true;
  el("approveCheck").checked = false;
  el("approveButton").disabled = true;
}
el("reviewForm").addEventListener("input", markDirty);
el("upload").addEventListener("submit", async event => {
  event.preventDefault();
  el("extract").disabled = true;
  el("review").hidden = true;
  intake = null;
  status("Extracting label information… This can take up to two minutes.");
  try {
    const body = new FormData();
    body.append("image", el("file").files[0]);
    intake = await api("/api/intakes", { method: "POST", body });
    el("preview").src = `/api/intakes/${intake.id}/image`;
    el("fields").replaceChildren();
    for (const [key, labelText] of Object.entries(labels)) {
      const label = document.createElement("label");
      label.textContent = labelText;
      const field = document.createElement("textarea");
      field.dataset.field = key;
      field.value = intake.values[key] || "";
      field.maxLength = 10000;
      const confidence = document.createElement("small");
      const original = intake.detected[key];
      confidence.textContent = `Original AI value: ${original.value || "Not detected"} · Confidence: ${
        original.confidence == null ? "Unavailable" : `${Math.round(original.confidence * 100)}%`
      } (original extraction; not rescored after edits)`;
      label.append(field, confidence);
      el("fields").append(label);
    }
    el("category").value = intake.context.category;
    el("state").value = "";
    el("coaRequired").checked = false;
    el("coaProvided").checked = false;
    el("reviewForm").querySelectorAll("input,textarea,select,button").forEach(field => { field.disabled = false; });
    el("approval").hidden = false;
    el("approval").reset();
    el("download").hidden = true;
    markDirty();
    renderScreening();
    el("review").hidden = false;
    status("Extraction ready. Review every field, select context, and save corrections before approval.");
  } catch (error) { status(error.message); }
  finally { el("extract").disabled = false; }
});
el("reviewForm").addEventListener("submit", async event => {
  event.preventDefault();
  el("saveReview").disabled = true;
  el("extract").disabled = true;
  // Capture a snapshot; edits made during this request still require another save.
  const values = Object.fromEntries([...document.querySelectorAll("[data-field]")].map(field => [field.dataset.field, field.value]));
  const context = {
    category: el("category").value, state: el("state").value.trim(),
    coaRequired: el("coaRequired").checked, coaProvided: el("coaProvided").checked
  };
  dirty = false;
  el("approveButton").disabled = true;
  try {
    intake = await post(`/api/intakes/${intake.id}/review`, { values, context, revision: intake.revision });
    renderScreening();
    el("approveButton").disabled = dirty;
    status(dirty ? "Further edits are unsaved. Save again before approval." : "Review saved and screening refreshed. Explicit approval is still required.");
  } catch (error) { markDirty(); status(error.message); }
  finally { el("saveReview").disabled = false; el("extract").disabled = false; }
});
el("approval").addEventListener("submit", async event => {
  event.preventDefault();
  if (dirty || !el("approveCheck").checked) return;
  el("approveButton").disabled = true;
  el("extract").disabled = true;
  el("reviewForm").querySelectorAll("input,textarea,select,button").forEach(field => { field.disabled = true; });
  try {
    intake = await post(`/api/intakes/${intake.id}/approve`, {
      approved: true, inspector: el("inspector").value, revision: intake.revision
    });
    el("approval").hidden = true;
    el("download").hidden = false;
    status(`Approved by ${intake.inspector}. Record ${intake.id} saved at ${intake.approvedAt}.`);
  } catch (error) {
    el("reviewForm").querySelectorAll("input,textarea,select,button").forEach(field => { field.disabled = false; });
    el("approveButton").disabled = dirty;
    status(error.message);
  }
  finally { el("extract").disabled = false; }
});
el("download").addEventListener("click", () => {
  const url = URL.createObjectURL(new Blob([JSON.stringify(intake, null, 2)], { type: "application/json" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `approved-${intake.id}.json`;
  link.click();
  URL.revokeObjectURL(url);
});
api("/api/config").then(config => {
  labels = config.fields;
  el("extract").disabled = false;
  el("mode").textContent = config.mode === "Sample"
    ? "SAMPLE MODE: synthetic label values only. Your uploaded image is NOT analyzed. Sample records are marked as such."
    : "LIVE MODE: images are sent to Microsoft Content Understanding. Local demo only; do not expose this unauthenticated application publicly.";
}).catch(error => { status(error.message); el("extract").disabled = true; });
