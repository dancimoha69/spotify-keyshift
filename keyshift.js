// Key Shift: Spotify UI for the Windows audio companion.
// The companion captures only Spotify's process audio and performs the DSP.
(function () {
  const API = "http://127.0.0.1:43817";
  const MIN = -12;
  const MAX = 12;
  const BACKGROUND_DB = "keyshift-background";
  const BACKGROUND_STORE = "settings";
  const BACKGROUND_STYLE_ID = "keyshift-background-style";
  const NAVIGATION_STYLE_ID = "keyshift-navigation-style";
  let backgroundBlob = null;
  let backgroundUrl = null;
  let backgroundOpacity = 0.45;
  let backgroundFit = "stretch";
  let backgroundReady = Promise.resolve();
  let backgroundRouteTimer = null;
  let activeSettingsPage = null;
  let settingsMainView = null;
  let originalMainViewPosition = null;
  let changedMainViewPosition = false;
  let settingsKeyListener = null;
  let openingSettings = false;
  const BUTTON_ID = "keyshift-open-button";

  function syncBackgroundRoute() {
    const style = document.getElementById(BACKGROUND_STYLE_ID);
    if (!style) return;
    const route = `${window.location.pathname}${window.location.hash}${window.location.href}`;
    const isProfileRoute = /\/(?:user|profile|artist)\/[^/?#]+/i.test(route) || /spotify:(?:user|profile|artist):/i.test(route);
    style.media = isProfileRoute ? "not all" : "all";
  }

  function openBackgroundDb() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(BACKGROUND_DB, 1);
      request.onupgradeneeded = () => {
        if (!request.result.objectStoreNames.contains(BACKGROUND_STORE))
          request.result.createObjectStore(BACKGROUND_STORE);
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error || new Error("Не удалось открыть хранилище фона"));
    });
  }

  async function readBackgroundValue(key) {
    const db = await openBackgroundDb();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(BACKGROUND_STORE, "readonly");
      const request = transaction.objectStore(BACKGROUND_STORE).get(key);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error || new Error("Не удалось прочитать настройки фона"));
      transaction.oncomplete = () => db.close();
      transaction.onerror = () => { db.close(); reject(transaction.error); };
    });
  }

  async function writeBackgroundValue(key, value) {
    const db = await openBackgroundDb();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(BACKGROUND_STORE, "readwrite");
      transaction.objectStore(BACKGROUND_STORE).put(value, key);
      transaction.oncomplete = () => { db.close(); resolve(); };
      transaction.onerror = () => { db.close(); reject(transaction.error || new Error("Не удалось сохранить фон")); };
      transaction.onabort = () => { db.close(); reject(transaction.error || new Error("Сохранение фона отменено")); };
    });
  }

  async function deleteBackgroundValue(key) {
    const db = await openBackgroundDb();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(BACKGROUND_STORE, "readwrite");
      transaction.objectStore(BACKGROUND_STORE).delete(key);
      transaction.oncomplete = () => { db.close(); resolve(); };
      transaction.onerror = () => { db.close(); reject(transaction.error || new Error("Не удалось удалить фон")); };
      transaction.onabort = () => { db.close(); reject(transaction.error || new Error("Удаление фона отменено")); };
    });
  }

  function applyBackground(blob, opacity = backgroundOpacity) {
    const root = document.documentElement;
    if (!blob) {
      if (backgroundUrl) URL.revokeObjectURL(backgroundUrl);
      backgroundUrl = null;
      backgroundBlob = null;
      root.style.removeProperty("--keyshift-background-image");
      root.style.removeProperty("--keyshift-background-shade");
      root.style.removeProperty("--keyshift-background-size");
      document.getElementById(BACKGROUND_STYLE_ID)?.remove();
      document.getElementById(NAVIGATION_STYLE_ID)?.remove();
      if (backgroundRouteTimer !== null) {
        window.clearInterval(backgroundRouteTimer);
        backgroundRouteTimer = null;
      }
      return;
    }

    if (blob !== backgroundBlob) {
      if (backgroundUrl) URL.revokeObjectURL(backgroundUrl);
      backgroundBlob = blob;
      backgroundUrl = URL.createObjectURL(blob);
    }

    let style = document.getElementById(BACKGROUND_STYLE_ID);
    if (!style) {
      style = document.createElement("style");
      style.id = BACKGROUND_STYLE_ID;
      document.head.appendChild(style);
    }
    let navigationStyle = document.getElementById(NAVIGATION_STYLE_ID);
    if (!navigationStyle) {
      navigationStyle = document.createElement("style");
      navigationStyle.id = NAVIGATION_STYLE_ID;
      navigationStyle.textContent = `#root { position: relative !important; z-index: 1 !important; background: transparent !important; }`;
      document.head.appendChild(navigationStyle);
    }
    style.textContent = `
      body::before {
        content: none !important;
      }
      #main-view :is(div, section, article, header, footer, nav, main):not([role="button"]):not([role="tab"]):not([role="switch"]):not([role="slider"]):not([data-testid*="artwork"]) {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main-view :is(div, section, article, header, footer, nav, main):not([role="button"]):not([role="tab"]):not([role="switch"]):not([role="slider"]):not([data-testid*="artwork"])::before,
      #main-view :is(div, section, article, header, footer, nav, main):not([role="button"]):not([role="tab"]):not([role="switch"]):not([role="slider"]):not([data-testid*="artwork"])::after {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main, .Root__top-container, .Root__nav-bar,
      .Root__right-sidebar, .main-view-container__scroll-node,
      .main-view-container__scroll-node-child {
        background-color: rgba(18, 18, 18, .12) !important;
        background-image: none !important;
      }
      #main-view {
        background-color: rgba(18, 18, 18, .20) !important;
        background-image: linear-gradient(rgba(18, 18, 18, var(--keyshift-background-shade, .55)), rgba(18, 18, 18, var(--keyshift-background-shade, .55))), var(--keyshift-background-image, none) !important;
        background-position: center, center !important;
        background-size: 100% 100%, var(--keyshift-background-size, 100% 100%) !important;
        background-repeat: no-repeat, no-repeat !important;
        background-attachment: scroll, scroll !important;
      }
      #main-view > * {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main-view > *::before,
      #main-view > *::after {
        background-color: transparent !important;
        background-image: none !important;
      }
      .main-view-container {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main-view [data-testid="entity-header"] {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main-view [data-testid="entity-header"] .hbNOZbHD3bPL3OcHQWNn,
      #main-view [data-testid="entity-header"] .uP1rcUSIWCFaOZaoH0ZG,
      #main-view [data-testid="entity-header"] .nfBy1HoO33kgPOyEc3WZ,
      #main-view [data-testid="overscroll-background"] {
        background-color: transparent !important;
        background-image: none !important;
      }
      #main-view .t_mbBtGZ5QtvpsVNOtOj {
        background-color: transparent !important;
        background-image: none !important;
      }
      /* Playlist layout paints an opaque base behind its action bar and track header. */
      #main-view .fmJtSKRbaIwuGI4eOWBt {
        background: transparent !important;
      }
      /* Spotify 1.3.5 paints the home-page purple wash on a separate layer. */
      #main-view .kMaYBahSBLOnErF7DLR2 {
        background-color: transparent !important;
        background-image: none !important;
        opacity: 0 !important;
      }
      #main-view .xCEkyVpabf3oVGmFQVej,
      #main-view .main-view-container__mh-footer-container {
        background-color: transparent !important;
        background-image: none !important;
      }
      .Root__now-playing-bar {
        background-color: rgba(18, 18, 18, .86) !important;
      }
    `;
    syncBackgroundRoute();
    if (backgroundRouteTimer === null)
      backgroundRouteTimer = window.setInterval(syncBackgroundRoute, 500);
    root.style.setProperty("--keyshift-background-image", `url(${JSON.stringify(backgroundUrl)})`);
    root.style.setProperty("--keyshift-background-shade", String(1 - Math.max(0.1, Math.min(0.9, opacity))));
    root.style.setProperty("--keyshift-background-size", backgroundFit === "stretch" ? "100% 100%" : backgroundFit);
  }

  async function restoreBackground() {
    try {
      const [blob, opacity, fit] = await Promise.all([
        readBackgroundValue("image"),
        readBackgroundValue("opacity"),
        readBackgroundValue("fit"),
      ]);
      if (typeof opacity === "number") backgroundOpacity = Math.max(0.1, Math.min(0.9, opacity));
      // Migrate the old crop/letterbox choices to the requested full, uncropped view.
      backgroundFit = "stretch";
      if (fit !== "stretch") void writeBackgroundValue("fit", backgroundFit).catch((error) => {
        console.warn("Key Shift: could not save the uncropped background mode", error);
      });
      if (blob instanceof Blob && blob.type.startsWith("image/")) applyBackground(blob);
    } catch (error) {
      console.warn("Key Shift: background settings could not be restored", error);
    }
  }

  async function request(path, options = {}) {
    const response = await fetch(`${API}${path}`, {
      cache: "no-store",
      ...options,
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.message || `HTTP ${response.status}`);
    return data;
  }

  function closeControls() {
    if (settingsKeyListener) {
      document.removeEventListener("keydown", settingsKeyListener, true);
      settingsKeyListener = null;
    }
    activeSettingsPage?.remove();
    activeSettingsPage = null;
    if (settingsMainView && changedMainViewPosition && settingsMainView.style.position === "relative")
      settingsMainView.style.position = originalMainViewPosition || "";
    settingsMainView = null;
    originalMainViewPosition = null;
    changedMainViewPosition = false;
    document.getElementById(BUTTON_ID)?.setAttribute("aria-expanded", "false");
  }

  function showControls(panel) {
    closeControls();
    const mainView = document.querySelector('[data-testid="main-view-container"]') || document.querySelector("main");
    const page = document.createElement("section");
    page.id = "keyshift-settings-page";
    page.setAttribute("aria-label", "Настройки Key Shift");
    page.style.cssText = `position:${mainView ? "absolute" : "fixed"};${mainView ? "inset:0" : "inset:64px 0 90px"};z-index:1000;overflow:auto;overscroll-behavior:contain;box-sizing:border-box;background:#121212!important;background-image:none!important;opacity:1!important;backdrop-filter:none!important;color:#fff;font:14px/1.5 Arial,sans-serif`;

    if (mainView) {
      if (getComputedStyle(mainView).position === "static") {
        originalMainViewPosition = mainView.style.position;
        mainView.style.position = "relative";
        changedMainViewPosition = true;
      }
      settingsMainView = mainView;
    }

    const content = document.createElement("div");
    content.style.cssText = "box-sizing:border-box;width:min(820px,100%);margin:0 auto;padding:32px clamp(20px,5vw,48px) 48px";

    const header = document.createElement("header");
    header.style.cssText = "position:sticky;top:0;z-index:5;display:flex;align-items:center;justify-content:space-between;gap:16px;padding:16px 0;background:#121212!important;opacity:1!important;border-bottom:1px solid #333";
    const titleGroup = document.createElement("div");
    const eyebrow = document.createElement("p");
    eyebrow.textContent = "НАСТРОЙКИ ПЛЕЕРА";
    eyebrow.style.cssText = "margin:0 0 4px;color:#1ed760;font-size:11px;font-weight:700;letter-spacing:.12em";
    const title = document.createElement("h1");
    title.textContent = "Key Shift";
    title.style.cssText = "margin:0;font-size:30px;line-height:1.2;font-weight:700";
    titleGroup.append(eyebrow, title);

    const back = document.createElement("button");
    back.type = "button";
    back.textContent = "←  Назад в Spotify";
    back.setAttribute("aria-label", "Вернуться к Spotify");
    back.style.cssText = "border:0;border-radius:999px;padding:10px 14px;background:#282828;color:#fff;font:inherit;font-weight:600;cursor:pointer";
    back.addEventListener("click", closeControls);
    header.append(titleGroup, back);

    panel.style.cssText = "box-sizing:border-box;width:100%;max-width:680px;margin-top:24px;padding:22px 24px;border-radius:8px;background:#181818!important;background-image:none!important;opacity:1!important;color:#fff;font:14px/1.5 Arial,sans-serif";
    content.append(header, panel);
    page.append(content);
    activeSettingsPage = page;
    document.getElementById(BUTTON_ID)?.setAttribute("aria-expanded", "true");
    settingsKeyListener = (event) => { if (event.key === "Escape") closeControls(); };
    document.addEventListener("keydown", settingsKeyListener, true);
    (mainView || document.body).append(page);
    back.focus();
  }

  function makeKeyShiftButton() {
    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.title = "Key Shift";
    button.setAttribute("aria-label", "Открыть Key Shift");
    button.setAttribute("aria-expanded", "false");
    button.setAttribute("data-testid", "keyshift-open-button");
    button.style.cssText = "display:inline-flex;align-items:center;justify-content:center;flex:0 0 32px;width:32px;height:32px;margin:0 8px;border:0;border-radius:50%;background:transparent;color:#b3b3b3;cursor:pointer;transition:color .15s,background .15s";
    button.innerHTML = '<svg viewBox="0 0 24 24" width="19" height="19" aria-hidden="true" fill="currentColor"><path d="M4 7h9v2H4V7zm0 8h6v2H4v-2zm13-9h2v4h-2V6zm-3 5h8v2h-8v-2zm3 4h2v4h-2v-4zM9 5h2v6H9V5zm5 8h2v6h-2v-6z"/></svg>';
    button.addEventListener("mouseenter", () => { button.style.color = "#fff"; button.style.background = "rgba(255,255,255,.08)"; });
    button.addEventListener("mouseleave", () => { button.style.color = "#b3b3b3"; button.style.background = "transparent"; });
    button.addEventListener("click", openControls);
    return button;
  }

  function ensureKeyShiftButton() {
    if (document.getElementById(BUTTON_ID)) return;
    const playPause = document.querySelector('[data-testid="control-button-playpause"]');
    const controls = playPause?.parentElement;
    if (!controls) return;
    controls.insertBefore(makeKeyShiftButton(), playPause.nextSibling);
  }

  async function openControls() {
    if (activeSettingsPage) {
      closeControls();
      return;
    }
    if (openingSettings) return;
    openingSettings = true;
    await backgroundReady;
    const panel = document.createElement("div");
    panel.style.cssText = "padding:4px 0 2px;min-width:300px;color:#fff;font:14px Arial,sans-serif";

    const intro = document.createElement("p");
    intro.textContent = "Меняет тональность Spotify без изменения темпа.";
    intro.style.cssText = "margin:0 0 16px;color:#b3b3b3;line-height:1.45";

    const toggleRow = document.createElement("label");
    toggleRow.style.cssText = "display:flex;align-items:center;gap:10px;cursor:pointer;font-weight:600";
    const toggle = document.createElement("input");
    toggle.type = "checkbox";
    toggle.style.cssText = "width:18px;height:18px;accent-color:#1ed760";
    const toggleLabel = document.createElement("span");
    toggleLabel.textContent = "Обработка звука Spotify";
    toggleRow.append(toggle, toggleLabel);

    const row = document.createElement("div");
    row.style.cssText = "display:flex;align-items:center;justify-content:center;gap:18px;margin-top:18px";

    const value = document.createElement("strong");
    value.style.cssText = "min-width:130px;text-align:center;font-size:16px";

    const makeButton = (text, label, action) => {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = text;
      button.setAttribute("aria-label", label);
      button.style.cssText = "border:0;border-radius:999px;background:rgba(255,255,255,.1);color:#1ed760;width:40px;height:40px;font-size:20px;cursor:pointer";
      button.addEventListener("click", action);
      return button;
    };

    const slider = document.createElement("input");
    slider.type = "range";
    slider.min = String(MIN);
    slider.max = String(MAX);
    slider.step = "1";
    slider.style.cssText = "width:100%;margin-top:16px;accent-color:#1ed760";

    const status = document.createElement("p");
    status.style.cssText = "margin:14px 0 0;text-align:center;color:#b3b3b3;font-size:12px;line-height:1.5;min-height:32px";

    const backgroundSection = document.createElement("section");
    backgroundSection.style.cssText = "margin-top:18px;padding-top:14px;border-top:1px solid #444";
    const backgroundTitle = document.createElement("strong");
    backgroundTitle.textContent = "Фон Spotify";
    backgroundTitle.style.cssText = "display:block;margin-bottom:10px";

    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = "image/*";
    fileInput.style.display = "none";

    const backgroundButtons = document.createElement("div");
    backgroundButtons.style.cssText = "display:flex;gap:8px";
    const makeActionButton = (text) => {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = text;
      button.style.cssText = "flex:1;border:0;border-radius:8px;padding:9px 10px;background:rgba(255,255,255,.1);color:#fff;font:inherit;cursor:pointer";
      return button;
    };
    const chooseBackground = makeActionButton("Выбрать изображение");
    const clearBackground = makeActionButton("Убрать фон");
    clearBackground.style.flex = "0 0 auto";
    chooseBackground.addEventListener("click", () => fileInput.click());

    const backgroundName = document.createElement("p");
    backgroundName.textContent = "Фон не выбран";
    backgroundName.style.cssText = "margin:8px 0;color:#b3b3b3;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap";

    const opacityLabel = document.createElement("label");
    opacityLabel.style.cssText = "display:flex;align-items:center;justify-content:space-between;gap:12px;font-size:12px;color:#b3b3b3";
    const opacityText = document.createElement("span");
    opacityText.textContent = "Яркость изображения";
    const opacityValue = document.createElement("span");
    const opacitySlider = document.createElement("input");
    opacitySlider.type = "range";
    opacitySlider.min = "10";
    opacitySlider.max = "90";
    opacitySlider.step = "5";
    opacitySlider.value = String(Math.round(backgroundOpacity * 100));
    opacitySlider.style.cssText = "width:100%;margin:6px 0 0;accent-color:#1ed760";
    opacityLabel.append(opacityText, opacityValue);
    const updateOpacityLabel = () => { opacityValue.textContent = `${opacitySlider.value}%`; };
    updateOpacityLabel();
    if (backgroundBlob) backgroundName.textContent = `Фон: ${backgroundBlob.name || "сохранённое изображение"}`;

    const fitLabel = document.createElement("label");
    fitLabel.textContent = "Масштаб изображения";
    fitLabel.style.cssText = "display:flex;align-items:center;justify-content:space-between;gap:12px;margin-top:10px;font-size:12px;color:#b3b3b3";
    const fitSelect = document.createElement("select");
    fitSelect.style.cssText = "border:0;border-radius:6px;padding:6px 8px;background:#3a3a3a;color:#fff;font:inherit";
    for (const [fitValue, label] of [["stretch", "Заполнить без обрезки"], ["contain", "Вписать целиком"], ["cover", "Заполнить с обрезкой"]]) {
      const option = document.createElement("option");
      option.value = fitValue;
      option.textContent = label;
      fitSelect.appendChild(option);
    }
    fitSelect.value = backgroundFit;
    fitSelect.addEventListener("change", () => {
      backgroundFit = fitSelect.value;
      if (backgroundBlob) applyBackground(backgroundBlob);
      void writeBackgroundValue("fit", backgroundFit).catch((error) => {
        backgroundName.textContent = `Не удалось сохранить масштаб: ${error.message}`;
      });
    });
    fitLabel.appendChild(fitSelect);

    let opacitySaveQueue = Promise.resolve();
    opacitySlider.addEventListener("input", () => {
      backgroundOpacity = Number(opacitySlider.value) / 100;
      updateOpacityLabel();
      if (backgroundBlob) applyBackground(backgroundBlob);
      opacitySaveQueue = opacitySaveQueue
        .then(() => writeBackgroundValue("opacity", backgroundOpacity))
        .catch((error) => { backgroundName.textContent = `Не удалось сохранить яркость: ${error.message}`; });
    });

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files?.[0];
      fileInput.value = "";
      if (!file) return;
      if (!file.type.startsWith("image/")) {
        backgroundName.textContent = "Выбери файл изображения (GIF, PNG, JPG, WEBP и т. п.)";
        return;
      }
      if (file.size > 50 * 1024 * 1024) {
        backgroundName.textContent = "Файл больше 50 МБ. Выбери изображение поменьше.";
        return;
      }
      chooseBackground.disabled = true;
      backgroundName.textContent = "Сохраняю изображение…";
      try {
        const preview = new Image();
        const previewUrl = URL.createObjectURL(file);
        preview.src = previewUrl;
        await preview.decode();
        URL.revokeObjectURL(previewUrl);
        await writeBackgroundValue("image", file);
        applyBackground(file);
        backgroundName.textContent = `Фон: ${file.name}`;
      } catch (error) {
        backgroundName.textContent = `Не получилось загрузить изображение: ${error.message}`;
      } finally {
        chooseBackground.disabled = false;
      }
    });

    clearBackground.addEventListener("click", async () => {
      clearBackground.disabled = true;
      try {
        await deleteBackgroundValue("image");
        applyBackground(null);
        backgroundName.textContent = "Фон не выбран";
      } catch (error) {
        backgroundName.textContent = `Не удалось убрать фон: ${error.message}`;
      } finally {
        clearBackground.disabled = false;
      }
    });
    backgroundButtons.append(chooseBackground, clearBackground);
    backgroundSection.append(backgroundTitle, fileInput, backgroundButtons, backgroundName, opacityLabel, opacitySlider, fitLabel);

    let semitones = 0;
    let pitchQueue = Promise.resolve();
    const updateValue = () => {
      value.textContent = semitones === 0 ? "0 · исходная" : `${semitones > 0 ? "+" : ""}${semitones} полутонов`;
      slider.value = String(semitones);
    };

    const sendPitch = async () => {
      const requested = semitones;
      pitchQueue = pitchQueue.then(() => request("/pitch", {
        method: "POST",
        body: JSON.stringify({ semitones: requested }),
      })).catch((error) => {
        status.textContent = `Не удалось связаться с обработчиком: ${error.message}`;
      });
      await pitchQueue;
    };

    const setPitch = (next) => {
      semitones = Math.max(MIN, Math.min(MAX, next));
      updateValue();
      void sendPitch();
    };

    row.append(
      makeButton("−", "Ниже на полутон", () => setPitch(semitones - 1)),
      value,
      makeButton("+", "Выше на полутон", () => setPitch(semitones + 1)),
    );

    slider.addEventListener("input", () => setPitch(Number(slider.value)));
    toggle.addEventListener("change", async () => {
      toggle.disabled = true;
      status.textContent = toggle.checked ? "Подключаю звук Spotify…" : "Отключаю обработку…";
      try {
        const data = await request("/enabled", {
          method: "POST",
          body: JSON.stringify({ enabled: toggle.checked }),
        });
        toggle.checked = data.enabled;
        status.textContent = data.message;
      } catch (error) {
        toggle.checked = false;
        status.textContent = `Фоновый обработчик не отвечает: ${error.message}`;
      } finally {
        toggle.disabled = false;
      }
    });

    panel.append(intro, toggleRow, row, slider, status, backgroundSection);
    showControls(panel);
    openingSettings = false;

    updateValue();
    request("/status")
      .then((data) => {
        semitones = data.semitones;
        toggle.checked = data.enabled;
        updateValue();
        status.textContent = data.message;
      })
      .catch(() => {
        status.textContent = "Аудиообработчик ещё запускается. Подожди немного и повтори.";
      });
  }

  backgroundReady = restoreBackground();
  ensureKeyShiftButton();
  const uiObserver = new MutationObserver(() => {
    if (!document.getElementById(BUTTON_ID)) requestAnimationFrame(ensureKeyShiftButton);
  });
  uiObserver.observe(document.documentElement, { childList: true, subtree: true });
})();
