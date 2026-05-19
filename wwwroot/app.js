const form = document.querySelector("#upload-form");
const fileInput = document.querySelector("#log-file");
const fileName = document.querySelector("#file-name");
const button = document.querySelector("#analyze-button");
const result = document.querySelector("#result");
const statusPill = document.querySelector("#status-pill");

fileInput.addEventListener("change", () => {
  const file = fileInput.files[0];
  fileName.textContent = file ? `${file.name} (${formatBytes(file.size)})` : "No file selected";
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const file = fileInput.files[0];
  if (!file) {
    setStatus("Choose a file", "error");
    result.textContent = "Select a .txt or .log file before analyzing.";
    result.className = "result";
    return;
  }

  const formData = new FormData();
  formData.append("logFile", file);

  setStatus("Analyzing", "loading");
  button.disabled = true;
  result.className = "result empty";
  result.textContent = "Sending log content for analysis...";

  try {
    const response = await fetch("/api/analyze", {
      method: "POST",
      body: formData
    });

    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload.detail || payload.error || "Analysis failed.");
    }

    setStatus("Done", "done");
    result.className = "result";
    result.innerHTML = renderMarkdownHeadings(payload.analysis);
  } catch (error) {
    setStatus("Error", "error");
    result.className = "result";
    result.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});

function setStatus(text, state) {
  statusPill.textContent = text;
  statusPill.className = `status-pill ${state}`;
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function renderMarkdownHeadings(markdown) {
  const escaped = escapeHtml(markdown);
  return escaped.replace(/^## (.+)$/gm, "<h3>$1</h3>");
}

function escapeHtml(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}
