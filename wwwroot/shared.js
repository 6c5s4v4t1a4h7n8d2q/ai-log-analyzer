function setStatus(element, text, state) {
  element.textContent = text;
  element.className = `status-pill ${state}`;
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
