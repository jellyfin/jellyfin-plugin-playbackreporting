// Pure helpers of the injected « Mes statistiques » script. Run: node --test 'Jellyfin.Plugin.Jellystics.Tests/js/*.test.js'
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const client = require(path.join(__dirname, '..', '..', 'Jellyfin.Plugin.Jellystics', 'Me', 'client.js'));

test('basePath is the part of the pathname before /web/', () => {
  assert.equal(client.basePath('/web/index.html'), '');
  assert.equal(client.basePath('/web/'), '');
  assert.equal(client.basePath('/web'), '');
  assert.equal(client.basePath('/jellyfin/web/index.html'), '/jellyfin');
  assert.equal(client.basePath('/media/jellyfin/web/'), '/media/jellyfin');
  assert.equal(client.basePath('/Jellyfin/WEB/index.html'), '/Jellyfin');
  assert.equal(client.basePath('/'), '');
  assert.equal(client.basePath(''), '');
  assert.equal(client.basePath(undefined), '');
});

test('targetUrl points at the plugin page under the base path', () => {
  assert.equal(client.targetUrl('/web/index.html'), '/Jellystics/Me/');
  assert.equal(client.targetUrl('/jellyfin/web/'), '/jellyfin/Jellystics/Me/');
});

test('pickLang prefers the page language, then the browser, en by default', () => {
  assert.equal(client.pickLang('fr', 'en-US'), 'fr');
  assert.equal(client.pickLang('fr-FR', 'en-US'), 'fr');
  assert.equal(client.pickLang('es_ES', 'fr'), 'es');
  assert.equal(client.pickLang('', 'es-MX'), 'es');
  assert.equal(client.pickLang(null, 'fr-CA'), 'fr');
  assert.equal(client.pickLang('de', 'fr'), 'fr');
  assert.equal(client.pickLang('de', 'it'), 'en');
  assert.equal(client.pickLang(undefined, undefined), 'en');
  assert.equal(client.pickLang('EN-gb', 'fr'), 'en');
});

test('labelFor gives the localized entry label', () => {
  assert.equal(client.labelFor('fr', ''), 'Mes statistiques');
  assert.equal(client.labelFor('en-US', ''), 'My statistics');
  assert.equal(client.labelFor('es', ''), 'Mis estadísticas');
  assert.equal(client.labelFor('ja', 'ko'), 'My statistics');
});

test('loading the script without a DOM does not throw nor touch globals', () => {
  assert.equal(typeof globalThis.__jellysticsMe, 'undefined');
});
