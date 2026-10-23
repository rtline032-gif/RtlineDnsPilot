'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, 'Code.gs'), 'utf8');
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'appsscript.json'), 'utf8'));
let passed = 0;
function test(name, fn) { try { fn(); passed++; console.log('PASS ' + name); } catch (e) { console.error('FAIL ' + name + ': ' + e.message); process.exitCode = 1; } }
function fixture() {
  const props = new Map(), files = new Map(), calls = [], logs = [], propertyWrites = [];
  const state = { held: false, denyLock: false, day: '2026-10-22', minute: '2026-10-22 00:00', onUpdate: null, throwUpdate: false,
    downloadStatus: 200, downloadText: null };
  const blob = text => ({ getBytes: () => Array.from(Buffer.from(String(text),'utf8')), getDataAsString: () => String(text) });
  const propertyService = { getProperty: key => props.has(key) ? props.get(key) : null,
    setProperty: (key, value) => { propertyWrites.push({key,value:String(value)}); props.set(key, String(value)); return propertyService; },
    deleteProperty: key => { props.delete(key); return propertyService; }, getProperties: () => Object.fromEntries(props) };
  const context = {
    PropertiesService: { getScriptProperties: () => propertyService },
    LockService: { getScriptLock: () => ({ tryLock: () => { if (state.held || state.denyLock) return false; state.held = true; return true; },
      releaseLock: () => { assert.equal(state.held,true); state.held = false; } }) },
    Utilities: { newBlob: blob, getUuid: () => crypto.randomUUID(), formatDate: (_,zone,format) => format === 'yyyy-MM-dd' ? state.day : state.minute,
      DigestAlgorithm: { SHA_256:'SHA256' }, Charset:{ UTF_8:'UTF8' },
      computeDigest: (_, text) => Array.from(crypto.createHash('sha256').update(text,'utf8').digest()).map(b => b > 127 ? b - 256 : b) },
    ContentService: { MimeType: { JSON:'application/json' }, createTextOutput: text => ({ text, setMimeType() { return this; } }) },
    console: { log: text => logs.push(text) },
    ScriptApp: { getOAuthToken: () => 'fixture-token-never-logged' },
    UrlFetchApp: { fetch(url, options) {
      assert.equal(state.held,true);
      assert.equal(url,'https://www.googleapis.com/drive/v3/files/script-created-catalog?alt=media');
      assert.deepEqual(JSON.parse(JSON.stringify(options)),{method:'get',headers:{Authorization:'Bearer fixture-token-never-logged'},
        muteHttpExceptions:true,followRedirects:false,validateHttpsCertificates:true});
      calls.push({method:'download',id:'script-created-catalog'});
      return {getResponseCode:()=>state.downloadStatus,getContentText:encoding=>{
        assert.equal(encoding,'UTF-8');return state.downloadText ?? files.get('script-created-catalog').text;
      }};
    } },
    Drive: { Files: {
      create(metadata, data, options) {
        assert.equal(state.held,true); assert.deepEqual(Object.keys(metadata).sort(),['appProperties','mimeType','name']);
        assert.equal(options.fields,'id'); assert.equal(metadata.mimeType,'application/json');
        const id = 'script-created-catalog'; assert.equal(files.has(id),false); files.set(id,{ text:data.getDataAsString(),metadata });
        calls.push({ method:'create',id }); return { id };
      },
      get(id, options) {
        assert.equal(state.held,true); assert.equal(id,props.get('CATALOG_FILE_ID')); assert.equal(id,'script-created-catalog');
        const file=files.get(id); if(!file) throw Error('missing'); calls.push({method:'get',id,options});
        if(options.alt === 'media') throw Error('HttpResponseException: Code 200; advanced-service media is not supported.');
        return { id,mimeType:file.metadata.mimeType,size:String(Buffer.byteLength(file.text)),
          trashed:!!file.trashed,appProperties:file.metadata.appProperties };
      },
      update(metadata,id,data,options) {
        assert.equal(state.held,true); assert.deepEqual(Object.keys(metadata),[]); assert.equal(id,'script-created-catalog');
        assert.equal(id,props.get('CATALOG_FILE_ID')); assert.equal(options.fields,'id');
        if(state.onUpdate) state.onUpdate();
        calls.push({method:'update',id});
        if(state.throwUpdate) throw Error('SECRET diagnostic must not leak');
        files.get(id).text=data.getDataAsString();return {id};
      }
    } }
  };
  const api=vm.runInNewContext(source + '\n;({setupCatalog,createInvite,revokeInvite,disableReceiver,doGet,doPost,profileKey_,catalogRow_,normalizeCandidate_,BASE_PROFILES_,LIMITS_,enablePublicSubmissions:typeof enablePublicSubmissions === "function" ? enablePublicSubmissions : undefined,disablePublicSubmissions:typeof disablePublicSubmissions === "function" ? disablePublicSubmissions : undefined});',context);
  function ownerSetup() {
    api.setupCatalog();api.createInvite();const line=logs.find(s=>s.startsWith('ONE-TIME invite code:'));
    const code=/ONE-TIME invite code: ([a-f0-9]{64})/.exec(line)[1];const hash=/Revoke hash: ([a-f0-9]{64})/.exec(line)[1];
    logs.length=0;return {code,hash};
  }
  const credentials=ownerSetup();
  function post(profiles=[candidate()], mutate) {
    const request={Protocol:1,InviteCode:credentials.code,Profiles:profiles};if(mutate) mutate(request);
    const event={postData:{type:'application/json; charset=utf-8',contents:JSON.stringify(request)},parameters:{}};
    return JSON.parse(api.doPost(event).text);
  }
  function publicPost(profiles=[candidate()], mutate) {
    const request={Protocol:2,Profiles:profiles};if(mutate) mutate(request);
    const event={postData:{type:'application/json; charset=utf-8',contents:JSON.stringify(request)},parameters:{}};
    return JSON.parse(api.doPost(event).text);
  }
  function get(event={parameters:{}}) { return JSON.parse(api.doGet(event).text); }
  return { api,props,files,calls,logs,state,credentials,post,publicPost,get,context,propertyWrites };
}
function candidate(ip='8.8.8.8') { return {Mode:'Udp',Servers:[ip],DnsPort:53,DohUrl:'',WorkedServices:['chatgpt']}; }
function additions(n, start=1) { return Array.from({length:n},(_,i)=>candidate('8.9.'+Math.floor((i+start)/254)+'.'+((i+start)%254+1))); }
function catalog(f) { return JSON.parse(f.files.get('script-created-catalog').text); }
function writeCount(f) { return f.calls.filter(c=>c.method==='update').length; }
function invite(f) { return JSON.parse(f.props.get('INVITE_'+f.credentials.hash)); }

test('manifest keeps drive.file and permits only the required external-request service',()=>{
  assert.deepEqual(manifest.oauthScopes,['https://www.googleapis.com/auth/drive.file','https://www.googleapis.com/auth/script.external_request']);
  assert.deepEqual(manifest.dependencies.enabledAdvancedServices,[{userSymbol:'Drive',version:'v3',serviceId:'drive'}]);
  assert.equal(/\bDriveApp\b|\beval\s*\(|\bnew\s+Function\b/.test(source),false);
});

test('catalog reading uses one fixed Google media GET without redirecting or logging tokens',()=>{
  const f=fixture();f.api.enablePublicSubmissions();assert.equal(f.props.get('PUBLIC_SUBMISSIONS_ENABLED'),'1');
  assert.equal(f.calls.filter(c=>c.method==='download').length,1);
  assert.equal(f.logs.join('\n').includes('fixture-token'),false);assert.equal(writeCount(f),0);
});

test('media redirects HTTP errors and malformed responses fail closed before public enable',()=>{
  for(const status of [302,403,500]) {const f=fixture();f.state.downloadStatus=status;
    assert.throws(()=>f.api.enablePublicSubmissions());assert.equal(f.props.get('PUBLIC_SUBMISSIONS_ENABLED'),'0');assert.equal(writeCount(f),0);}
  for(const text of ['not JSON','{}',' '.repeat(256*1024+1)]) {const f=fixture();f.state.downloadText=text;
    assert.throws(()=>f.api.enablePublicSubmissions());assert.equal(f.props.get('PUBLIC_SUBMISSIONS_ENABLED'),'0');assert.equal(writeCount(f),0);}
});

test('invalid catalog identity is rejected before Drive or authenticated media request',()=>{
  const f=fixture();f.props.set('CATALOG_FILE_ID','https://attacker.invalid/file');const before=f.calls.length;
  assert.throws(()=>f.api.enablePublicSubmissions());assert.equal(f.calls.length,before);assert.equal(f.props.get('PUBLIC_SUBMISSIONS_ENABLED'),'0');
});
test('setup creates exactly one own file with no folder/permission mutation',()=>{
  const f=fixture();f.api.setupCatalog();assert.equal(f.calls.filter(c=>c.method==='create').length,1);
  assert.deepEqual(catalog(f),[]);assert.equal(f.props.get('RECEIVER_ENABLED'),'1');
});
test('per-recipient invites have 256-bit codes and only hashes in properties',()=>{
  const f=fixture();assert.match(f.credentials.code,/^[a-f0-9]{64}$/);assert.notEqual(f.credentials.code,f.credentials.hash);
  assert.equal(JSON.stringify(Object.fromEntries(f.props)).includes(f.credentials.code),false);
  assert.equal(crypto.createHash('sha256').update(f.credentials.code).digest('hex'),f.credentials.hash);
  f.api.createInvite();assert.equal([...f.props.keys()].filter(k=>k.startsWith('INVITE_')).length,2);
});
test('successful POST adds disabled importable public-derived row only',()=>{
  const f=fixture();const r=f.post();assert.deepEqual(r,{Protocol:1,Ok:true,Accepted:1,Duplicates:0});
  const rows=catalog(f);assert.equal(rows.length,1);assert.deepEqual(Object.keys(rows[0]).sort(),['DnsPort','DohUrl','Enabled','Id','Mode','Name','Note','Servers']);
  assert.equal(rows[0].Enabled,false);assert.match(rows[0].Id,/^[a-f0-9]{32}$/);assert.equal(rows[0].Name,'DNS '+rows[0].Id.slice(0,16));
  assert.match(rows[0].Note,/независимо не проверено/);assert.equal(JSON.stringify(rows).includes(f.credentials.code),false);assert.equal(f.logs.length,0);
});
test('duplicates never rewrite the catalog or alter row evidence',()=>{
  const f=fixture();f.post();const before=f.files.get('script-created-catalog').text;const p=candidate();p.WorkedServices=['gemini'];
  const r=f.post([p]);assert.equal(r.Ok,true);assert.equal(r.Accepted,0);assert.equal(r.Duplicates,1);assert.equal(writeCount(f),1);
  assert.equal(f.files.get('script-created-catalog').text,before);assert.equal(invite(f).Attempts,2);
});
test('unknown or revoked invitation denied before any Drive read',()=>{
  const f=fixture();const before=f.calls.length;const r=f.post(undefined,q=>q.InviteCode='0'.repeat(64));assert.equal(r.Error,'denied');assert.equal(f.calls.length,before);
  f.props.set('REVOKE_INVITE_HASH',f.credentials.hash);f.api.revokeInvite();assert.equal(f.post().Error,'denied');assert.equal(f.calls.length,before);
});
test('receiver disabled and GET/admin lookalikes cannot write',()=>{
  const f=fixture();const before=f.calls.length;assert.equal(f.get({parameters:{Command:'setupCatalog'}}).Error,'invalid_request');
  assert.equal(f.post(undefined,q=>q.Command='setupCatalog').Error,'invalid_request');f.api.disableReceiver();assert.equal(f.post().Error,'not_configured');
  assert.equal(f.calls.length,before);
});
test('malformed envelopes, unknown personal fields and duplicate batch settings reject before Drive',()=>{
  for(const mutate of [q=>q.Protocol=2,q=>q.FolderId='OTHER',q=>q.Profiles=[],q=>q.Profiles=Array(33).fill(candidate()),
    q=>q.Profiles[0].Name='PRIVATE',q=>q.Profiles[0].ProfileId='PRIVATE',q=>q.Profiles[0].WorkedServices=['custom'],
    q=>q.Profiles[0].Servers=['8.8.8.8','8.8.8.8'],q=>q.Profiles.push(candidate())]) {
    const f=fixture();const before=f.calls.length;assert.equal(f.post(undefined,mutate).Error,'invalid_request');assert.equal(f.calls.length,before);
  }
});
test('body length is bounded by UTF8 bytes, malformed JSON/type/path rejected',()=>{
  const f=fixture();const before=f.calls.length;
  for(const event of [{postData:{type:'application/json',contents:'{' }},{postData:{type:'text/plain',contents:'{}'}},
    {postData:{type:'application/json',contents:' '.repeat(96*1024+1)}},{postData:{type:'application/json',contents:'я'.repeat(60*1024)}},
    {postData:{type:'application/json',contents:'{}'},queryString:'fileId=OTHER'},{postData:{type:'application/json',contents:'{}'},pathInfo:'setupCatalog'}])
      assert.equal(JSON.parse(f.api.doPost(event).text).Error,'invalid_request');
  assert.equal(f.calls.length,before);
});
test('all 15 base configurations reject, including reordered address arrays',()=>{
  const f=fixture();assert.equal(f.api.BASE_PROFILES_.length,15);
  for(const b of f.api.BASE_PROFILES_) {
    const x=fixture();const p={Mode:b[0],Servers:Array.from(b[1]).reverse(),DnsPort:b[2],DohUrl:b[3],WorkedServices:['gemini']};
    const before=x.calls.length;assert.equal(x.post([p]).Error,'invalid_request');assert.equal(x.calls.length,before);
  }
});
test('private reserved mapped multicast documentation and compressed IPv6 reject',()=>{
  const values=['10.0.0.1','100.64.0.1','127.0.0.1','169.254.1.1','172.16.0.1','192.168.1.1','192.0.2.1','198.18.1.1','198.51.100.1','203.0.113.1',
    '224.0.0.1','255.255.255.255','008.8.8.8','8.8.8','2001:4860:4860::8888','0000:0000:0000:0000:0000:ffff:0808:0808',
    'fe80:0000:0000:0000:0000:0000:0000:0001','fc00:0000:0000:0000:0000:0000:0000:0001',
    '2001:0db8:0000:0000:0000:0000:0000:0001','2002:0000:0000:0000:0000:0000:0000:0001','3fff:0000:0000:0000:0000:0000:0000:0001'];
  for(const ip of values) {const f=fixture();const before=f.calls.length;assert.equal(f.post([candidate(ip)]).Error,'invalid_request',ip);assert.equal(f.calls.length,before);}
});
test('strict global IPv6 canonical form and HTTPS DoH are accepted',()=>{
  const f=fixture();const p=candidate('2001:4860:4860:0000:0000:0000:0000:8888');p.Mode='Doh';p.DohUrl='https://dns.google/dns-query';p.WorkedServices=['gemini','chatgpt'];
  assert.equal(f.post([p]).Accepted,1);assert.deepEqual(catalog(f)[0].Servers,p.Servers);assert.match(catalog(f)[0].Note,/chatgpt,gemini/);
});
test('DoH schema rejects URL credentials parameters fragments private names and noncanonical ports',()=>{
  for(const url of ['http://dns.google/dns-query','https://user:pass@dns.google/dns-query','https://dns.google/dns-query?x=1',
    'https://dns.google/dns-query#x','https://machine.local/dns-query','https://8.8.8.8/dns-query','https://dns.google:443/dns-query',
    'https://dns.google:99999/dns-query','https://dns.google/../dns-query','https://dns.google/dns%xx',
    'https://dns.comss.one/dns%2dquery','https://dns.comss.one/%64ns-query']) {
    const f=fixture();const p=candidate();p.Mode='Doh';p.DohUrl=url;assert.equal(f.post([p]).Error,'invalid_request',url);
  }
});
test('five authenticated attempts per invitation per UTC day include duplicates',()=>{
  const f=fixture();for(let i=0;i<5;i++) assert.equal(f.post().Ok,true);const reads=f.calls.length;
  assert.equal(f.post().Error,'rate_limit');assert.equal(f.calls.length,reads);assert.equal(writeCount(f),1);
  f.state.day='2026-10-23';assert.equal(f.post().Ok,true);assert.equal(invite(f).Attempts,1);
});
test('100 new additions per day are enforced globally across invitations',()=>{
  const f=fixture();f.props.set('DAILY_ROWS',JSON.stringify({Day:f.state.day,Added:99}));
  assert.equal(f.post([candidate()]).Accepted,1);assert.equal(f.post([candidate('1.1.1.1')]).Error,'rate_limit');assert.equal(catalog(f).length,1);
  f.state.day='2026-10-23';assert.equal(f.post([candidate('1.1.1.1')]).Accepted,1);
});
test('oversized 1000-row catalog is preserved and never rewritten',()=>{
  const f=fixture();const rows=additions(1000).map(p=>f.api.catalogRow_(p));f.files.get('script-created-catalog').text=JSON.stringify(rows);
  // The byte cap can fill before the 1000-entry cap, as required by the receiver policy.
  const fileBytes=Buffer.byteLength(f.files.get('script-created-catalog').text);assert.ok(fileBytes>256*1024);
  assert.equal(f.api.LIMITS_.Rows,1000);
  assert.equal(f.post().Error,'internal_error');assert.equal(writeCount(f),0);assert.equal(catalog(f).length,1000);
});
test('256 KiB capacity is checked before writing additional catalog content',()=>{
  const f=fixture();const profiles=additions(200).map(p=>{p.Mode='Doh';p.DohUrl='https://dns.google/'+ 'a'.repeat(1500);return p;});
  let rows=[];for(const p of profiles){const next=rows.concat([f.api.catalogRow_(p)]);if(Buffer.byteLength(JSON.stringify(next))>256*1024)break;rows=next;}
  f.files.get('script-created-catalog').text=JSON.stringify(rows);assert.ok(rows.length>0);
  const extra=candidate('1.1.1.1');extra.Mode='Doh';extra.DohUrl='https://dns.google/'+ 'a'.repeat(2000);
  assert.equal(f.post([extra]).Error,'capacity');assert.equal(writeCount(f),0);
});
test('ScriptLock prevents overlapping requests and owner mutations',()=>{
  const f=fixture();let nested;f.state.onUpdate=()=>{nested=f.post([candidate('1.1.1.1')]);};
  assert.equal(f.post().Accepted,1);assert.equal(nested.Error,'busy');assert.equal(writeCount(f),1);assert.equal(catalog(f).length,1);assert.equal(f.state.held,false);
  f.state.denyLock=true;assert.equal(f.post().Error,'busy');assert.throws(()=>f.api.disableReceiver());
});
test('client file/folder IDs and corrupt or foreign catalog metadata never authorize another file',()=>{
  const f=fixture();f.files.set('OTHER',{text:'PRIVATE',metadata:{mimeType:'application/json',appProperties:{}}});
  const before=f.calls.length;assert.equal(f.post(undefined,q=>q.FileId='OTHER').Error,'invalid_request');assert.equal(f.calls.length,before);
  f.files.get('script-created-catalog').metadata.appProperties={};assert.equal(f.post().Error,'internal_error');
  assert.equal(f.files.get('OTHER').text,'PRIVATE');assert.equal(f.calls.some(c=>c.id==='OTHER'),false);assert.equal(writeCount(f),0);
});
test('corrupt JSON/rows remain intact and exceptions reveal no content or invitation',()=>{
  const f=fixture();const file=f.files.get('script-created-catalog');file.text='[{"PRIVATE":"SECRET"}]';const before=file.text;
  const result=f.post();assert.equal(result.Ok,false);assert.equal(file.text,before);assert.equal(writeCount(f),0);
  assert.equal(JSON.stringify(result).includes('PRIVATE'),false);assert.equal(JSON.stringify(result).includes(f.credentials.code),false);
});
test('ambiguous Drive error reserves capacity but never retries an upload internally',()=>{
  const f=fixture();f.state.throwUpdate=true;const r=f.post();assert.equal(r.Error,'internal_error');assert.equal(writeCount(f),1);
  assert.equal(JSON.parse(f.props.get('DAILY_ROWS')).Added,1);assert.equal(JSON.stringify(r).includes('SECRET'),false);assert.equal(f.logs.length,0);
});
test('public submissions stay disabled after default setup and require explicit owner enable',()=>{
  const f=fixture();const before=f.calls.length;
  assert.deepEqual(f.publicPost(),{Protocol:2,Ok:false,Accepted:0,Duplicates:0,Error:'not_configured'});
  assert.equal(f.calls.length,before);assert.equal(f.get().Ready,false);assert.equal(f.get().PublicEnabled,false);
  f.api.enablePublicSubmissions();assert.equal(f.get().Ready,true);
  assert.deepEqual(f.publicPost(),{Protocol:2,Ok:true,Accepted:1,Duplicates:0});
});
test('owner public enable validates existing own catalog before exposing writes',()=>{
  for(const corrupt of [f=>f.props.delete('CATALOG_FILE_ID'),f=>f.files.delete('script-created-catalog'),
    f=>f.files.get('script-created-catalog').metadata.appProperties={},f=>f.files.get('script-created-catalog').text='{}',
    f=>f.api.disableReceiver()]) {
    const f=fixture();corrupt(f);assert.throws(()=>f.api.enablePublicSubmissions());assert.equal(f.get().Ready,false);assert.equal(writeCount(f),0);
  }
});
test('GET readiness exposes only service identity and public status with no Drive access',()=>{
  const f=fixture();f.api.enablePublicSubmissions();const before=f.calls.length;
  assert.deepEqual(f.get(),{Service:'RtlineDnsPilot',Protocol:2,PublicEnabled:true,Ready:true});
  assert.equal(f.calls.length,before);assert.equal(JSON.stringify(f.get()).includes('script-created-catalog'),false);
  assert.equal(JSON.stringify(f.get()).includes(f.credentials.code),false);
});
test('owner disable public preserves invite path and cannot be undone by a public operation',()=>{
  const f=fixture();f.api.enablePublicSubmissions();f.api.disablePublicSubmissions();const before=f.calls.length;
  assert.equal(f.publicPost().Error,'not_configured');assert.equal(f.calls.length,before);assert.equal(f.get().Ready,false);
  assert.equal(f.post().Accepted,1);
  for(const command of ['setupCatalog','enablePublicSubmissions','disablePublicSubmissions','createInvite','revokeInvite','disableReceiver']) {
    assert.equal(f.publicPost(undefined,q=>q.Command=command).Error,'invalid_request');
    assert.equal(f.get({queryString:'Command='+command,parameters:{Command:command}}).Error,'invalid_request');
  }
  assert.equal(f.get().PublicEnabled,false);
});
test('setup and full receiver disable never silently re-enable public submissions',()=>{
  const f=fixture();f.api.enablePublicSubmissions();f.api.setupCatalog();assert.equal(f.get().Ready,false);
  f.api.enablePublicSubmissions();f.api.disableReceiver();assert.equal(f.get().Ready,false);
  f.api.setupCatalog();assert.equal(f.get().Ready,false);assert.equal(f.publicPost().Error,'not_configured');
});
test('protocol 2 envelope excludes invite endpoint file folder and identity fields',()=>{
  for(const mutate of [q=>q.InviteCode='0'.repeat(64),q=>q.Url='https://other.invalid/exec',q=>q.FileId='OTHER',q=>q.FolderId='OTHER',
    q=>q.Name='PRIVATE',q=>q.Profiles=[],q=>q.Profiles=Array(33).fill(candidate()),q=>q.Protocol=3]) {
    const f=fixture();f.api.enablePublicSubmissions();const before=f.calls.length;const writes=f.propertyWrites.length;
    assert.equal(f.publicPost(undefined,mutate).Error,'invalid_request');assert.equal(f.calls.length,before);assert.equal(f.propertyWrites.length,writes);
  }
});
test('public duplicate and reordered settings return counts without rewriting evidence',()=>{
  const f=fixture();f.api.enablePublicSubmissions();const p=candidate();p.Servers=['8.8.8.8','1.1.1.1'];
  assert.equal(f.publicPost([p]).Accepted,1);const before=f.files.get('script-created-catalog').text;
  p.Servers.reverse();p.WorkedServices=['gemini'];assert.deepEqual(f.publicPost([p]),{Protocol:2,Ok:true,Accepted:0,Duplicates:1});
  assert.equal(writeCount(f),1);assert.equal(f.files.get('script-created-catalog').text,before);
});
test('public minute budget rejects the eleventh attempt before reading Drive',()=>{
  const f=fixture();f.api.enablePublicSubmissions();for(let i=0;i<10;i++)assert.equal(f.publicPost().Ok,true);
  const reads=f.calls.length,writes=f.propertyWrites.length;
  assert.equal(f.publicPost().Error,'rate_limit');assert.equal(f.calls.length,reads);assert.equal(f.propertyWrites.length,writes);
  f.state.minute='2026-10-22 00:01';assert.equal(f.publicPost().Ok,true);assert.equal(writeCount(f),1);
});
test('public daily request budget spans minute windows and resets by UTC day',()=>{
  const f=fixture();f.api.enablePublicSubmissions();
  for(let i=0;i<200;i++){f.state.minute='2026-10-22 00:'+String(Math.floor(i/10)).padStart(2,'0');assert.equal(f.publicPost().Ok,true);}
  f.state.minute='2026-10-22 01:00';const before=f.calls.length;const writes=f.propertyWrites.length;
  assert.equal(f.publicPost().Error,'rate_limit');assert.equal(f.calls.length,before);assert.equal(f.propertyWrites.length,writes);
  f.state.day='2026-10-23';f.state.minute='2026-10-23 00:00';assert.equal(f.publicPost().Ok,true);
});
test('valid public envelopes with malformed private or base candidates consume bounded budget before Drive',()=>{
  const mutations=[p=>p.Name='PRIVATE',p=>p.WorkedServices=['custom'],p=>p.Servers=['192.168.1.1'],p=>p.DohUrl='https://private.local/dns-query',
    p=>{p.Servers=['111.88.96.55','111.88.96.54'];},p=>p.Servers=['2001:4860:4860::8888'],p=>p.Servers=['8.8.8.8','8.8.8.8']];
  const f=fixture();f.api.enablePublicSubmissions();const before=f.calls.length;
  for(const mutate of mutations){const p=candidate();mutate(p);assert.equal(f.publicPost([p]).Error,'invalid_request');}
  assert.equal(f.calls.length,before);for(let i=mutations.length;i<10;i++)assert.equal(f.publicPost().Ok,true);
  const reads=f.calls.length;assert.equal(f.publicPost().Error,'rate_limit');assert.equal(f.calls.length,reads);
});
test('public clients cannot create per-installation properties or unbounded rate-limit writes',()=>{
  const f=fixture();f.api.enablePublicSubmissions();const keys=new Set(f.props.keys());
  for(let i=0;i<100;i++)assert.equal(f.publicPost(undefined,q=>q.InstallationId=String(i)).Error,'invalid_request');
  assert.deepEqual(new Set(f.props.keys()),keys);
  for(let i=0;i<10;i++)assert.equal(f.publicPost().Ok,true);
  const boundedWrites=f.propertyWrites.length;
  for(let i=0;i<100;i++)assert.equal(f.publicPost().Error,'rate_limit');assert.equal(f.propertyWrites.length,boundedWrites);
  const extra=[...f.props.keys()].filter(k=>!keys.has(k));assert.equal(extra.length,2);
  assert.ok([...f.props.values()].every(v=>Buffer.byteLength(v)<256));
});
test('public and invited submissions share global daily additions and deduplicate across protocols',()=>{
  const f=fixture();f.api.enablePublicSubmissions();f.props.set('DAILY_ROWS',JSON.stringify({Day:f.state.day,Added:98}));
  assert.equal(f.publicPost().Accepted,1);assert.equal(f.post().Duplicates,1);assert.equal(f.post([candidate('1.1.1.1')]).Accepted,1);
  assert.equal(f.publicPost([candidate('9.9.9.9')]).Error,'rate_limit');assert.equal(catalog(f).length,2);assert.equal(writeCount(f),2);
});
test('public catalog capacity is bounded before any update and preserves existing bytes',()=>{
  const f=fixture();f.api.enablePublicSubmissions();
  const profiles=additions(200).map(p=>{p.Mode='Doh';p.DohUrl='https://dns.google/'+'a'.repeat(1500);return p;});
  let rows=[];for(const p of profiles){const next=rows.concat([f.api.catalogRow_(p)]);if(Buffer.byteLength(JSON.stringify(next))>256*1024)break;rows=next;}
  const file=f.files.get('script-created-catalog');file.text=JSON.stringify(rows);const before=file.text;
  const p=candidate('1.1.1.1');p.Mode='Doh';p.DohUrl='https://dns.google/'+'a'.repeat(2000);
  assert.equal(f.publicPost([p]).Error,'capacity');assert.equal(writeCount(f),0);assert.equal(file.text,before);
});
test('public malformed and corrupt limiter state fails closed without accessing Drive',()=>{
  for(const value of ['{}','not JSON',JSON.stringify({Day:'2026-10-22',Requests:-1,Minute:'2026-10-22 00:00',MinuteRequests:0}),
    JSON.stringify({Day:'2026-10-22',Requests:0,Minute:'2026-10-22 00:00',MinuteRequests:1,Extra:'x'})]) {
    const f=fixture();f.api.enablePublicSubmissions();f.props.set('PUBLIC_REQUESTS',value);const before=f.calls.length;
    assert.equal(f.publicPost().Error,'internal_error');assert.equal(f.calls.length,before);
  }
});
test('public concurrent submission cannot bypass script lock or create another catalog',()=>{
  const f=fixture();f.api.enablePublicSubmissions();let nested;
  f.state.onUpdate=()=>{nested=f.publicPost([candidate('1.1.1.1')]);assert.throws(()=>f.api.disablePublicSubmissions());};
  assert.equal(f.publicPost().Accepted,1);assert.equal(nested.Error,'busy');assert.equal(writeCount(f),1);assert.equal(catalog(f).length,1);
  assert.equal(f.calls.filter(c=>c.method==='create').length,1);assert.equal(f.state.held,false);
});
test('public ambiguous write failure reserves request and row budgets without internal retry',()=>{
  const f=fixture();f.api.enablePublicSubmissions();f.state.throwUpdate=true;const r=f.publicPost();
  assert.equal(r.Error,'internal_error');assert.equal(r.Protocol,2);assert.equal(writeCount(f),1);
  assert.equal(JSON.parse(f.props.get('DAILY_ROWS')).Added,1);assert.equal(JSON.parse(f.props.get('PUBLIC_REQUESTS')).Requests,1);
  assert.equal(JSON.stringify(r).includes('SECRET'),false);assert.equal(JSON.stringify(r).includes(f.credentials.code),false);
});
console.log('Receiver checks: '+passed+' passed.');
