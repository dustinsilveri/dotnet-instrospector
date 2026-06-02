const cweIdsInput = document.getElementById("cweIds");
const lookupButton = document.getElementById("lookupCwe");
const cweStatus = document.getElementById("cweStatus");
const cweWarningsSection = document.getElementById("cweWarningsSection");
const cweWarnings = document.getElementById("cweWarnings");
const cweTableSection = document.getElementById("cweTableSection");
const cweTableBody = document.getElementById("cweTableBody");

let currentItems = [];
let editingItemId = null;
const STORAGE_KEY = "cwe_viewer_state";

// Load previously viewed CWEs on page load
function loadSavedState() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved) {
      const state = JSON.parse(saved);
      if (state.items && Array.isArray(state.items) && state.items.length > 0) {
        currentItems = state.items;
        editingItemId = null;
        renderRows(currentItems);
        cweStatus.textContent = `Restored ${currentItems.length} previously viewed CWE entr${currentItems.length === 1 ? "y" : "ies"} from storage.`;
      }
    }
  } catch (error) {
    console.error("Failed to load saved state:", error);
  }
}

// Save current state to localStorage
function saveState() {
  try {
    const state = { items: currentItems };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch (error) {
    console.error("Failed to save state:", error);
  }
}

function parseIds(rawInput) {
  return rawInput
    .split(/[\s,;]+/)
    .map((value) => value.trim().replace(/^cwe-/i, ""))
    .filter((value) => /^\d+$/.test(value));
}

function renderWarnings(warnings) {
  cweWarnings.innerHTML = "";
  if (!warnings.length) {
    cweWarningsSection.hidden = true;
    return;
  }

  warnings.forEach((warning) => {
    const item = document.createElement("li");
    item.textContent = warning;
    cweWarnings.appendChild(item);
  });

  cweWarningsSection.hidden = false;
}

function renderRows(items) {
  cweTableBody.innerHTML = "";

  if (!items.length) {
    cweTableSection.hidden = true;
    editingItemId = null;
    return;
  }

  currentItems = items.map((item) => ({ ...item }));
  const fragment = document.createDocumentFragment();
  items.forEach((item) => {
    const isEditing = editingItemId === item.id;
    const row = document.createElement("tr");

    const idCell = document.createElement("td");
    idCell.textContent = `CWE-${item.id}`;

    const nameCell = document.createElement("td");
    if (isEditing) {
      const input = document.createElement("input");
      input.type = "text";
      input.value = item.name;
      input.className = "cweInlineInput";
      input.dataset.field = "name";
      nameCell.appendChild(input);
    } else {
      nameCell.textContent = item.name;
    }

    const descriptionCell = document.createElement("td");
    if (isEditing) {
      const textarea = document.createElement("textarea");
      textarea.value = item.description;
      textarea.className = "cweInlineTextarea";
      textarea.rows = 4;
      textarea.dataset.field = "description";
      descriptionCell.appendChild(textarea);
    } else {
      descriptionCell.textContent = item.description;
    }

    const sourceCell = document.createElement("td");
    const sourceLink = document.createElement("a");
    sourceLink.href = item.sourceUrl;
    sourceLink.target = "_blank";
    sourceLink.rel = "noopener noreferrer";
    sourceLink.textContent = "MITRE";
    sourceCell.appendChild(sourceLink);

    const actionsCell = document.createElement("td");
    actionsCell.className = "cweActions";

    if (isEditing) {
      const saveButton = document.createElement("button");
      saveButton.type = "button";
      saveButton.textContent = "Save";
      saveButton.className = "cweActionButton";
      saveButton.addEventListener("click", () => {
        void saveEdit(item.id, row);
      });

      const cancelButton = document.createElement("button");
      cancelButton.type = "button";
      cancelButton.textContent = "Cancel";
      cancelButton.className = "cweActionButton cweActionButtonSecondary";
      cancelButton.addEventListener("click", () => {
        editingItemId = null;
        renderRows(currentItems);
      });

      actionsCell.appendChild(saveButton);
      actionsCell.appendChild(cancelButton);
    } else {
      const editButton = document.createElement("button");
      editButton.type = "button";
      editButton.textContent = "Edit";
      editButton.className = "cweActionButton";
      editButton.addEventListener("click", () => {
        editingItemId = item.id;
        renderRows(currentItems);
      });

      const deleteButton = document.createElement("button");
      deleteButton.type = "button";
      deleteButton.textContent = "Delete";
      deleteButton.className = "cweActionButton cweActionButtonDanger";
      deleteButton.addEventListener("click", () => {
        void deleteRow(item.id);
      });

      actionsCell.appendChild(editButton);
      actionsCell.appendChild(deleteButton);
    }

    row.appendChild(idCell);
    row.appendChild(nameCell);
    row.appendChild(descriptionCell);
    row.appendChild(sourceCell);
    row.appendChild(actionsCell);

    fragment.appendChild(row);
  });

  cweTableBody.appendChild(fragment);
  cweTableSection.hidden = false;
}

async function saveEdit(itemId, row) {
  const nameInput = row.querySelector('[data-field="name"]');
  const descriptionInput = row.querySelector('[data-field="description"]');

  const nextName = nameInput?.value.trim() ?? "";
  const nextDescription = descriptionInput?.value.trim() ?? "";

  if (!nextName || !nextDescription) {
    cweStatus.textContent = "Name and description are required when editing a CWE row.";
    return;
  }

  const existingItem = currentItems.find((item) => item.id === itemId);
  if (!existingItem) {
    cweStatus.textContent = `CWE-${itemId} is no longer available in the current table.`;
    return;
  }

  const payload = {
    id: itemId,
    name: nextName,
    description: nextDescription,
    sourceUrl: existingItem.sourceUrl
  };

  cweStatus.textContent = `Saving CWE-${itemId}...`;

  const response = await fetch(`/api/cwe/${itemId}`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });

  const responsePayload = await response.json().catch(() => ({}));
  if (!response.ok) {
    cweStatus.textContent = `Failed to save CWE-${itemId}: ${responsePayload.error || responsePayload.title || "request failed"}`;
    return;
  }

  currentItems = currentItems.map((item) => item.id === itemId
    ? { ...item, name: nextName, description: nextDescription }
    : item);

  editingItemId = null;
  renderRows(currentItems);
  saveState();
  cweStatus.textContent = `Saved CWE-${itemId}.`;
}

async function deleteRow(itemId) {
  cweStatus.textContent = `Deleting CWE-${itemId}...`;

  const response = await fetch(`/api/cwe/${itemId}`, {
    method: "DELETE"
  });

  const responsePayload = await response.json().catch(() => ({}));
  if (!response.ok) {
    cweStatus.textContent = `Failed to delete CWE-${itemId}: ${responsePayload.error || responsePayload.title || "request failed"}`;
    return;
  }

  currentItems = currentItems.filter((item) => item.id !== itemId);
  editingItemId = editingItemId === itemId ? null : editingItemId;
  renderRows(currentItems);
  saveState();

  if (!currentItems.length) {
    cweStatus.textContent = "All CWE rows have been removed and persisted.";
    return;
  }

  cweStatus.textContent = `Deleted CWE-${itemId}.`;
}

async function lookupCwes() {
  const ids = parseIds(cweIdsInput.value);
  if (!ids.length) {
    cweStatus.textContent = "Enter at least one numeric CWE ID.";
    return;
  }

  // Skip IDs already shown in the table
  const newIds = ids.filter(id => !currentItems.some(item => String(item.id) === id));
  if (!newIds.length) {
    cweStatus.textContent = "All entered IDs are already in the table.";
    cweIdsInput.value = "";
    return;
  }

  lookupButton.disabled = true;
  cweStatus.textContent = `Looking up ${newIds.length} CWE entr${newIds.length === 1 ? "y" : "ies"} from MITRE...`;

  try {
    const response = await fetch(`/api/cwe?ids=${encodeURIComponent(newIds.join(","))}`);
    const payload = await response.json().catch(() => ({}));

    if (!response.ok) {
      throw new Error(payload.error || payload.title || "Failed to load CWE entries.");
    }

    const items = payload.items ?? [];
    const warnings = payload.warnings ?? [];

    editingItemId = null;
    renderWarnings(warnings);

    // Merge new items into the existing table
    const merged = [
      ...currentItems,
      ...items.filter(item => !currentItems.some(existing => existing.id === item.id))
    ];
    renderRows(merged);
    saveState();
    cweIdsInput.value = "";

    cweStatus.textContent = `Added ${items.length} CWE entr${items.length === 1 ? "y" : "ies"}.`;
  } catch (error) {
    cweStatus.textContent = `Lookup failed: ${error.message}`;
    renderWarnings([]);
  } finally {
    lookupButton.disabled = false;
  }
}

lookupButton.addEventListener("click", lookupCwes);
cweIdsInput.addEventListener("keydown", (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
    lookupCwes();
  }
});

// Restore saved state on page load
document.addEventListener("DOMContentLoaded", loadSavedState);
