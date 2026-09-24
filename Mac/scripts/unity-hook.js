// PTCGL Leaderboard & Match History - lists the macOS loader in the game's own Unity manifests.
//
//   osascript -l JavaScript unity-hook.js install|uninstall|status "<game>.app/Contents/Resources/Data"
//
// A Unity player reads two files at start-up: ScriptingAssemblies.json (which managed assemblies
// to load, and what kind each is) and RuntimeInitializeOnLoads.json (which static methods to call
// before the first scene). Adding PtcglLeaderboard.MacLoader to both is how the game is asked to
// start BepInEx on a Mac, where Doorstop cannot inject into a notarized app. Nothing else in
// either file is touched, so removing the two entries puts the game back as it shipped.
//
// JavaScript for Automation because JSON has to be edited as JSON, and macOS has no other JSON
// tool on every Mac. The logic is plain JavaScript and is exported for Node, which is how it is
// tested; run() is the only part that talks to macOS.
//
// Prints "changed" or "unchanged" (install/uninstall) or "hooked"/"not hooked" (status). Throws,
// and so exits non-zero with the message on stderr, when a file cannot be read, parsed or written.

var HOOK = {
  file: 'PtcglLeaderboard.MacLoader.dll',
  // What ScriptingAssemblies.json calls every game assembly (Assembly-CSharp included); the
  // engine's own UnityEngine.* assemblies are 2.
  type: 16,
  entry: {
    assemblyName: 'PtcglLeaderboard.MacLoader',
    nameSpace: 'PtcglLeaderboard.MacLoader',
    className: 'Entrypoint',
    methodName: 'Init',
    loadTypes: 1,          // RuntimeInitializeLoadType.BeforeSceneLoad
    isUnityClass: false
  }
};

function checkAssemblies(j) {
  if (!j || typeof j !== 'object' || !Array.isArray(j.names) || !Array.isArray(j.types) ||
      j.names.length !== j.types.length) {
    throw new Error('ScriptingAssemblies.json is not in the format the leaderboard expects');
  }
}

function checkInitializers(j) {
  if (!j || typeof j !== 'object' || !Array.isArray(j.root)) {
    throw new Error('RuntimeInitializeOnLoads.json is not in the format the leaderboard expects');
  }
}

function isOurEntry(e) {
  return !!e && typeof e === 'object' && e.assemblyName === HOOK.entry.assemblyName;
}

function sameEntry(a, b) {
  var keys = Object.keys(b);
  if (Object.keys(a).length !== keys.length) return false;
  for (var i = 0; i < keys.length; i++) if (a[keys[i]] !== b[keys[i]]) return false;
  return true;
}

function copyEntry() {
  var e = {};
  Object.keys(HOOK.entry).forEach(function (k) { e[k] = HOOK.entry[k]; });
  return e;
}

// Each returns true when it changed the object it was given.

function hookAssemblies(j, install) {
  checkAssemblies(j);
  var changed = false;
  if (install) {
    var at = j.names.indexOf(HOOK.file);
    if (at < 0) { j.names.push(HOOK.file); j.types.push(HOOK.type); return true; }
    if (j.types[at] !== HOOK.type) { j.types[at] = HOOK.type; changed = true; }
    for (var d = j.names.length - 1; d > at; d--) {
      if (j.names[d] === HOOK.file) { j.names.splice(d, 1); j.types.splice(d, 1); changed = true; }
    }
    return changed;
  }
  for (var i = j.names.length - 1; i >= 0; i--) {
    if (j.names[i] === HOOK.file) { j.names.splice(i, 1); j.types.splice(i, 1); changed = true; }
  }
  return changed;
}

function hookInitializers(j, install) {
  checkInitializers(j);
  var ours = j.root.filter(isOurEntry);
  if (install) {
    if (ours.length === 1 && sameEntry(ours[0], HOOK.entry)) return false;
    j.root = j.root.filter(function (e) { return !isOurEntry(e); });
    j.root.push(copyEntry());
    return true;
  }
  if (ours.length === 0) return false;
  j.root = j.root.filter(function (e) { return !isOurEntry(e); });
  return true;
}

function isHooked(assemblies, initializers) {
  checkAssemblies(assemblies);
  checkInitializers(initializers);
  var at = assemblies.names.indexOf(HOOK.file);
  var ours = initializers.root.filter(isOurEntry);
  return at >= 0 && assemblies.types[at] === HOOK.type && ours.length === 1 && sameEntry(ours[0], HOOK.entry);
}

// A file's text around its JSON: a byte-order mark before it (JSON.parse rejects one) and the
// whitespace after it (RuntimeInitializeOnLoads.json ends in a newline, ScriptingAssemblies.json
// does not). Kept, so that uninstalling gives back the original file byte for byte.
function frame(text) {
  var bom = text.charCodeAt(0) === 0xFEFF ? text.charAt(0) : '';
  var tail = /\s*$/.exec(text)[0];
  return { bom: bom, tail: tail, json: text.substring(bom.length) };
}

// Parse, change, and produce the text to write back - or null when nothing changes. Unity writes
// both files as compact JSON, which is what JSON.stringify produces.
function transform(assembliesText, initializersText, mode) {
  var fa = frame(assembliesText), fr = frame(initializersText);
  var a = JSON.parse(fa.json);
  var r = JSON.parse(fr.json);
  if (mode === 'status') return { status: isHooked(a, r) ? 'hooked' : 'not hooked' };
  if (mode !== 'install' && mode !== 'uninstall') throw new Error('unknown mode: ' + mode);
  var install = mode === 'install';
  var ca = hookAssemblies(a, install);
  var cr = hookInitializers(r, install);
  return {
    assemblies: ca ? fa.bom + JSON.stringify(a) + fa.tail : null,
    initializers: cr ? fr.bom + JSON.stringify(r) + fr.tail : null
  };
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { HOOK: HOOK, hookAssemblies: hookAssemblies, hookInitializers: hookInitializers,
                     isHooked: isHooked, transform: transform };
}

// ---------------------------------------------------------------------------------------------
// macOS (osascript -l JavaScript)

function readText(path) {
  var s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  if (!s || (typeof s.isNil === 'function' && s.isNil())) throw new Error('could not read ' + path);
  var text = s.js;
  if (typeof text !== 'string') throw new Error('could not read ' + path);
  return text;
}

function writeText(path, text) {
  // Atomically: a half-written manifest would stop the game from starting.
  var ok = $(text).writeToFileAtomicallyEncodingError(path, true, $.NSUTF8StringEncoding, null);
  if (!ok) throw new Error('could not write ' + path + ' (operation not permitted?)');
  JSON.parse(readText(path));      // it must read back as JSON, or throw
}

function run(argv) {
  ObjC.import('Foundation');
  var mode = argv[0], data = argv[1];
  if (!mode || !data) throw new Error('usage: unity-hook.js install|uninstall|status <Data folder>');
  var assembliesPath = data + '/ScriptingAssemblies.json';
  var initializersPath = data + '/RuntimeInitializeOnLoads.json';
  var out = transform(readText(assembliesPath), readText(initializersPath), mode);
  if (mode === 'status') return out.status;

  // Install lists the assembly before the method that lives in it; uninstall removes the method
  // first. Stopped half-way, either leaves an assembly nothing calls - harmless - never a call
  // into an assembly the game was not told to load.
  var order = mode === 'install'
    ? [[assembliesPath, out.assemblies], [initializersPath, out.initializers]]
    : [[initializersPath, out.initializers], [assembliesPath, out.assemblies]];
  var changed = false;
  order.forEach(function (w) {
    if (w[1] !== null) { writeText(w[0], w[1]); changed = true; }
  });
  return changed ? 'changed' : 'unchanged';
}
