const versionSelect = document.getElementById("version");
const introspectButton = document.getElementById("introspect");
const exportCsvButton = document.getElementById("exportCsv");
const statusText = document.getElementById("status");
const summarySection = document.getElementById("summary");
const imageValue = document.getElementById("image");
const assemblyCountValue = document.getElementById("assemblyCount");
const runtimeFoldersValue = document.getElementById("runtimeFolders");
const resultsSection = document.getElementById("resultsSection");
const resultsContainer = document.getElementById("results");
const warningsSection = document.getElementById("warningsSection");
const warningsList = document.getElementById("warnings");
const filterInput = document.getElementById("filter");
const rootNamespaceSelect = document.getElementById("rootNamespace");
const drillLevels = document.getElementById("drillLevels");
const typeSelector = document.getElementById("typeSelector");
const memberSelector = document.getElementById("memberSelector");
const drillPath = document.getElementById("drillPath");
const sourceLinks = document.getElementById("sourceLinks");
const sourceDotNetLink = document.getElementById("sourceDotNetLink");
const sourceGitHubLink = document.getElementById("sourceGitHubLink");
const assemblyTemplate = document.getElementById("assemblyTemplate");

let currentAssemblies = [];
let currentVersion = "";
let selectedSegments = [];
let selectedType = "";
let selectedMember = "";
let namespaceChildren = new Map();
let typeDetailsById = new Map();
let riskyAnnotations = new Set();

function createRiskKey(annotation) {
  return [
    annotation.version,
    annotation.assemblyPath,
    annotation.typeFullName,
    annotation.itemKind,
    annotation.itemName ?? ""
  ].join("|");
}

async function loadRiskAnnotations(version) {
  riskyAnnotations = new Set();
  if (!version) {
    return;
  }

  const response = await fetch(`/api/risky/${encodeURIComponent(version)}`);
  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    throw new Error(payload.error || payload.title || "Failed to load risk annotations.");
  }

  const payload = await response.json();
  riskyAnnotations = new Set((payload ?? [])
    .filter((item) => item.isRisky)
    .map((item) => createRiskKey({
      version: item.version,
      assemblyPath: item.assemblyPath,
      typeFullName: item.typeFullName,
      itemKind: item.itemKind,
      itemName: item.itemName ?? ""
    })));
}

async function saveRiskAnnotation(annotation, isRisky) {
  const response = await fetch("/api/risky", {
    method: "PUT",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      version: annotation.version,
      assemblyPath: annotation.assemblyPath,
      typeFullName: annotation.typeFullName,
      itemKind: annotation.itemKind,
      itemName: annotation.itemName,
      isRisky
    })
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    throw new Error(payload.error || payload.title || "Failed to save risk annotation.");
  }
}

async function loadVersions() {
  try {
    const response = await fetch("/api/versions");
    if (!response.ok) {
      throw new Error("Version API request failed.");
    }

    const versions = await response.json();
    versionSelect.innerHTML = "";

    versions.forEach((version, index) => {
      const option = document.createElement("option");
      option.value = version;
      option.textContent = version;
      if (index === 0) {
        option.selected = true;
      }
      versionSelect.appendChild(option);
    });

    exportCsvButton.disabled = versions.length === 0;

    statusText.textContent = "Choose a version and start introspection.";
  } catch (error) {
    statusText.textContent = `Failed to load versions: ${error.message}`;
  }
}

async function exportCsv() {
  const version = versionSelect.value;
  if (!version) {
    statusText.textContent = "Select a version first.";
    return;
  }

  statusText.textContent = `Starting CSV export for .NET ${version}...`;

  const link = document.createElement("a");
  link.href = `/api/introspect/export-csv/${encodeURIComponent(version)}`;
  link.download = `dotnet-introspection-${version.replaceAll(".", "-")}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();

  statusText.textContent = `CSV export requested for .NET ${version}.`;
}

function addOptions(selectElement, items, placeholder) {
  selectElement.innerHTML = "";

  const initial = document.createElement("option");
  initial.value = "";
  initial.textContent = placeholder;
  selectElement.appendChild(initial);

  items.forEach((item) => {
    const option = document.createElement("option");
    option.value = item.value;
    option.textContent = item.label;
    selectElement.appendChild(option);
  });

  selectElement.disabled = items.length === 0;
}

function getRootName(qualifiedName) {
  return qualifiedName.split(".")[0];
}

function addNamespaceEdge(parentNamespace, childSegment) {
  if (!namespaceChildren.has(parentNamespace)) {
    namespaceChildren.set(parentNamespace, new Set());
  }

  namespaceChildren.get(parentNamespace).add(childSegment);
}

function addTypeDetails(type, assembly) {
  const typeId = type.typeId ?? `${assembly.assemblyPath}|${type.fullName}`;

  if (!typeDetailsById.has(typeId)) {
    typeDetailsById.set(typeId, {
      typeId,
      assemblyName: assembly.assemblyName,
      assemblyPath: assembly.assemblyPath,
      fullName: type.fullName,
      namespace: type.namespace,
      name: type.name,
      methods: new Set(),
      properties: new Set()
    });
  }

  const existing = typeDetailsById.get(typeId);
  (type.methods ?? []).forEach((methodName) => existing.methods.add(methodName));
  (type.properties ?? []).forEach((propertyName) => existing.properties.add(propertyName));
}

function rebuildIndexes() {
  namespaceChildren = new Map();
  typeDetailsById = new Map();

  currentAssemblies.forEach((assembly) => {
    (assembly.namespaces ?? []).forEach((ns) => {
      const parts = ns.split(".");
      for (let i = 0; i < parts.length; i += 1) {
        const parent = parts.slice(0, i).join(".");
        addNamespaceEdge(parent, parts[i]);
      }
    });

    (assembly.types ?? []).forEach((type) => {
      addTypeDetails(type, assembly);

      const namespaceValue = type.namespace ?? "";
      if (namespaceValue) {
        const parts = namespaceValue.split(".");
        for (let i = 0; i < parts.length; i += 1) {
          const parent = parts.slice(0, i).join(".");
          addNamespaceEdge(parent, parts[i]);
        }
      }
    });
  });
}

function getCurrentNamespacePath() {
  const root = rootNamespaceSelect.value;
  if (!root) {
    return "";
  }

  const path = [root, ...selectedSegments.filter((segment) => !!segment)];
  return path.join(".");
}

function updateSelectedPathLabel() {
  const namespacePath = getCurrentNamespacePath();
  if (!namespacePath) {
    drillPath.textContent = "Path: (none)";
    updateSourceLinks();
    return;
  }

  if (selectedType && typeDetailsById.has(selectedType)) {
    const type = typeDetailsById.get(selectedType);

    if (selectedMember) {
      const [kind, memberName] = selectedMember.split(":", 2);
      drillPath.textContent = `${type.fullName}.${memberName} (${kind})`;
    } else {
      drillPath.textContent = type.fullName;
    }
  } else {
    drillPath.textContent = `Namespace: ${namespacePath}`;
  }

  updateSourceLinks();
}

function updateSourceLinks() {
  if (!selectedType || !typeDetailsById.has(selectedType)) {
    sourceLinks.hidden = true;
    return;
  }

  const type = typeDetailsById.get(selectedType);
  const selectedMemberLabel = selectedMember ? selectedMember.split(":", 2)[1] : "";

  const sourceDotNetQuery = selectedMemberLabel
    ? `${type.fullName} ${selectedMemberLabel}`
    : type.fullName;

  const githubQuery = selectedMemberLabel
    ? `repo:dotnet/runtime ${type.fullName} ${selectedMemberLabel}`
    : `repo:dotnet/runtime ${type.fullName}`;

  sourceDotNetLink.href = `https://source.dot.net/#q=${encodeURIComponent(sourceDotNetQuery)}`;
  sourceGitHubLink.href = `https://github.com/search?q=${encodeURIComponent(githubQuery)}&type=code`;
  sourceLinks.hidden = false;
}

function refreshNamespaceSelectors() {
  const roots = Array.from(namespaceChildren.get("") ?? []).sort((a, b) => a.localeCompare(b));
  addOptions(
    rootNamespaceSelect,
    roots.map((root) => ({ value: root, label: root })),
    "Select top-level namespace..."
  );

  drillLevels.innerHTML = "";
  addOptions(typeSelector, [], "Select class...");
  addOptions(memberSelector, [], "Select property or method...");
  selectedSegments = [];
  selectedType = "";
  selectedMember = "";
  updateSelectedPathLabel();
}

function refreshDrillLevelSelectors(root) {
  drillLevels.innerHTML = "";

  if (!root) {
    updateSelectedPathLabel();
    return;
  }

  let parentPath = root;
  for (let level = 0; ; level += 1) {
    const children = Array.from(namespaceChildren.get(parentPath) ?? []).sort((a, b) => a.localeCompare(b));
    if (children.length === 0) {
      const terminalSelect = document.createElement("select");
      terminalSelect.disabled = true;
      addOptions(
        terminalSelect,
        [],
        `No sub-namespaces under ${parentPath}`
      );
      drillLevels.appendChild(terminalSelect);
      break;
    }

    const select = document.createElement("select");
    select.dataset.level = String(level);

    addOptions(
      select,
      children.map((segment) => ({ value: segment, label: segment })),
      `Select sub-namespace under ${parentPath}`
    );

    const selectedAtLevel = selectedSegments[level] ?? "";
    if (selectedAtLevel && children.includes(selectedAtLevel)) {
      select.value = selectedAtLevel;
      parentPath = `${parentPath}.${selectedAtLevel}`;
    } else {
      selectedSegments = selectedSegments.slice(0, level);
    }

    select.addEventListener("change", () => {
      const depth = Number.parseInt(select.dataset.level, 10);
      const value = select.value;

      if (value) {
        selectedSegments[depth] = value;
        selectedSegments = selectedSegments.slice(0, depth + 1);
      } else {
        selectedSegments = selectedSegments.slice(0, depth);
      }

      selectedType = "";
      selectedMember = "";

      refreshDrillLevelSelectors(rootNamespaceSelect.value);
      refreshTypeSelector();
      refreshMemberSelector();
      updateSelectedPathLabel();
      applyFilter();
    });

    drillLevels.appendChild(select);

    if (!select.value) {
      break;
    }
  }

  updateSelectedPathLabel();
}

function refreshTypeSelector() {
  const currentNamespace = getCurrentNamespacePath();
  if (!currentNamespace) {
    addOptions(typeSelector, [], "Select class...");
    return;
  }

  const matchingTypes = Array.from(typeDetailsById.values())
    .filter((type) => type.namespace === currentNamespace)
    .sort((a, b) => {
      const byName = a.name.localeCompare(b.name);
      if (byName !== 0) {
        return byName;
      }

      return a.assemblyName.localeCompare(b.assemblyName);
    });

  addOptions(
    typeSelector,
    matchingTypes.map((type) => ({ value: type.typeId, label: `${type.name} (${type.assemblyName})` })),
    `Select class in ${currentNamespace}...`
  );

  if (selectedType && matchingTypes.some((type) => type.typeId === selectedType)) {
    typeSelector.value = selectedType;
  } else {
    selectedType = "";
    typeSelector.value = "";
  }

  updateSelectedPathLabel();
}

function refreshMemberSelector() {
  if (!selectedType || !typeDetailsById.has(selectedType)) {
    addOptions(memberSelector, [], "Select property or method...");
    return;
  }

  const type = typeDetailsById.get(selectedType);
  const methods = Array.from(type.methods).sort((a, b) => a.localeCompare(b));
  const properties = Array.from(type.properties).sort((a, b) => a.localeCompare(b));

  const options = [
    ...properties.map((name) => ({ value: `property:${name}`, label: `Property: ${name}` })),
    ...methods.map((name) => ({ value: `method:${name}`, label: `Method: ${name}` }))
  ];

  addOptions(memberSelector, options, "Select property or method...");
  if (selectedMember && options.some((option) => option.value === selectedMember)) {
    memberSelector.value = selectedMember;
  } else {
    selectedMember = "";
    memberSelector.value = "";
  }

  updateSelectedPathLabel();
}

function getSelection() {
  return {
    root: rootNamespaceSelect.value,
    namespaceScope: getCurrentNamespacePath(),
    typeName: selectedType,
    memberSelection: selectedMember
  };
}

function assemblyMatchesRoot(item, root) {
  if (!root) {
    return true;
  }

  const inNamespaces = (item.namespaces ?? []).some((ns) => ns === root || ns.startsWith(`${root}.`));
  const inTypes = (item.types ?? []).some((type) => type.namespace === root || type.namespace.startsWith(`${root}.`));
  return inNamespaces || inTypes;
}

function assemblyMatchesSelection(item, namespaceScope, typeName, memberSelection) {
  const types = item.types ?? [];

  if (typeName) {
    const selectedTypeDetails = typeDetailsById.get(typeName);
    if (!selectedTypeDetails || selectedTypeDetails.assemblyPath !== item.assemblyPath) {
      return false;
    }

    const exactType = types.find((type) => type.fullName === selectedTypeDetails.fullName);
    if (!exactType) {
      return false;
    }

    if (!memberSelection) {
      return true;
    }

    const [kind, memberName] = memberSelection.split(":", 2);
    if (kind === "method") {
      return (exactType.methods ?? []).includes(memberName);
    }

    if (kind === "property") {
      return (exactType.properties ?? []).includes(memberName);
    }

    return false;
  }

  if (!namespaceScope) {
    return true;
  }

  const nsMatch = (item.namespaces ?? []).some((ns) => ns === namespaceScope || ns.startsWith(`${namespaceScope}.`));
  const typeMatch = types.some((type) => type.namespace === namespaceScope || type.namespace.startsWith(`${namespaceScope}.`));
  return nsMatch || typeMatch;
}

function getDisplayItems(item, root, namespaceScope, typeName, memberSelection) {
  const types = item.types ?? [];

  if (typeName) {
    const selectedTypeDetails = typeDetailsById.get(typeName);
    if (!selectedTypeDetails || selectedTypeDetails.assemblyPath !== item.assemblyPath) {
      return [];
    }

    const exactType = types.find((type) => type.fullName === selectedTypeDetails.fullName);
    if (!exactType) {
      return [];
    }

    if (memberSelection) {
      const [kind, memberName] = memberSelection.split(":", 2);
      return [
        {
          text: `Class: ${exactType.fullName}`,
          annotation: {
            assemblyPath: item.assemblyPath,
            typeFullName: exactType.fullName,
            itemKind: "class",
            itemName: ""
          }
        },
        {
          text: `${kind === "property" ? "Property" : "Method"}: ${memberName}`,
          annotation: {
            assemblyPath: item.assemblyPath,
            typeFullName: exactType.fullName,
            itemKind: kind,
            itemName: memberName
          }
        }
      ];
    }

    const properties = (exactType.properties ?? []).map((name) => ({
      text: `Property: ${name}`,
      annotation: {
        assemblyPath: item.assemblyPath,
        typeFullName: exactType.fullName,
        itemKind: "property",
        itemName: name
      }
    }));
    const methods = (exactType.methods ?? []).map((name) => ({
      text: `Method: ${name}`,
      annotation: {
        assemblyPath: item.assemblyPath,
        typeFullName: exactType.fullName,
        itemKind: "method",
        itemName: name
      }
    }));
    return [{
      text: `Class: ${exactType.fullName}`,
      annotation: {
        assemblyPath: item.assemblyPath,
        typeFullName: exactType.fullName,
        itemKind: "class",
        itemName: ""
      }
    }, ...properties, ...methods];
  }

  if (namespaceScope) {
    const namespaces = (item.namespaces ?? [])
      .filter((ns) => ns === namespaceScope || ns.startsWith(`${namespaceScope}.`))
      .map((name) => ({ text: `Namespace: ${name}` }));

    const classes = types
      .filter((type) => type.namespace === namespaceScope || type.namespace.startsWith(`${namespaceScope}.`))
      .map((type) => ({
        text: `Class: ${type.fullName}`,
        annotation: {
          assemblyPath: item.assemblyPath,
          typeFullName: type.fullName,
          itemKind: "class",
          itemName: ""
        }
      }));

    return [...namespaces, ...classes];
  }

  if (root) {
    const namespaces = (item.namespaces ?? [])
      .filter((ns) => ns === root || ns.startsWith(`${root}.`))
      .map((name) => ({ text: `Namespace: ${name}` }));

    const classes = types
      .filter((type) => type.namespace === root || type.namespace.startsWith(`${root}.`))
      .map((type) => ({
        text: `Class: ${type.fullName}`,
        annotation: {
          assemblyPath: item.assemblyPath,
          typeFullName: type.fullName,
          itemKind: "class",
          itemName: ""
        }
      }));

    return [...namespaces, ...classes];
  }

  return (item.namespaces ?? []).map((name) => ({ text: `Namespace: ${name}` }));
}

function renderAssemblies(items, root = "", namespaceScope = "", typeName = "", memberSelection = "") {
  resultsContainer.innerHTML = "";

  if (!items.length) {
    resultsContainer.textContent = "No assemblies found.";
    return;
  }

  const fragment = document.createDocumentFragment();

  items.forEach((item) => {
    const assemblyNode = assemblyTemplate.content.firstElementChild.cloneNode(true);
    assemblyNode.querySelector(".assemblyName").textContent = item.assemblyName;
    const scopedNamespaces = namespaceScope
      ? (item.namespaces ?? []).filter((ns) => ns === namespaceScope || ns.startsWith(`${namespaceScope}.`))
      : root
        ? (item.namespaces ?? []).filter((ns) => ns === root || ns.startsWith(`${root}.`))
        : (item.namespaces ?? []);

    const scopedTypes = typeName
      ? (item.types ?? []).filter((type) => {
          const selectedTypeDetails = typeDetailsById.get(typeName);
          return !!selectedTypeDetails &&
            selectedTypeDetails.assemblyPath === item.assemblyPath &&
            type.fullName === selectedTypeDetails.fullName;
        })
      : namespaceScope
        ? (item.types ?? []).filter((type) => type.namespace === namespaceScope || type.namespace.startsWith(`${namespaceScope}.`))
        : root
          ? (item.types ?? []).filter((type) => type.namespace === root || type.namespace.startsWith(`${root}.`))
          : (item.types ?? []);

    const scopedLabel = typeName
      ? selectedMember
        ? `selected type/member (${scopedTypes.length} type match)`
        : `selected type (${scopedTypes.length} type match)`
      : namespaceScope || root
        ? `${scopedNamespaces.length} namespaces, ${scopedTypes.length} classes in scope (${item.namespaceCount} namespaces total)`
        : `${item.namespaceCount} namespaces, ${(item.types ?? []).length} classes`;

    assemblyNode.querySelector(".meta").textContent = `${item.runtimeFolder} • ${scopedLabel}`;
    assemblyNode.querySelector(".dll").textContent = `DLL: ${item.assemblyDll}`;
    assemblyNode.querySelector(".path").textContent = `Path: ${item.assemblyPath}`;

    const namespaceList = assemblyNode.querySelector(".namespaces");
    namespaceList.innerHTML = "";

    const displayItems = getDisplayItems(item, root, namespaceScope, typeName, memberSelection);
    displayItems.forEach((entry) => {
      const li = document.createElement("li");

      if (entry.annotation && currentVersion) {
        const annotation = {
          version: currentVersion,
          assemblyPath: entry.annotation.assemblyPath,
          typeFullName: entry.annotation.typeFullName,
          itemKind: entry.annotation.itemKind,
          itemName: entry.annotation.itemName ?? ""
        };

        const key = createRiskKey(annotation);
        const label = document.createElement("label");
        label.className = "riskyToggle";

        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.checked = riskyAnnotations.has(key);

        const text = document.createElement("span");
        text.textContent = `${entry.text} | risky`;

        checkbox.addEventListener("change", async () => {
          const previousValue = !checkbox.checked;
          const nextValue = checkbox.checked;

          if (nextValue) {
            riskyAnnotations.add(key);
          } else {
            riskyAnnotations.delete(key);
          }

          checkbox.disabled = true;
          try {
            await saveRiskAnnotation(annotation, nextValue);
          } catch (error) {
            checkbox.checked = previousValue;
            if (previousValue) {
              riskyAnnotations.add(key);
            } else {
              riskyAnnotations.delete(key);
            }

            statusText.textContent = `Failed to save risk flag: ${error.message}`;
          } finally {
            checkbox.disabled = false;
          }
        });

        label.appendChild(checkbox);
        label.appendChild(text);
        li.appendChild(label);
      } else {
        li.textContent = entry.text;
      }

      namespaceList.appendChild(li);
    });

    fragment.appendChild(assemblyNode);
  });

  resultsContainer.appendChild(fragment);
}

function applyFilter() {
  const { root, namespaceScope, typeName, memberSelection } = getSelection();
  const term = filterInput.value.trim().toLowerCase();

  const filtered = currentAssemblies.filter((item) => {
    if (!assemblyMatchesRoot(item, root)) {
      return false;
    }

    if (!assemblyMatchesSelection(item, namespaceScope, typeName, memberSelection)) {
      return false;
    }

    if (!term) {
      return true;
    }

    if (item.assemblyDll.toLowerCase().includes(term) || item.assemblyPath.toLowerCase().includes(term)) {
      return true;
    }

    if (item.assemblyName.toLowerCase().includes(term)) {
      return true;
    }

    const namespaceMatch = (item.namespaces ?? []).some((ns) => ns.toLowerCase().includes(term));
    if (namespaceMatch) {
      return true;
    }

    const typeMatch = (item.types ?? []).some((type) => {
      if (type.fullName.toLowerCase().includes(term)) {
        return true;
      }

      const methodMatch = (type.methods ?? []).some((name) => name.toLowerCase().includes(term));
      if (methodMatch) {
        return true;
      }

      return (type.properties ?? []).some((name) => name.toLowerCase().includes(term));
    });

    return typeMatch;
  });

  renderAssemblies(filtered, root, namespaceScope, typeName, memberSelection);
}

async function introspect() {
  const version = versionSelect.value;
  if (!version) {
    statusText.textContent = "Select a version first.";
    return;
  }

  introspectButton.disabled = true;
  statusText.textContent = `Pulling and introspecting .NET ${version}...`;

  try {
    const response = await fetch("/api/introspect", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ version })
    });

    const payload = await response.json();

    if (!response.ok) {
      throw new Error(payload.error || payload.title || "Introspection failed.");
    }

    currentAssemblies = (payload.assemblies ?? []).map((item) => ({
      ...item,
      namespaces: item.namespaces ?? [],
      types: (item.types ?? []).map((type) => ({
        ...type,
        methods: type.methods ?? [],
        properties: type.properties ?? []
      }))
    }));
    currentVersion = payload.version ?? version;

    let annotationLoadMessage = "";
    try {
      await loadRiskAnnotations(currentVersion);
    } catch (error) {
      annotationLoadMessage = ` Risk annotations unavailable: ${error.message}`;
    }

    imageValue.textContent = payload.image;
    assemblyCountValue.textContent = String(payload.assemblies.length);
    runtimeFoldersValue.textContent = payload.runtimeFolders.join(", ");

    warningsList.innerHTML = "";
    if (payload.warnings.length > 0) {
      warningsSection.hidden = false;
      payload.warnings.forEach((warning) => {
        const item = document.createElement("li");
        item.textContent = warning;
        warningsList.appendChild(item);
      });
    } else {
      warningsSection.hidden = true;
    }

    summarySection.hidden = false;
    resultsSection.hidden = false;
    filterInput.value = "";
    rebuildIndexes();
    refreshNamespaceSelectors();
    refreshDrillLevelSelectors(rootNamespaceSelect.value);
    refreshTypeSelector();
    refreshMemberSelector();
    renderAssemblies(currentAssemblies);

    statusText.textContent = `Completed for .NET ${payload.version}. Generated at ${new Date(payload.generatedAt).toLocaleString()}.${annotationLoadMessage}`;
  } catch (error) {
    statusText.textContent = error.message;
  } finally {
    introspectButton.disabled = false;
  }
}

introspectButton.addEventListener("click", introspect);
exportCsvButton.addEventListener("click", exportCsv);
filterInput.addEventListener("input", applyFilter);
rootNamespaceSelect.addEventListener("change", () => {
  selectedSegments = [];
  selectedType = "";
  selectedMember = "";
  refreshDrillLevelSelectors(rootNamespaceSelect.value);
  refreshTypeSelector();
  refreshMemberSelector();
  updateSelectedPathLabel();
  applyFilter();
});

typeSelector.addEventListener("change", () => {
  selectedType = typeSelector.value;
  selectedMember = "";
  refreshMemberSelector();
  updateSelectedPathLabel();
  applyFilter();
});

memberSelector.addEventListener("change", () => {
  selectedMember = memberSelector.value;
  updateSelectedPathLabel();
  applyFilter();
});

loadVersions();
