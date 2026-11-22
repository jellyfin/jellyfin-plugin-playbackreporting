const JellysticsConfig = {
  pluginUniqueId: "7f3c2a1e-9b84-4d6f-a2c5-3e1b8d9f6a40",
};

export default function (view) {
  const pathInput = view.querySelector("#pbrDbPath");
  const saveButton = view.querySelector("#saveButton");
  const importButton = view.querySelector("#importButton");
  const importStatus = view.querySelector("#importStatus");
  const purgeDate = view.querySelector("#purgeDate");
  const purgeButton = view.querySelector("#purgeButton");
  let pollTimer = null;

  function renderStatus(s) {
    if (s.state === "running") {
      importStatus.textContent = "Status: import running...";
    } else if (s.state === "done") {
      importStatus.textContent =
        "Status: done. read " + s.read + " / imported " + s.imported +
        " / skipped " + s.skipped + " / repaired " + s.repaired;
    } else if (s.state === "failed") {
      importStatus.textContent = "Status: failed. " + (s.error || "");
    } else {
      importStatus.textContent = "Status: idle";
    }
  }

  async function refreshStatus() {
    try {
      const s = await ApiClient.fetch({
        url: ApiClient.getUrl("Jellystics/Import/Status"),
        type: "GET",
        dataType: "json",
      });
      renderStatus(s);
      if (s.state === "running") {
        schedulePoll();
      }
    } catch (error) {
      console.error("Jellystics: cannot read the import status", error);
      importStatus.textContent = "Status: unavailable";
    }
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    pollTimer = setTimeout(refreshStatus, 2000);
  }

  function loadConfig() {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(JellysticsConfig.pluginUniqueId)
      .then(function (config) {
        pathInput.value = config.PbrDbPath || "";
      })
      .finally(Dashboard.hideLoadingMsg);
  }

  function saveConfig() {
    Dashboard.showLoadingMsg();
    return ApiClient.getPluginConfiguration(JellysticsConfig.pluginUniqueId)
      .then(function (config) {
        config.PbrDbPath = pathInput.value.trim();
        return ApiClient.updatePluginConfiguration(JellysticsConfig.pluginUniqueId, config);
      })
      .then(Dashboard.processPluginConfigurationUpdateResult)
      .finally(Dashboard.hideLoadingMsg);
  }

  async function errorText(error, fallback) {
    try {
      const body = await error.json();
      return body.error || fallback;
    } catch (e) {
      return fallback;
    }
  }

  saveButton.addEventListener("click", function (e) {
    e.preventDefault();
    saveConfig();
  });

  importButton.addEventListener("click", async function (e) {
    e.preventDefault();
    try {
      await saveConfig();
      await ApiClient.fetch({
        url: ApiClient.getUrl("Jellystics/Import/PlaybackReporting"),
        type: "POST",
      });
      importStatus.textContent = "Status: import running...";
      schedulePoll();
    } catch (error) {
      Dashboard.alert({ message: await errorText(error, "Could not start the import."), title: "Error" });
    }
  });

  purgeButton.addEventListener("click", async function (e) {
    e.preventDefault();
    if (!purgeDate.value) {
      Dashboard.alert({ message: "Choose a date first.", title: "Purge" });
      return;
    }
    if (!window.confirm("Permanently delete every play that started before " + purgeDate.value + "?")) {
      return;
    }
    const before = new Date(purgeDate.value + "T00:00:00").toISOString();
    try {
      const result = await ApiClient.fetch({
        url: ApiClient.getUrl("Jellystics/Purge"),
        type: "POST",
        contentType: "application/json",
        data: JSON.stringify({ before: before }),
        dataType: "json",
      });
      Dashboard.alert({ message: result.deleted + " play(s) deleted.", title: "Purge" });
    } catch (error) {
      Dashboard.alert({ message: await errorText(error, "Purge failed."), title: "Error" });
    }
  });

  view.addEventListener("viewshow", function () {
    loadConfig();
    refreshStatus();
  });

  view.addEventListener("viewhide", function () {
    clearTimeout(pollTimer);
  });
}
