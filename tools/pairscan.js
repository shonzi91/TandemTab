/* Hunt the defect shape this sweep keeps finding: a LIGHT rule that declares several selectors in one
   declaration, whose html.dark counterpart was written for only SOME of them. The forgotten siblings keep the
   light colour on a dark ground. Three were found by hand (.modal .modal-field, .app-legal-link, .warn-text);
   this checks whether more are hiding. */
var fs = require('fs'), path = require('path');
var COLOR = /(^|[\s;{])(color|background|background-color|border-color)\s*:/i;
var LITERAL = /#[0-9a-fA-F]{3,8}\b|rgba?\(/;

function strip(c) { return c.replace(/\/\*[\s\S]*?\*\//g, ''); }
function rules(css) {
  var out = [], i = 0;
  while (true) {
    var b = css.indexOf('{', i); if (b === -1) break;
    var parts = css.slice(i, b).split('}');
    var sel = parts[parts.length - 1].trim();
    var d = 1, j = b + 1;
    while (j < css.length && d > 0) { if (css[j] === '{') d++; else if (css[j] === '}') d--; j++; }
    var body = css.slice(b + 1, j - 1);
    if (sel.charAt(0) === '@') { if (sel.indexOf('@keyframes') !== 0) out = out.concat(rules(body)); }
    else out.push([sel, body]);
    i = j;
  }
  return out;
}
function norm(s) { return s.replace(/\s+/g, ' ').replace(/html\.dark\s+/g, '').trim(); }

var all = [];
process.argv.slice(2).forEach(function (p) {
  rules(strip(fs.readFileSync(p, 'utf8'))).forEach(function (r) {
    all.push({ file: path.basename(p), sel: r[0], body: r[1] });
  });
});

/* every selector that has SOME dark rule mentioning it */
var darkCovered = {};
all.forEach(function (r) {
  if (r.sel.indexOf('html.dark') === -1) return;
  r.sel.split(',').forEach(function (s) { darkCovered[norm(s)] = true; });
});

var findings = [];
all.forEach(function (r) {
  if (r.sel.indexOf('html.dark') !== -1) return;
  if (!COLOR.test(r.body) || !LITERAL.test(r.body)) return;
  var sels = r.sel.split(',').map(norm).filter(Boolean);
  if (sels.length < 2) return;                         /* only multi-selector declarations */
  var covered = sels.filter(function (s) { return darkCovered[s]; });
  var missing = sels.filter(function (s) { return !darkCovered[s]; });
  /* the smell: SOME siblings got a dark rule and some did not */
  if (covered.length && missing.length) {
    findings.push({ file: r.file, sel: r.sel.replace(/\s+/g, ' ').slice(0, 100), covered: covered, missing: missing });
  }
});

findings.forEach(function (f) {
  console.log('\n' + f.file + '  ' + f.sel);
  console.log('   has dark : ' + f.covered.join(' | '));
  console.log('   MISSING  : ' + f.missing.join(' | '));
});
console.log('\nTOTAL PARTIALLY-DARKENED RULES: ' + findings.length);
