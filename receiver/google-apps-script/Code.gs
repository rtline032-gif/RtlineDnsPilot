/* RtlineDnsPilot receiver, protocols 1/2. Only a fixed script-created file is writable. */
const LIMITS_ = Object.freeze({ Body: 96 * 1024, Profiles: 32, Attempts: 5, DailyRows: 100, Rows: 1000,
  Bytes: 256 * 1024, Invites: 200, PublicMinuteRequests: 10, PublicDailyRequests: 200 });
const CATALOG_MARKER_ = 'rtlineDnsPilotSharedDnsV1';
const BASE_PROFILES_ = [
  ['Doh', ['111.88.96.54','111.88.96.55'],53,'https://xbox-dns.ru/dns-query'],
  ['Doh', ['181.215.40.49','31.58.216.201'],53,'https://dns.bezmezhau.com/dns-query'],
  ['Doh', ['194.87.196.13','45.151.102.219','2a00:b700:0005:0000:0000:0000:0003:0062','2a12:5940:d4ef:0000:0000:0000:0000:0219'],53,'https://dns.dns-ai.ru/dns-query'],
  ['Udp', ['111.88.96.54','111.88.96.55'],53,''],
  ['Udp', ['181.215.40.49','31.58.216.201'],53,''],
  ['Doh', ['195.133.25.16'],53,'https://dns.comss.one/dns-query'],
  ['Doh', ['83.220.169.155','212.109.195.93'],53,'https://dns.comss.one/dns-query'],
  ['Udp', ['83.220.169.155','212.109.195.93'],53,''],
  ['Udp', ['159.194.200.33','37.230.192.51'],53,''],
  ['Doh', ['193.233.244.38'],53,'https://dns.dagami.ru/dns-query'],
  ['Udp', ['193.233.244.38'],53,''],
  ['Doh', ['83.217.211.149'],53,'https://dns.zapret.moe/dns-query'],
  ['Udp', ['83.217.211.149'],53,''],
  ['Doh', ['179.254.166.171'],53,'https://dns.malw.link/dns-query'],
  ['Udp', ['179.254.166.171','164.5.224.81'],53,'']
];

// Run these owner helpers in the Apps Script editor. None is exposed by doPost/doGet.
function setupCatalog() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try {
    const props = PropertiesService.getScriptProperties();
    let id = props.getProperty('CATALOG_FILE_ID');
    if (!id) {
      const file = Drive.Files.create({ name: 'RtlineDnsPilot-shared-dns.json', mimeType: 'application/json',
        appProperties: { [CATALOG_MARKER_]: '1' } }, Utilities.newBlob('[]', 'application/json'), { fields: 'id' });
      id = file.id;
      props.setProperty('CATALOG_FILE_ID', id);
    }
    props.setProperty('RECEIVER_ENABLED', '1');
    // Setup is safe by default, including after a previous public deployment was disabled.
    props.setProperty('PUBLIC_SUBMISSIONS_ENABLED', '0');
    console.log('Catalog: https://drive.google.com/file/d/' + id + '/view');
  } finally { lock.releaseLock(); }
}

function enablePublicSubmissions() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try {
    const props = PropertiesService.getScriptProperties();
    const id = props.getProperty('CATALOG_FILE_ID');
    if (props.getProperty('RECEIVER_ENABLED') !== '1' || !id) throw new Error('Run setupCatalog first.');
    readCatalog_(id); // Refuse to expose a missing, foreign or corrupt catalog.
    props.setProperty('PUBLIC_SUBMISSIONS_ENABLED', '1');
    console.log('Public submissions enabled with global limits. No invitation is required.');
  } finally { lock.releaseLock(); }
}

function disablePublicSubmissions() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try { PropertiesService.getScriptProperties().setProperty('PUBLIC_SUBMISSIONS_ENABLED', '0'); }
  finally { lock.releaseLock(); }
}

function createInvite() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try {
    const props = PropertiesService.getScriptProperties();
    if (!props.getProperty('CATALOG_FILE_ID')) throw new Error('Run setupCatalog first.');
    if (Object.keys(props.getProperties()).filter(k => k.indexOf('INVITE_') === 0).length >= LIMITS_.Invites)
      throw new Error('Invite capacity reached.');
    // getUuid is Java UUID.randomUUID; three independent UUIDs feed a 256-bit token.
    const code = sha256_(Utilities.getUuid() + '\n' + Utilities.getUuid() + '\n' + Utilities.getUuid());
    const hash = sha256_(code);
    props.setProperty('INVITE_' + hash, JSON.stringify({ Day: '', Attempts: 0, Revoked: false }));
    console.log('ONE-TIME invite code: ' + code + '\nRevoke hash: ' + hash);
  } finally { lock.releaseLock(); }
}

function revokeInvite() {
  const props = PropertiesService.getScriptProperties();
  const hash = props.getProperty('REVOKE_INVITE_HASH');
  if (!/^[a-f0-9]{64}$/.test(hash || '')) throw new Error('Set REVOKE_INVITE_HASH to the hash from createInvite.');
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try {
    if (!props.getProperty('INVITE_' + hash)) throw new Error('Unknown invite hash.');
    props.setProperty('INVITE_' + hash, JSON.stringify({ Day: '', Attempts: 0, Revoked: true }));
    props.deleteProperty('REVOKE_INVITE_HASH');
    console.log('Invite revoked.');
  } finally { lock.releaseLock(); }
}

function disableReceiver() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) throw new Error('Receiver busy.');
  try {
    const props = PropertiesService.getScriptProperties();
    props.setProperty('RECEIVER_ENABLED', '0');
    props.setProperty('PUBLIC_SUBMISSIONS_ENABLED', '0');
  }
  finally { lock.releaseLock(); }
}
function doGet(e) {
  if (e && (e.queryString || e.pathInfo || Object.keys(e.parameters || {}).length)) return reply_(false, 0, 0, 'invalid_request', 2);
  const props = PropertiesService.getScriptProperties();
  const enabled = props.getProperty('PUBLIC_SUBMISSIONS_ENABLED') === '1';
  // Configuration readiness only: no Drive reads, IDs, credentials or account data.
  return json_({ Service: 'RtlineDnsPilot', Protocol: 2, PublicEnabled: enabled,
    Ready: enabled && props.getProperty('RECEIVER_ENABLED') === '1' && !!props.getProperty('CATALOG_FILE_ID') });
}

function doPost(e) {
  let lock;
  let protocol = 1;
  try {
    if (!e || e.queryString || e.pathInfo || Object.keys(e.parameters || {}).length || !e.postData
        || !/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(e.postData.type || '')) fail_('invalid_request');
    const body = e.postData.contents;
    if (typeof body !== 'string' || body.length > LIMITS_.Body || byteSize_(body) > LIMITS_.Body) fail_('invalid_request');
    let request;
    try { request = JSON.parse(body); } catch (_) { fail_('invalid_request'); }
    if (request && request.Protocol === 2) protocol = 2;
    exactKeys_(request, protocol === 2 ? ['Protocol','Profiles'] : ['Protocol','InviteCode','Profiles']);
    if (request.Protocol !== protocol || protocol === 1 && (typeof request.InviteCode !== 'string' || !/^[a-f0-9]{64}$/.test(request.InviteCode))
        || !Array.isArray(request.Profiles) || request.Profiles.length < 1 || request.Profiles.length > LIMITS_.Profiles) fail_('invalid_request');
    const props = PropertiesService.getScriptProperties();
    const inviteKey = protocol === 1 ? 'INVITE_' + sha256_(request.InviteCode) : null;
    if (inviteKey && !props.getProperty(inviteKey)) fail_('denied');
    lock = LockService.getScriptLock();
    if (!lock.tryLock(5000)) { lock = null; fail_('busy'); }
    const invite = inviteKey ? parseState_(props.getProperty(inviteKey)) : null;
    if (invite && invite.Revoked !== false) fail_('denied');
    if (props.getProperty('RECEIVER_ENABLED') !== '1' || !props.getProperty('CATALOG_FILE_ID')) fail_('not_configured');
    const now = new Date();
    const day = Utilities.formatDate(now, 'UTC', 'yyyy-MM-dd');
    if (protocol === 2) {
      if (props.getProperty('PUBLIC_SUBMISSIONS_ENABLED') !== '1') fail_('not_configured');
      consumePublicRequest_(props, day, now);
    } else {
      const attempts = invite.Day === day ? invite.Attempts : 0;
      if (!Number.isInteger(attempts) || attempts < 0) fail_('internal_error');
      if (attempts >= LIMITS_.Attempts) fail_('rate_limit');
      // Every authenticated attempt consumes a slot, including duplicates and malformed channel data.
      props.setProperty(inviteKey, JSON.stringify({ Day: day, Attempts: attempts + 1, Revoked: false }));
    }
    const profiles = request.Profiles.map(normalizeCandidate_);
    const keys = profiles.map(profileKey_);
    if (new Set(keys).size !== keys.length || profiles.some(isBaseline_)) fail_('invalid_request');
    const id = props.getProperty('CATALOG_FILE_ID');
    const catalog = readCatalog_(id);
    const existing = new Set(catalog.map(p => profileKey_(candidateFromCatalog_(p))));
    const additions = profiles.filter((_, i) => !existing.has(keys[i]));
    if (!additions.length) return reply_(true, 0, profiles.length, undefined, protocol);
    const state = props.getProperty('DAILY_ROWS') ? parseState_(props.getProperty('DAILY_ROWS')) : { Day: '', Added: 0 };
    const added = state.Day === day ? state.Added : 0;
    if (!Number.isInteger(added) || added < 0) fail_('internal_error');
    if (added + additions.length > LIMITS_.DailyRows) fail_('rate_limit');
    if (catalog.length + additions.length > LIMITS_.Rows) fail_('capacity');
    const next = catalog.concat(additions.map(catalogRow_));
    const text = JSON.stringify(next);
    if (byteSize_(text) > LIMITS_.Bytes) fail_('capacity');
    // Reserve before the external write: an ambiguous failure can stop earlier, never exceed the daily cap.
    props.setProperty('DAILY_ROWS', JSON.stringify({ Day: day, Added: added + additions.length }));
    Drive.Files.update({}, id, Utilities.newBlob(text, 'application/json'), { fields: 'id' });
    return reply_(true, additions.length, profiles.length - additions.length, undefined, protocol);
  } catch (error) {
    return reply_(false, 0, 0, error && error.receiverCode || 'internal_error', protocol);
  } finally { if (lock) lock.releaseLock(); }
}

function consumePublicRequest_(props, day, now) {
  const minute = Utilities.formatDate(now, 'UTC', 'yyyy-MM-dd HH:mm');
  const text = props.getProperty('PUBLIC_REQUESTS');
  const state = text ? parseState_(text) : { Day: day, Requests: 0, Minute: minute, MinuteRequests: 0 };
  if (Object.keys(state).length !== 4 || typeof state.Day !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(state.Day)
      || typeof state.Minute !== 'string' || !/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/.test(state.Minute)
      || state.Minute.indexOf(state.Day + ' ') !== 0 || !Number.isInteger(state.Requests) || state.Requests < 0
      || state.Requests > LIMITS_.PublicDailyRequests || !Number.isInteger(state.MinuteRequests) || state.MinuteRequests < 0
      || state.MinuteRequests > LIMITS_.PublicMinuteRequests || state.MinuteRequests > state.Requests) fail_('internal_error');
  const requests = state.Day === day ? state.Requests : 0;
  const minuteRequests = state.Minute === minute ? state.MinuteRequests : 0;
  if (requests >= LIMITS_.PublicDailyRequests || minuteRequests >= LIMITS_.PublicMinuteRequests) fail_('rate_limit');
  // One fixed property, reserved before profile validation or any Drive call; rejected excess traffic never writes it.
  props.setProperty('PUBLIC_REQUESTS', JSON.stringify({ Day: day, Requests: requests + 1, Minute: minute, MinuteRequests: minuteRequests + 1 }));
}

function readCatalog_(id) {
  if (typeof id !== 'string' || !/^[A-Za-z0-9_-]{10,200}$/.test(id)
      || id !== PropertiesService.getScriptProperties().getProperty('CATALOG_FILE_ID')) fail_('internal_error');
  const metadata = Drive.Files.get(id, { fields: 'id,mimeType,size,trashed,appProperties' });
  if (!metadata || metadata.id !== id || metadata.trashed || metadata.mimeType !== 'application/json'
      || !metadata.appProperties || metadata.appProperties[CATALOG_MARKER_] !== '1'
      || !/^\d+$/.test(String(metadata.size)) || Number(metadata.size) > LIMITS_.Bytes) fail_('internal_error');
  // Advanced Drive service rejects raw media in production. Use Google's documented download endpoint.
  // The token is sent only to this fixed Google host; client input cannot set a URL or a file ID.
  const response = UrlFetchApp.fetch('https://www.googleapis.com/drive/v3/files/' + encodeURIComponent(id) + '?alt=media', {
    method: 'get', headers: { Authorization: 'Bearer ' + ScriptApp.getOAuthToken() },
    muteHttpExceptions: true, followRedirects: false, validateHttpsCertificates: true
  });
  if (response.getResponseCode() !== 200) fail_('internal_error');
  const text = response.getContentText('UTF-8');
  if (byteSize_(text) > LIMITS_.Bytes) fail_('internal_error');
  let value;
  try { value = JSON.parse(text); } catch (_) { fail_('internal_error'); }
  if (!Array.isArray(value) || value.length > LIMITS_.Rows || byteSize_(JSON.stringify(value)) > LIMITS_.Bytes) fail_('internal_error');
  const keys = value.map(p => profileKey_(candidateFromCatalog_(p)));
  if (new Set(keys).size !== keys.length) fail_('internal_error');
  return value;
}

function candidateFromCatalog_(row) {
  exactKeys_(row, ['Id','Name','Enabled','Mode','Servers','DnsPort','DohUrl','Note']);
  if (row.Enabled !== false || typeof row.Note !== 'string') fail_('internal_error');
  const match = /^Сообщено пользователем; независимо не проверено\. Успешные публичные проверки: (chatgpt|gemini|chatgpt,gemini)\.$/.exec(row.Note);
  if (!match) fail_('internal_error');
  const value = normalizeCandidate_({ Mode: row.Mode, Servers: row.Servers, DnsPort: row.DnsPort,
    DohUrl: row.DohUrl, WorkedServices: match[1].split(',') });
  const generated = catalogRow_(value);
  if (row.Id !== generated.Id || row.Name !== generated.Name || isBaseline_(value)) fail_('internal_error');
  return value;
}

function normalizeCandidate_(input) {
  exactKeys_(input, ['Mode','Servers','DnsPort','DohUrl','WorkedServices']);
  if (input.Mode !== 'Udp' && input.Mode !== 'Doh' || !Array.isArray(input.Servers) || input.Servers.length < 1 || input.Servers.length > 8
      || !Number.isInteger(input.DnsPort) || input.DnsPort < 1 || input.DnsPort > 65535 || typeof input.DohUrl !== 'string'
      || !Array.isArray(input.WorkedServices) || input.WorkedServices.length < 1 || input.WorkedServices.length > 2) fail_('invalid_request');
  const servers = input.Servers.map(publicIp_).sort();
  const worked = input.WorkedServices.slice().sort();
  if (new Set(servers).size !== servers.length || new Set(worked).size !== worked.length
      || worked.some(s => s !== 'chatgpt' && s !== 'gemini')) fail_('invalid_request');
  let doh = '';
  if (input.Mode === 'Doh') {
    if (input.DnsPort !== 53 || input.DohUrl.length > 2048 || /[^\x21-\x7e]/.test(input.DohUrl)) fail_('invalid_request');
    const match = /^https:\/\/([a-z0-9.-]+)(?::([1-9][0-9]{0,4}))?(\/[^?#\\]*)$/.exec(input.DohUrl);
    if (!match || !publicHost_(match[1]) || match[2] && (Number(match[2]) > 65535 || match[2] === '443')
        || /%(?![0-9a-fA-F]{2})/.test(match[3]) || /\/(?:\.|\.\.)(?:\/|$)/.test(match[3])
        || (match[3].match(/%[0-9a-fA-F]{2}/g) || []).some(s => /^[A-Za-z0-9._~-]$/.test(String.fromCharCode(parseInt(s.slice(1),16))))) fail_('invalid_request');
    doh = input.DohUrl;
  } else if (input.DohUrl !== '') fail_('invalid_request');
  return { Mode: input.Mode, Servers: servers, DnsPort: input.DnsPort, DohUrl: doh, WorkedServices: worked };
}

function publicIp_(value) {
  if (typeof value !== 'string' || value.length > 39) fail_('invalid_request');
  if (/^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}$/.test(value)) {
    const b = value.split('.').map(Number);
    if (b.some(n => n > 255) || [0,10,127].indexOf(b[0]) >= 0 || b[0] >= 224
        || b[0] === 100 && b[1] >= 64 && b[1] <= 127 || b[0] === 169 && b[1] === 254
        || b[0] === 172 && b[1] >= 16 && b[1] <= 31
        || b[0] === 192 && (b[1] === 0 && [0,2].indexOf(b[2]) >= 0 || b[1] === 88 && b[2] === 99 || b[1] === 168)
        || b[0] === 198 && ([18,19].indexOf(b[1]) >= 0 || b[1] === 51 && b[2] === 100)
        || b[0] === 203 && b[1] === 0 && b[2] === 113) fail_('invalid_request');
    return value;
  }
  if (!/^[a-f0-9]{4}(?::[a-f0-9]{4}){7}$/.test(value)) fail_('invalid_request');
  const b = value.split(':').map(s => parseInt(s,16));
  if ((b[0] & 0xe000) !== 0x2000 || b[0] === 0x2001 && (b[1] < 0x0200 || b[1] === 0x0db8)
      || b[0] === 0x2002 || b[0] === 0x3fff) fail_('invalid_request');
  return value;
}

function publicHost_(host) {
  const labels = host.split('.');
  return host.length <= 253 && labels.length >= 2 && labels.every(s => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(s))
    && /[a-z]/.test(labels[labels.length-1]) && ['local','localhost','internal','lan','home','invalid','test','example'].indexOf(labels[labels.length-1]) < 0;
}
function profileKey_(p) { return sha256_(p.Mode + '\n' + p.Servers.slice().sort().join(',') + '\n' + p.DnsPort + '\n' + p.DohUrl); }
function isBaseline_(p) { return BASE_PROFILES_.some(b => profileKey_(p) === profileKey_({ Mode:b[0], Servers:b[1], DnsPort:b[2], DohUrl:b[3] })); }
function catalogRow_(p) {
  const key = profileKey_(p);
  return { Id:key.slice(0,32), Name:'DNS ' + key.slice(0,16), Enabled:false, Mode:p.Mode, Servers:p.Servers,
    DnsPort:p.DnsPort, DohUrl:p.DohUrl,
    Note:'Сообщено пользователем; независимо не проверено. Успешные публичные проверки: ' + p.WorkedServices.join(',') + '.' };
}
function exactKeys_(value, keys) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).length !== keys.length
      || keys.some(k => !Object.prototype.hasOwnProperty.call(value,k))) fail_('invalid_request');
}
function parseState_(text) { try { const value = JSON.parse(text); if (!value || typeof value !== 'object' || Array.isArray(value)) fail_('internal_error'); return value; } catch (_) { fail_('internal_error'); } }
function sha256_(text) { return Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, text, Utilities.Charset.UTF_8).map(b => ('0' + ((b + 256) % 256).toString(16)).slice(-2)).join(''); }
function byteSize_(text) { return Utilities.newBlob(text).getBytes().length; }
function fail_(code) { const error = new Error('Receiver request rejected.'); error.receiverCode = code; throw error; }
function reply_(ok, accepted, duplicates, error, protocol = 1) {
  const value = { Protocol:protocol, Ok:ok, Accepted:accepted, Duplicates:duplicates };
  if (error) value.Error = ['invalid_request','denied','rate_limit','capacity','internal_error','busy','not_configured'].indexOf(error) >= 0 ? error : 'internal_error';
  return json_(value);
}
function json_(value) { return ContentService.createTextOutput(JSON.stringify(value)).setMimeType(ContentService.MimeType.JSON); }
