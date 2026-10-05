const el = id => document.getElementById(id);
let profiles = [];
let editing = null;
const status = message => { el("settingsStatus").textContent = message; };
async function api(path, body) {
  const response = await fetch(path, body ? {
    method: "POST", headers: { "Content-Type": "application/json", "X-Intake-Request": "1" },
    body: JSON.stringify(body)
  } : {});
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}
function updateAddButton() {
  el("addField").disabled = el("profileFields").children.length >= 30;
}
function addField(definition = {}) {
  const row = document.createElement("fieldset");
  row.className = "profile-field";
  const legend = document.createElement("legend");
  legend.textContent = "Extraction field";
  row.append(legend);
  for (const [key, name, max, placeholder] of [
    ["key", "Stable field key", 64, "ThcPercentage"],
    ["name", "Display name", 100, "THC percentage"],
    ["description", "Extraction instructions", 2000, "Extract the THC percentage printed on the label. Preserve the % sign. Leave blank if not visible."]
  ]) {
    const label = document.createElement("label");
    label.textContent = name;
    const input = document.createElement(key === "description" ? "textarea" : "input");
    input.dataset.property = key;
    input.required = true;
    input.maxLength = max;
    input.value = definition[key] || "";
    input.placeholder = placeholder;
    if (key === "key") {
      input.pattern = "[A-Za-z][A-Za-z0-9_]{0,63}";
      input.title = "Start with a letter; use only letters, numbers and underscores (maximum 64 characters).";
    }
    label.append(input);
    row.append(label);
  }
  const remove = document.createElement("button");
  remove.type = "button";
  remove.textContent = "Remove field";
  remove.addEventListener("click", () => { row.remove(); updateAddButton(); });
  row.append(remove);
  el("profileFields").append(row);
  updateAddButton();
}
function render() {
  editing = profiles.find(profile => profile.id === el("editProfile").value) || null;
  el("profileName").value = editing?.name || "";
  el("version").textContent = editing
    ? `Editing v${editing.version}. Saving creates v${editing.version + 1}; earlier versions remain unchanged.`
    : "New profile: saving creates version 1.";
  el("profileFields").replaceChildren();
  if (editing) editing.fields.forEach(addField);
  else addField();
  status("");
}
async function load(selected = "") {
  profiles = await api("/api/profiles");
  el("editProfile").replaceChildren();
  const option = document.createElement("option");
  option.value = "";
  option.textContent = "New profile";
  el("editProfile").append(option);
  for (const profile of profiles) {
    const option = document.createElement("option");
    option.value = profile.id;
    option.textContent = `${profile.name} · v${profile.version}`;
    el("editProfile").append(option);
  }
  el("editProfile").value = selected;
  render();
}
el("editProfile").addEventListener("change", render);
el("addField").addEventListener("click", () => addField());
el("profileForm").addEventListener("submit", async event => {
  event.preventDefault();
  const fields = [...el("profileFields").children].map(row =>
    Object.fromEntries([...row.querySelectorAll("[data-property]")].map(input =>
      [input.dataset.property, input.value.trim()])));
  if (fields.length === 0) { status("Add at least one field."); return; }
  const request = { name: el("profileName").value.trim(), fields, version: editing?.version || 0 };
  el("editor").disabled = true;
  el("editProfile").disabled = true;
  status("Saving profile…");
  let reloadRequired = false;
  try {
    const saved = await api(editing ? `/api/profiles/${editing.id}` : "/api/profiles", request);
    editing = saved;
    try {
      await load(saved.id);
      status(`Saved ${saved.name} v${saved.version}. Refresh profiles on the intake page to select it. Existing intakes have not changed.`);
    } catch (error) {
      reloadRequired = true;
      status(`Saved ${saved.name} v${saved.version}, but settings could not refresh: ${error.message}. Reload this page before making further changes. Do not retry creating the profile.`);
    }
  } catch (error) {
    status(`${error.message} If the profile changed in another tab, reload this page before retrying.`);
  } finally {
    el("editor").disabled = reloadRequired;
    el("editProfile").disabled = reloadRequired;
  }
});
load().then(() => {
  el("editor").disabled = false;
  el("editProfile").disabled = false;
}).catch(error => status(error.message));
