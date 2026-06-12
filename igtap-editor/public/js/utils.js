// ── Preprocessing ──────────────────────────────────────────────
function preprocess(f) {
  return f
    .replace(/π/g,  'PI')
    .replace(/τ/g,  'TAU')
    .replace(/φ/g,  'PHI')
    .replace(/[ωΩ]/g, 'OMEGA')
    .replace(/Γ/g,  'gamma')
    .replace(/∞/g,  'Infinity')
    .replace(/\^/g, '**')
    .replace(/(\w+)!(?!=)/g, 'factorial($1)');  // n! → factorial(n), skip !=
}

// ── Gamma (Lanczos) ────────────────────────────────────────────
function _lnGamma(z) {
  const C = [
    0.99999999999980993,  676.5203681218851, -1259.1392167224028,
     771.32342877765313, -176.61502916214059,  12.507343278686905,
    -0.13857109526572012,   9.9843695780195716e-6, 1.5056327351493116e-7,
  ];
  if (z < 0.5) return Math.log(Math.PI / Math.sin(Math.PI * z)) - _lnGamma(1 - z);
  z--;
  let x = C[0];
  for (let i = 1; i < 9; i++) x += C[i] / (z + i);
  const t = z + 7.5;
  return 0.5 * Math.log(2 * Math.PI) + (z + 0.5) * Math.log(t) - t + Math.log(x);
}
function gamma(z) {
  if (z <= 0 && Number.isInteger(z)) return Infinity;
  return Math.exp(_lnGamma(z));
}

// ── Factorial ──────────────────────────────────────────────────
const _factCache = [1];
function factorial(n) {
  if (n < 0)               return gamma(n + 1);
  if (!Number.isInteger(n)) return gamma(n + 1);
  if (n > 170)              return Infinity;
  while (_factCache.length <= n) _factCache.push(_factCache[_factCache.length - 1] * _factCache.length);
  return _factCache[n];
}

// ── Fibonacci ──────────────────────────────────────────────────
const _fibCache = [0, 1];
function fib(n) {
  n = Math.abs(Math.round(n));
  if (n > 78) return Infinity;
  while (_fibCache.length <= n) _fibCache.push(_fibCache[_fibCache.length - 1] + _fibCache[_fibCache.length - 2]);
  return _fibCache[n];
}

// ── Combinations / Permutations ────────────────────────────────
function C(n, r) {
  n = Math.round(n); r = Math.round(r);
  if (r < 0 || r > n || n < 0) return 0;
  if (r === 0 || r === n) return 1;
  r = Math.min(r, n - r);
  let result = 1;
  for (let i = 0; i < r; i++) result = result * (n - i) / (i + 1);
  return Math.round(result);
}
function P(n, r) { return factorial(n) / factorial(n - r); }

// ── GCD / LCM ──────────────────────────────────────────────────
function gcd(a, b) {
  a = Math.abs(Math.round(a)); b = Math.abs(Math.round(b));
  while (b) { const t = b; b = a % b; a = t; }
  return a;
}
function lcm(a, b) { const g = gcd(a, b); return g === 0 ? 0 : Math.abs(Math.round(a) * Math.round(b)) / g; }

// ── Lambert W (principal branch, x ≥ −1/e) ────────────────────
function lambertW(x) {
  if (x === 0) return 0;
  if (x < -1 / Math.E) return NaN;
  let w = x < 1 ? x : Math.log(x);
  for (let i = 0; i < 64; i++) {
    const ew = Math.exp(w), wew = w * ew, d = wew - x, w1 = w + 1;
    const nw = w - d / (ew * w1 - (w + 2) * d / (2 * w1));
    if (Math.abs(nw - w) < 1e-12 * (1 + Math.abs(w))) return nw;
    w = nw;
  }
  return w;
}

// ── Error function (Horner / Abramowitz 7-term) ────────────────
function erf(x) {
  const t = 1 / (1 + 0.3275911 * Math.abs(x));
  const y = 1 - (((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t
    - 0.284496736) * t + 0.254829592) * t) * Math.exp(-x * x);
  return Math.sign(x) * y;
}

// ── Riemann zeta (partial sum, good for s > 1) ────────────────
function zeta(s, terms = 2000) {
  if (s <= 1) return Infinity;
  let sum = 0;
  for (let k = 1; k <= terms; k++) sum += Math.pow(k, -s);
  return sum;
}

// ── Utilities ──────────────────────────────────────────────────
function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
function lerp(a, b, t)     { return a + (b - a) * t; }
function smoothstep(lo, hi, x) {
  const t = clamp((x - lo) / (hi - lo), 0, 1);
  return t * t * (3 - 2 * t);
}
function smootherstep(lo, hi, x) {
  const t = clamp((x - lo) / (hi - lo), 0, 1);
  return t * t * t * (t * (t * 6 - 15) + 10);
}
function sigmoid(x)        { return 1 / (1 + Math.exp(-x)); }
function step(edge, x)     { return x < edge ? 0 : 1; }
function tri(n)            { return n * (n + 1) / 2; }           // triangular numbers
function mod(a, b)         { return ((a % b) + b) % b; }         // always-positive modulo
function frac(x)           { return x - Math.trunc(x); }
function logBase(b, x)     { return Math.log(x) / Math.log(b); }

// ── Scope ──────────────────────────────────────────────────────
function buildScope(n, cap) {
  return {
    // Variables
    n, cap,

    // ── Constants ──────────────────────────────────────────
    PI:    Math.PI,          pi:    Math.PI,
    TAU:   2 * Math.PI,      tau:   2 * Math.PI,
    E:     Math.E,           e:     Math.E,
    PHI:   1.6180339887498948, phi: 1.6180339887498948,   // golden ratio
    OMEGA: 0.5671432904097838, omega: 0.5671432904097838, // Lambert W(1)
    LN2:   Math.LN2,
    LN10:  Math.LN10,
    SQRT2: Math.SQRT2,
    SQRT3: Math.sqrt(3),
    INF:   Infinity,  Infinity,
    NaN,

    // ── Basic ───────────────────────────────────────────────
    pow:   Math.pow,   sqrt:  Math.sqrt,  cbrt:  Math.cbrt,
    abs:   Math.abs,   sign:  Math.sign,
    floor: Math.floor, ceil:  Math.ceil,  round: Math.round, trunc: Math.trunc,
    min:   Math.min,   max:   Math.max,   hypot: Math.hypot,
    clamp, mod, frac, lerp, step,

    // ── Trig ────────────────────────────────────────────────
    sin:   Math.sin,   cos:   Math.cos,   tan:   Math.tan,
    asin:  Math.asin,  acos:  Math.acos,  atan:  Math.atan,  atan2: Math.atan2,
    sinh:  Math.sinh,  cosh:  Math.cosh,  tanh:  Math.tanh,
    asinh: Math.asinh, acosh: Math.acosh, atanh: Math.atanh,

    // ── Log / Exp ───────────────────────────────────────────
    log:   Math.log,   log2:  Math.log2,  log10: Math.log10,
    log1p: Math.log1p, exp:   Math.exp,   expm1: Math.expm1,
    logBase,

    // ── Special functions ───────────────────────────────────
    factorial, fac: factorial,
    gamma, Γ: gamma,
    C, nCr: C, P, nPr: P,
    gcd, lcm,
    fib, fibonacci: fib,
    lambertW, W: lambertW,
    erf, erfc: x => 1 - erf(x),
    zeta,

    // ── Curve helpers ───────────────────────────────────────
    tri,
    smoothstep, smootherstep, sigmoid,
  };
}

// ── Public API ─────────────────────────────────────────────────
export function evalBoxFormula(formula, n, cap) {
  if (!formula?.trim()) return NaN;
  try {
    // new Function without "use strict" so `with` is allowed
    // eslint-disable-next-line no-new-func
    return +new Function('_s', `with(_s){return(${preprocess(formula)});}`)(buildScope(n, cap));
  } catch { return NaN; }
}

export function computePrices(formula, cap) {
  const c = Math.max(1, cap | 0);
  return Array.from({ length: c }, (_, i) => {
    const p = evalBoxFormula(formula, i + 1, c);
    return (isFinite(p) && p >= 0) ? p : 0;
  });
}
