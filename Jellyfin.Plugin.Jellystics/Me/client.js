/*
 * Jellystics « Mes statistiques »: adds an entry to jellyfin-web's avatar menu (#app-user-menu, 2nd position,
 * after Profile) and to the legacy #/mypreferencesmenu page, leading to <basePath>/Jellystics/Me/.
 * Plain ES2017, no build step. Idempotent and guarded: it must never break jellyfin-web.
 * The pure helpers are exported for `node --test` (Jellyfin.Plugin.Jellystics.Tests/js).
 */
(function () {
  'use strict';

  var FLAG = 'data-jellystics-me';
  var LABELS = { fr: 'Mes statistiques', en: 'My statistics', es: 'Mis estadísticas' };
  // Material "BarChart" icon.
  var BAR_CHART_PATH = 'M4 9h4v11H4zm12 4h4v7h-4zm-6-9h4v16h-4z';

  /** Server base path: the part of the pathname before "/web/" ("" or e.g. "/jellyfin"). */
  function basePath(pathname) {
    var p = typeof pathname === 'string' ? pathname : '';
    var lower = p.toLowerCase();
    var i = lower.indexOf('/web/');
    if (i >= 0) return p.slice(0, i);
    if (lower.length >= 4 && lower.slice(-4) === '/web') return p.slice(0, -4);
    return '';
  }

  /** URL of the statistics page for a jellyfin-web pathname. */
  function targetUrl(pathname) {
    return basePath(pathname) + '/Jellystics/Me/';
  }

  /** fr, en or es from the page language, then the browser language; en by default. */
  function pickLang(docLang, navLang) {
    var candidates = [docLang, navLang];
    for (var i = 0; i < candidates.length; i++) {
      var c = candidates[i];
      if (typeof c !== 'string' || !c) continue;
      var primary = c.trim().toLowerCase().split(/[-_]/)[0];
      if (Object.prototype.hasOwnProperty.call(LABELS, primary)) return primary;
    }
    return 'en';
  }

  function labelFor(docLang, navLang) {
    return LABELS[pickLang(docLang, navLang)];
  }

  var api = { basePath: basePath, targetUrl: targetUrl, pickLang: pickLang, labelFor: labelFor, LABELS: LABELS, FLAG: FLAG };

  if (typeof module === 'object' && module && module.exports) module.exports = api;

  if (typeof document === 'undefined' || typeof window === 'undefined') return;
  if (window.__jellysticsMe) return; // loaded twice
  window.__jellysticsMe = api;

  function currentLabel() {
    var docLang = document.documentElement ? document.documentElement.getAttribute('lang') : '';
    return labelFor(docLang, navigator.language);
  }

  function setLabel(item, text) {
    var target = item.querySelector('.MuiListItemText-root .MuiTypography-root')
      || item.querySelector('.MuiListItemText-root')
      || item.querySelector('.listItemBodyText');
    if (target) {
      target.textContent = text;
      return;
    }
    var walker = document.createTreeWalker(item, NodeFilter.SHOW_TEXT, null);
    var node;
    while ((node = walker.nextNode())) {
      if (node.nodeValue && node.nodeValue.trim()) {
        node.nodeValue = text;
        return;
      }
    }
  }

  function setIcon(item) {
    var svg = item.querySelector('svg');
    if (svg) {
      while (svg.firstChild) svg.removeChild(svg.firstChild);
      var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      path.setAttribute('d', BAR_CHART_PATH);
      svg.appendChild(path);
      svg.setAttribute('viewBox', '0 0 24 24');
      svg.setAttribute('data-testid', 'BarChartIcon');
      return;
    }
    var font = item.querySelector('.material-icons');
    if (font) {
      // jellyfin-web's icon font: the icon name is a class (legacy pages), or the text.
      font.classList.remove('person');
      font.classList.add('bar_chart');
      if (font.textContent && font.textContent.trim()) font.textContent = 'bar_chart';
    }
  }

  function closeMenu(menu) {
    try {
      var target = menu.querySelector('[role="menu"]') || menu;
      target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }));
    } catch (e) { /* ignore */ }
    try {
      var backdrop = menu.querySelector('.MuiBackdrop-root');
      if (backdrop) backdrop.click();
    } catch (e) { /* ignore */ }
  }

  function makeEntry(source, onBeforeNavigate) {
    var entry = source.cloneNode(true);
    entry.setAttribute(FLAG, '1');
    entry.removeAttribute('id');
    var withIds = entry.querySelectorAll('[id]');
    for (var i = 0; i < withIds.length; i++) withIds[i].removeAttribute('id');
    entry.classList.remove('Mui-focusVisible', 'Mui-selected', 'lnkUserProfile');
    var url = targetUrl(location.pathname);
    if (entry.tagName === 'A') entry.setAttribute('href', url);
    else entry.removeAttribute('href');
    entry.setAttribute('tabindex', '-1');
    setLabel(entry, currentLabel());
    setIcon(entry);
    entry.addEventListener('click', function (e) {
      try {
        // Plain left click: our own navigation; modified clicks keep the native link behaviour.
        if (entry.tagName === 'A' && (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey)) return;
        e.preventDefault();
        e.stopPropagation();
        if (onBeforeNavigate) onBeforeNavigate();
      } catch (err) { /* ignore */ }
      location.href = url;
    });
    entry.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && entry.tagName !== 'A') {
        e.preventDefault();
        entry.click();
      }
    });
    return entry;
  }

  /** Avatar menu (MUI): clone the Profile item, insert the clone right after it. */
  function ensureMenu() {
    var menu = document.getElementById('app-user-menu');
    if (!menu || menu.querySelector('[' + FLAG + ']')) return;
    var link = menu.querySelector('ul[role="menu"] a[href*="userprofile"]') || menu.querySelector('a[href*="userprofile"]');
    if (!link) return;
    var item = (link.closest && link.closest('.MuiMenuItem-root')) || link;
    if (!item.parentNode) return;
    var entry = makeEntry(item, function () { closeMenu(menu); });
    item.parentNode.insertBefore(entry, item.nextSibling);
  }

  /** Legacy settings page (#/mypreferencesmenu): add the entry after the profile link. */
  function ensureLegacyPage() {
    var links = document.querySelectorAll('a.lnkUserProfile');
    for (var i = 0; i < links.length; i++) {
      var link = links[i];
      var next = link.nextElementSibling;
      if (next && next.hasAttribute(FLAG)) continue;
      if (!link.parentNode) continue;
      link.parentNode.insertBefore(makeEntry(link, null), link.nextSibling);
    }
  }

  function run() {
    try { ensureMenu(); } catch (e) { /* never break jellyfin-web */ }
    try { ensureLegacyPage(); } catch (e) { /* never break jellyfin-web */ }
  }

  var scheduled = false;
  function schedule() {
    if (scheduled) return;
    scheduled = true;
    setTimeout(function () {
      scheduled = false;
      run();
    }, 0);
  }

  function start() {
    try {
      run();
      new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    } catch (e) { /* ignore */ }
  }

  try {
    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start, { once: true });
  } catch (e) { /* ignore */ }
})();
