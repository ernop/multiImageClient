"use strict";
// Shared provider presentation; callers own selection and persistence.

function createGeneratorToggle(g, { checked = false, onChange } = {}) {
  const label = document.createElement("label");
  label.className = "gen-toggle" + (g.available ? "" : " unavailable");
  const baseTitle = g.available
    ? g.detail
    : `${g.detail} — NOT AVAILABLE: ${g.availabilityProblem || "missing configuration"}`;
  label.title = baseTitle;

  const cb = document.createElement("input");
  cb.type = "checkbox";
  cb.value = g.key;
  cb.dataset.available = String(g.available);
  cb.dataset.imageCapable = String(!!g.imageCapable);
  cb.dataset.imageAspectOverride = String(!!g.imageAspectOverride);
  cb.dataset.kind = g.kind || "image";
  cb.dataset.requiresImage = String(!!g.requiresImage);
  cb.dataset.sketchCapable = String(!!g.sketchCapable);
  cb.disabled = !g.available;
  cb.checked = g.available && checked;
  cb.addEventListener("change", () => {
    label.classList.toggle("checked", cb.checked);
    onChange?.(cb.checked);
  });

  label.appendChild(cb);
  label.appendChild(document.createTextNode(g.label));
  // Image-capability flag on every chip: capable targets always show a tiny
  // picture icon; text-only targets show a slashed one, but only while an
  // image is attached (CSS keys off #gens-row.has-image) — that's exactly
  // when "your attachment will NOT be sent here" matters.
  const imgFlag = document.createElement("span");
  imgFlag.className = "gen-img-flag " + (g.imageCapable ? "capable" : "text-only");
  imgFlag.innerHTML =
    '<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">' +
    '<rect x="1" y="2.5" width="14" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.5"/>' +
    '<circle cx="5.2" cy="6.4" r="1.3" fill="currentColor"/>' +
    '<path d="M3 12l3.2-3.6 2.4 2.7 1.9-2.2 2.5 3.1z" fill="currentColor"/>' +
    (g.imageCapable ? "" : '<line x1="0.5" y1="15.5" x2="15.5" y2="0.5" stroke="currentColor" stroke-width="1.7"/>') +
    "</svg>";
  label.appendChild(imgFlag);
  label.classList.toggle("checked", cb.checked);
  return label;
}
