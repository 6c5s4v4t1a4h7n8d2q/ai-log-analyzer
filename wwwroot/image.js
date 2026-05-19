const imageForm = document.querySelector("#image-form");
const imageInput = document.querySelector("#image-file");
const imageFileName = document.querySelector("#image-file-name");
const describeButton = document.querySelector("#describe-button");
const imageResult = document.querySelector("#image-result");
const imageStatusPill = document.querySelector("#image-status-pill");
const previewPanel = document.querySelector("#preview-panel");

imageInput.addEventListener("change", () => {
  const file = imageInput.files[0];
  imageFileName.textContent = file ? `${file.name} (${formatBytes(file.size)})` : "No file selected";
  renderPreview(file);
});

imageForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  const file = imageInput.files[0];
  if (!file) {
    setStatus(imageStatusPill, "Choose a file", "error");
    imageResult.textContent = "Select an image before asking for an explanation.";
    imageResult.className = "result";
    return;
  }

  const formData = new FormData();
  formData.append("imageFile", file);

  setStatus(imageStatusPill, "Explaining", "loading");
  describeButton.disabled = true;
  imageResult.className = "result empty";
  imageResult.textContent = "Sending image for analysis...";

  try {
    const response = await fetch("/api/describe-image", {
      method: "POST",
      body: formData
    });

    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload.detail || payload.error || "Image explanation failed.");
    }

    setStatus(imageStatusPill, "Done", "done");
    imageResult.className = "result";
    imageResult.innerHTML = renderMarkdownHeadings(payload.description);
  } catch (error) {
    setStatus(imageStatusPill, "Error", "error");
    imageResult.className = "result";
    imageResult.textContent = error.message;
  } finally {
    describeButton.disabled = false;
  }
});

function renderPreview(file) {
  if (!file) {
    previewPanel.className = "preview-panel empty";
    previewPanel.innerHTML = "<span>No image selected</span>";
    return;
  }

  const url = URL.createObjectURL(file);
  previewPanel.className = "preview-panel";
  previewPanel.innerHTML = "";

  const image = document.createElement("img");
  image.src = url;
  image.alt = "Selected upload preview";
  image.addEventListener("load", () => URL.revokeObjectURL(url), { once: true });

  previewPanel.append(image);
}
