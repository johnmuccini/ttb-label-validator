const byId = id => document.getElementById(id);
const state = { jobId: null, timer: null };
const labels = {
  approve: "Approved", reject: "Rejected", manual_review: "Manual review",
  malformed_input: "Malformed input", external_service_unavailable: "Service unavailable",
  queued: "Queued", processing: "Processing", cancelled: "Cancelled"
};

async function jsonRequest(url, options = {}) {
  let response;
  try { response = await fetch(url, options); }
  catch {
    throw new Error("Cannot reach the local controller. Start Ttb.LabelWeb.exe, keep its window open, and use http://127.0.0.1:5080.");
  }
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.message || body.detail || `Request failed (${response.status})`);
  return body;
}

if (window.location.protocol === "file:") {
  window.location.replace("http://127.0.0.1:5080");
}

async function loadConfig() {
  const config = await jsonRequest("/api/config");
  byId("versions").textContent = `Application ${config.applicationVersion} · Rules ${config.rulesVersion}`;
  byId("choose-files").disabled = !config.apiKeyValidated;
  if (config.apiKeyValidated) setKeyStatus("API key and Gemini connection validated for this session.", true);
  if (!config.syntheticDemoAvailable) byId("run-demo").disabled = true;
}

function setKeyStatus(message, success = false, error = false) {
  const element = byId("key-status");
  element.textContent = message;
  element.className = `status-text${success ? " success" : ""}${error ? " error" : ""}`;
}

byId("toggle-key").addEventListener("click", () => {
  const input = byId("api-key");
  const button = byId("toggle-key");
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  button.setAttribute("aria-pressed", String(show));
  button.setAttribute("aria-label", show ? "Hide API key" : "Show API key");
  button.title = show ? "Hide API key" : "Show API key";
  input.focus();
  input.setSelectionRange(input.value.length, input.value.length);
});

byId("test-key").addEventListener("click", async () => {
  const input = byId("api-key");
  const button = byId("test-key");
  if (!input.value.trim()) return setKeyStatus("Enter a Gemini API key first.", false, true);
  button.disabled = true;
  setKeyStatus("Testing the key and outbound Gemini connection…");
  try {
    await jsonRequest("/api/key/validate", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ apiKey: input.value.trim() })
    });
    input.value = "";
    byId("choose-files").disabled = false;
    setKeyStatus("API key and Gemini connection validated. The key is held in memory for this session.", true);
  } catch (error) { setKeyStatus(error.message, false, true); }
  finally { button.disabled = false; }
});

byId("choose-files").addEventListener("click", () => byId("file-input").click());
byId("file-input").addEventListener("change", async event => {
  const files = [...event.target.files];
  if (!files.length) return;
  byId("selection").textContent = `${files.length} PDF file${files.length === 1 ? "" : "s"} selected. Uploading…`;
  const form = new FormData();
  files.forEach(file => form.append("files", file));
  try { startJob(await jsonRequest("/api/jobs/live", { method: "POST", body: form })); }
  catch (error) { byId("selection").textContent = error.message; byId("selection").className = "status-text error"; }
  event.target.value = "";
});

byId("run-demo").addEventListener("click", async () => {
  byId("run-demo").disabled = true;
  try { startJob(await jsonRequest("/api/jobs/demo", { method: "POST" })); }
  catch (error) { alert(error.message); byId("run-demo").disabled = false; }
});

byId("cancel").addEventListener("click", async () => {
  if (!state.jobId) return;
  await fetch(`/api/jobs/${state.jobId}`, { method: "DELETE" });
  byId("cancel").disabled = true;
});

byId("download").addEventListener("click", () => {
  if (state.jobId) window.location.href = `/api/jobs/${state.jobId}/results`;
});

function startJob(job) {
  state.jobId = job.id;
  byId("progress-panel").classList.remove("hidden");
  byId("cancel").disabled = false;
  byId("download").disabled = true;
  byId("run-demo").disabled = true;
  render(job);
  clearInterval(state.timer);
  state.timer = setInterval(refresh, 600);
  byId("progress-panel").scrollIntoView({ behavior: "smooth", block: "start" });
}

async function refresh() {
  try {
    const job = await jsonRequest(`/api/jobs/${state.jobId}`);
    render(job);
    if (job.isComplete) {
      clearInterval(state.timer);
      byId("cancel").disabled = true;
      byId("download").disabled = false;
      byId("run-demo").disabled = false;
    }
  } catch (error) { clearInterval(state.timer); byId("progress-copy").textContent = error.message; }
}

function render(job) {
  byId("progress-copy").textContent = `${job.completed} of ${job.total} files completed${job.mode === "synthetic_demo" ? " · synthetic demonstration" : ""}`;
  byId("progress-bar").style.width = `${job.total ? job.completed / job.total * 100 : 0}%`;
  const s = job.summary;
  byId("summary").innerHTML = [
    [job.total, "files"], [s.approved, "approved"], [s.rejected, "rejected"],
    [s.manualReview, "manual review"], [s.malformedInput, "malformed"],
    [s.serviceUnavailable, "service unavailable"], [s.cancelled, "cancelled"]
  ].map(([number, label]) => `<span class="summary-chip"><strong>${number}</strong> ${label}</span>`).join("");
  byId("results").innerHTML = job.items.map(renderItem).join("");
}

function renderItem(item) {
  const status = item.status || "queued";
  const timing = item.totalMilliseconds == null ? escapeHtml(item.phase) :
    `Total ${formatTime(item.totalMilliseconds)} · Gemini ${formatTime(item.geminiMilliseconds || 0)}`;
  const findings = (item.findings || []).map(renderFinding).join("");
  const error = item.error ? `<div class="finding"><strong>Processing detail</strong>${escapeHtml(item.error)}</div>` : "";
  return `<article class="result ${status}">
    <div class="result-head"><span class="filename">${escapeHtml(item.fileName)}</span>
      <span><span class="badge status-badge">${escapeHtml(labels[status] || status)}</span>${item.synthetic ? ' <span class="badge synthetic">Synthetic extraction</span>' : ""}</span>
      <span class="timing">${timing}</span></div>
    ${(findings || error) ? `<div class="findings">${findings}${error}</div>` : ""}
  </article>`;
}

function renderFinding(finding) {
  const values = finding.applicationValue != null || finding.labelValue != null ?
    `<div class="values"><div class="value"><strong>Application</strong>${escapeHtml(finding.applicationValue || "—")}</div><div class="value"><strong>Label</strong>${escapeHtml(finding.labelValue || "—")}</div></div>` : "";
  const evidence = (finding.evidence || []).map(item => `${escapeHtml(item.imageId)}: ${escapeHtml(item.text)}`).join(" · ");
  return `<div class="finding"><strong>${escapeHtml(finding.message)}</strong><span>${escapeHtml(finding.code)}</span>${values}${evidence ? `<div class="evidence">Evidence: ${evidence}</div>` : ""}</div>`;
}

function formatTime(milliseconds) { return milliseconds >= 1000 ? `${(milliseconds / 1000).toFixed(2)} s` : `${milliseconds} ms`; }
function escapeHtml(value) { return String(value ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c])); }

loadConfig().catch(error => setKeyStatus(error.message, false, true));
