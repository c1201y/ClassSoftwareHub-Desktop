/* ═══════════════════════════════════════════════════════════════════
   函数图像 · Web 载体逻辑层
   ───────────────────────────────────────────────────────────────────
   与原生 FunctionPlotPage 的对应关系：
   · 绘制         ↔ FunctionPlot.cs / Plot_Wheel / Plot_Pressed
   · 表达式       ↔ ExpressionParser.cs（这里是等价的 JS 递归下降版）
   · 闭式分析     ↔ FunctionClosedForm.cs（这里是子集：只认 a·f(x)+c 的标准形式，
                   ⛔ 绝不按当前视口采样当结论 —— 拖动视图数字就变，那不是函数性质）
   · 数学键盘     ↔ KeyCap[] / BuildKeypad / Keypad_Click
   ───────────────────────────────────────────────────────────────────
   ⛔ 无超时、无重试：宿主一次判定有 WebView 就走这里，没有就走原生。
   ⛔ 不自绘标题栏、不引入假拖拽区。
   ═══════════════════════════════════════════════════════════════════ */
(function () {
  'use strict';

  // ────────────────────────────── DOM ──────────────────────────────
  var root = document.getElementById('root');
  var cv = document.getElementById('cv');
  var ctx = cv.getContext('2d');
  var curveList = document.getElementById('curveList');
  var keypad = document.getElementById('keypad');
  var targetBox = document.getElementById('targetBox');
  var analysisList = document.getElementById('analysisList');
  var analysisEmpty = document.getElementById('analysisEmpty');
  var analysisMode = document.getElementById('analysisMode');
  var basisNote = document.getElementById('basisNote');

  var view = { scale: 40, cx: 0, cy: 0 };   // scale = 每单位像素数
  var mode = 'rad';                          // rad | deg
  var curves = [];                           // {box, dot, tip, color, fn, err}
  var colorIndex = 0;
  var keypadTarget = null;
  var analysisCollapsed = false;

  var PALETTE_SIZE = 8;

  function css(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }
  function curveColor(i) { return 'var(--curve-' + (i % PALETTE_SIZE) + ')'; }

  // 主题相关的绘制参数做缓存：paint 每帧都跑，不能每次都 getComputedStyle。
  // 主题一变（宿主注入 data-theme）就整体刷新。
  var themeVars = {};
  var curveColors = [];
  function refreshTheme() {
    var st = getComputedStyle(document.documentElement);
    themeVars.grid = st.getPropertyValue('--grid').trim();
    themeVars.axis = st.getPropertyValue('--axis').trim();
    themeVars.axisText = st.getPropertyValue('--axis-text').trim();
    curveColors = [];
    for (var i = 0; i < PALETTE_SIZE; i++) {
      curveColors.push(st.getPropertyValue('--curve-' + i).trim() || '#888888');
    }
  }

  // ══════════════════════════ 表达式：词法 + 语法 ══════════════════════

  var FUNCS = ['sin', 'cos', 'tan', 'asin', 'acos', 'atan',
               'ln', 'log', 'sqrt', 'abs', 'exp', 'floor', 'round', 'sign'];

  function tokenize(src) {
    var s = String(src || '')
      .replace(/\u00d7/g, '*').replace(/\u00f7/g, '/')     // × ÷
      .replace(/\u2212/g, '-')                              // − (minus sign)
      .replace(/\u03c0/g, 'pi').replace(/pi/g, 'pi');
    var out = [];
    var i = 0;
    while (i < s.length) {
      var c = s[i];
      if (c === ' ' || c === '\t' || c === '\n') { i++; continue; }
      if (/[0-9]/.test(c) || (c === '.' && /[0-9]/.test(s[i + 1] || ''))) {
        var j = i;
        while (j < s.length && /[0-9.]/.test(s[j])) j++;
        out.push({ k: 'num', v: parseFloat(s.slice(i, j)) });
        i = j; continue;
      }
      if (/[A-Za-z]/.test(c)) {
        var k = i;
        while (k < s.length && /[A-Za-z]/.test(s[k])) k++;
        out.push({ k: 'id', v: s.slice(i, k).toLowerCase() });
        i = k; continue;
      }
      if (c === '\u00b2') { out.push({ k: 'op', v: '^' }); out.push({ k: 'num', v: 2 }); i++; continue; }
      if (c === '\u00b3') { out.push({ k: 'op', v: '^' }); out.push({ k: 'num', v: 3 }); i++; continue; }
      // √ 作为前缀运算符进 tokenizer（不能替换成 sqrt —— 那会和后面的 x 粘成一个标识符）
      if (c === '\u221a') { out.push({ k: 'op', v: 'sqrt' }); i++; continue; }
      // |x| 手输也认（键盘上「|x|」那颗键插的是 abs()，这里兼容直接敲竖线）
      if (c === '|') {
        var end = s.indexOf('|', i + 1);
        if (end < 0) throw new Error('绝对值少了一个「|」');
        out.push({ k: 'id', v: 'abs' }, { k: 'op', v: '(' });
        out.push.apply(out, tokenize(s.slice(i + 1, end)));
        out.push({ k: 'op', v: ')' });
        i = end + 1;
        continue;
      }
      if ('+-*/^(),'.indexOf(c) >= 0) { out.push({ k: 'op', v: c }); i++; continue; }
      throw new Error('表达式里有不支持的字符「' + c + '」');
    }
    return out;
  }

  // 递归下降。优先级：+ - < * / < 一元负 < ^（右结合）< 后缀/调用
  function parse(src) {
    var ts = tokenize(src);
    var p = 0;

    function peek() { return ts[p]; }
    function eat(v) {
      var t = ts[p];
      if (!t || t.k !== 'op' || t.v !== v) throw new Error('这里本该是「' + v + '」');
      p++; return t;
    }
    function isOp(v) { var t = ts[p]; return t && t.k === 'op' && t.v === v; }
    function startsPrimary() {
      var t = ts[p];
      if (!t) return false;
      if (t.k === 'num' || t.k === 'id') return true;
      if (t.k === 'op' && t.v === '(') return true;
      return false;
    }

    function expr() {
      var node = term();
      while (isOp('+') || isOp('-')) {
        var op = eat(ts[p].v).v;
        node = { t: 'bin', op: op, l: node, r: term() };
      }
      return node;
    }
    function term() {
      var node = unary();
      for (;;) {
        if (isOp('*') || isOp('/')) {
          var op = eat(ts[p].v).v;
          node = { t: 'bin', op: op, l: node, r: unary() };
        } else if (startsPrimary()) {
          // 隐式乘法：2x、2sin(x)、x(x+1)、)( 都按乘算
          node = { t: 'bin', op: '*', l: node, r: unary() };
        } else break;
      }
      return node;
    }
    function unary() {
      if (isOp('-')) { eat('-'); return { t: 'neg', e: unary() }; }
      if (isOp('+')) { eat('+'); return unary(); }
      // √x^2 取 sqrt(x^2)：根号覆盖整个幂，跟板书一致
      if (isOp('sqrt')) { eat('sqrt'); return { t: 'call', fn: 'sqrt', args: [power()] }; }
      return power();
    }
    function power() {
      var base = postfix();
      if (isOp('^')) {
        eat('^');
        return { t: 'bin', op: '^', l: base, r: unary() };   // 右结合
      }
      return base;
    }
    function postfix() { return primary(); }

    function primary() {
      var t = peek();
      if (!t) throw new Error('表达式不完整');
      if (t.k === 'num') { p++; return { t: 'num', v: t.v }; }
      if (t.k === 'op' && t.v === '(') {
        eat('(');
        var e = expr();
        eat(')');
        return e;
      }
      if (t.k === 'id') {
        p++;
        var name = t.v;
        if (name === 'x') return { t: 'var', name: 'x' };
        if (name === 'pi') return { t: 'const', name: 'pi' };
        if (name === 'e') return { t: 'const', name: 'e' };
        if (FUNCS.indexOf(name) >= 0) {
          if (!isOp('(')) throw new Error(name + ' 后面要加括号');
          eat('(');
          var args = [expr()];
          while (isOp(',')) { eat(','); args.push(expr()); }
          eat(')');
          return { t: 'call', fn: name, args: args };
        }
        throw new Error('不认识的符号「' + name + '」');
      }
      throw new Error('这里看不懂');
    }

    var ast = expr();
    if (p < ts.length) throw new Error('表达式后面还有多余的内容');
    return ast;
  }

  // ── 编译成可执行函数 ──
  // 角度制：三角函数的入参按"度"解释（原生 AngleMode.Deg 同一语义）
  function emit(node) {
    switch (node.t) {
      case 'num': return '(' + node.v + ')';
      case 'var': return 'x';
      case 'const': return node.name === 'pi' ? 'Math.PI' : 'Math.E';
      case 'neg': return '(-' + emit(node.e) + ')';
      case 'bin':
        if (node.op === '^') {
          // 幂：底数为负且指数非整数会出 NaN，交给调用方判断
          return 'Math.pow(' + emit(node.l) + ',' + emit(node.r) + ')';
        }
        return '(' + emit(node.l) + node.op + emit(node.r) + ')';
      case 'call': {
        var a = node.args.map(emit);
        if (node.fn === 'log') {
          return a.length >= 2
            ? '(Math.log(' + a[0] + ')/Math.log(' + a[1] + '))'
            : 'Math.log10(' + a[0] + ')';
        }
        if (node.fn === 'ln') return 'Math.log(' + a[0] + ')';
        if (node.fn === 'sin' || node.fn === 'cos' || node.fn === 'tan') {
          var k = mode === 'deg' ? '*(Math.PI/180)' : '';
          return 'Math.' + node.fn + '(' + a[0] + k + ')';
        }
        if (node.fn === 'sign') return '(' + a[0] + '>0?1:(' + a[0] + '<0?-1:0))';
        if (node.fn === 'abs' || node.fn === 'exp' || node.fn === 'sqrt' ||
            node.fn === 'floor' || node.fn === 'round' ||
            node.fn === 'asin' || node.fn === 'acos' || node.fn === 'atan') {
          return 'Math.' + node.fn + '(' + a[0] + ')';
        }
        throw new Error('不认识的函数 ' + node.fn);
      }
    }
    throw new Error('表达式结构有问题');
  }

  function compile(src) {
    var ast = parse(src);
    /* eslint-disable no-new-func */
    var fn = new Function('x', 'try{return ' + emit(ast) + '}catch(e){return NaN}');
    fn.ast = ast;
    return fn;
  }

  // ──────────────────────── 闭式分析（FunctionClosedForm 子集）───────────────────────

  function num(n) { return { t: 'num', v: n }; }
  function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }

  // 把 AST 摊成 a·inner + b（a、b 为常数）。inner 结构不同即失败。
  // ⚠️ `^` 整个节点当作 inner（x^2 是幂函数，2^x 是指数函数，交给 classify 分辨）。
  function decompose(node) {
    switch (node.t) {
      case 'num': return { a: 0, b: node.v, inner: null };
      case 'const': return null;                       // π / e 视为常数，不进标准形式
      case 'var': return { a: 1, b: 0, inner: node };
      case 'neg': {
        var dn = decompose(node.e);
        if (!dn) return null;
        return { a: -dn.a, b: -dn.b, inner: dn.inner };
      }
      case 'call': return { a: 1, b: 0, inner: node };
      case 'bin':
        if (node.op === '^') return { a: 1, b: 0, inner: node };
        break;
      default: return null;
    }
    if (node.t !== 'bin') return null;

    if (node.op === '+') {
      var L = decompose(node.l), R = decompose(node.r);
      if (!L || !R) return null;
      if (L.inner && R.inner && !same(L.inner, R.inner)) return null;
      return { a: L.a + R.a, b: L.b + R.b, inner: L.inner || R.inner };
    }
    if (node.op === '-') {
      var L2 = decompose(node.l), R2 = decompose(node.r);
      if (!L2 || !R2) return null;
      if (L2.inner && R2.inner && !same(L2.inner, R2.inner)) return null;
      return { a: L2.a - R2.a, b: L2.b - R2.b, inner: L2.inner || R2.inner };
    }
    if (node.op === '*') {
      var A = decompose(node.l), B = decompose(node.r);
      if (!A || !B) return null;
      // 常数×inner、常数×常数、inner×常数；inner×inner 不属标准形式
      if (!A.inner && B.inner) return { a: A.a * B.b + A.b * B.a, b: A.b * B.b, inner: B.inner };
      if (A.inner && !B.inner) return { a: A.a * B.b + A.b * B.a, b: A.b * B.b, inner: A.inner };
      if (!A.inner && !B.inner) return { a: A.a * B.b + A.b * B.a, b: A.b * B.b, inner: null };
      return null;
    }
    if (node.op === '/') {
      var Ld = decompose(node.l), Rd = decompose(node.r);
      if (!Ld || !Rd) return null;
      if (Rd.inner) return null;                       // 除以含变量的部分 → 商式，不收
      if (Rd.a !== 0) return null;
      if (Math.abs(Rd.b) < 1e-12) return null;
      return { a: Ld.a / Rd.b, b: Ld.b / Rd.b, inner: Ld.inner };
    }
    return null;                                       // ^ 不进这里，inner 直接是 pow 节点
  }

  function fmtNum(v) {
    if (!isFinite(v)) return null;
    var s = Number(v.toFixed(6)).toString();
    return s;
  }
  function PI(deg) { return deg ? '180°' : 'π'; }
  function PI2(deg) { return deg ? '360°' : '2π'; }
  function PIH(deg) { return deg ? '90°' : 'π/2'; }

  // 值域区间按 a·f+b 缩放；leftClosed/rightClosed 分别指定两端开闭
  function scaleRange(fLo, fHi, a, b, leftClosed, rightClosed) {
    var lo = b + a * fLo, hi = b + a * fHi;
    if (a < 0) {
      var t = lo; lo = hi; hi = t;
      var lc = leftClosed; leftClosed = rightClosed; rightClosed = lc;   // 端点跟着翻
    }
    function s(v) {
      if (v === Infinity) return '+∞';
      if (v === -Infinity) return '-∞';
      return fmtNum(v);
    }
    return (leftClosed ? '[' : '(') + s(lo) + ', ' + s(hi) + (rightClosed ? ']' : ')');
  }

  // 识别 inner 属于哪一类标准形式
  function classify(inner) {
    if (!inner) return null;
    if (inner.t === 'var') return { kind: 'identity' };
    if (inner.t === 'call') {
      if (['sin', 'cos', 'tan', 'asin', 'acos', 'atan', 'exp', 'ln', 'log', 'abs', 'sqrt']
        .indexOf(inner.fn) >= 0 && inner.args.length === 1 && inner.args[0].t === 'var') {
        return { kind: inner.fn };
      }
      if (inner.fn === 'log' && inner.args.length === 2) return null;
      return null;
    }
    if (inner.t === 'bin' && inner.op === '^') {
      var base = inner.l, ex = inner.r;
      if (base.t === 'var' && ex.t === 'num') return { kind: 'power', n: ex.v };
      if (ex.t === 'var' && base.t === 'num' && base.v > 0) return { kind: 'expower', base: base.v };
      return null;
    }
    return null;
  }

  function analyze(src) {
    var ast;
    try { ast = parse(src); } catch (e) { return null; }

    var d = decompose(ast);
    if (!d) return null;

    var a = d.a, b = d.b;
    var inner = d.inner;

    // 纯常数
    if (!inner) {
      return {
        title: '常数函数',
        fields: [
          ['标准形式', 'y = ' + fmtNum(b)],
          ['定义域', 'x ∈ ℝ'],
          ['值域', '{' + fmtNum(b) + '}'],
          ['单调性', '常数，不增不减'],
          ['周期性', '常数没有周期'],
          ['奇偶性', '偶函数'],
          ['渐近线', '无']
        ]
      };
    }

    var c = classify(inner);
    if (!c) return null;
    var deg = mode === 'deg';
    var F = [];   // [标签, 值]

    if (c.kind === 'identity') {
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '一次函数']);
      F.push(['定义域', 'x ∈ ℝ']);
      F.push(['值域', 'ℝ']);
      F.push(['单调性', a > 0 ? '单调递增' : '单调递减']);
      F.push(['零点', 'x = ' + fmtNum(-b / a)]);
      F.push(['奇偶性', b === 0 ? '奇函数' : '非奇非偶']);
      F.push(['渐近线', '无']);
      return { title: '一次函数', fields: F };
    }

    if (c.kind === 'power') {
      var n = c.n;
      if (!Number.isInteger(n)) return null;
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '幂函数 y = a·x' + sup(n)]);
      if (n > 0) {
        F.push(['定义域', 'x ∈ ℝ']);
        if (n % 2 === 1) {
          F.push(['值域', 'ℝ']);
          F.push(['奇偶性', b === 0 ? '奇函数' : '非奇非偶']);
          F.push(['单调性', a > 0 ? '单调递增' : '单调递减']);
          F.push(['极值', '无（函数无界）']);
        } else {
          F.push(['值域', scaleRange(0, Infinity, a, b, true, false)]);
          F.push(['奇偶性', b === 0 ? '偶函数' : '非奇非偶']);
          F.push(['单调性', a > 0 ? '(-∞, 0) 递减，(0, +∞) 递增' : '(-∞, 0) 递增，(0, +∞) 递减']);
          F.push(['极值', 'x = 0 处取' + (a > 0 ? '最小' : '最大') + '值 ' + fmtNum(b)]);
        }
        F.push(['零点', n % 2 === 1 ? 'x = 0' : (b === 0 ? 'x = 0' : '无')]);
      } else {
        F.push(['定义域', 'x ≠ 0']);
        F.push(['值域', a > 0 ? '(0, +∞)' : '(-∞, 0)']);
        F.push(['奇偶性', b === 0 ? (n % 2 === 0 ? '偶函数' : '奇函数') : '非奇非偶']);
        F.push(['单调性', a > 0 ? '(-∞, 0) 递减，(0, +∞) 递减' : '(-∞, 0) 递增，(0, +∞) 递增']);
        F.push(['渐近线', 'x = 0（沿定义域边界）']);
      }
      return { title: '幂函数', fields: F };
    }

    if (c.kind === 'sin' || c.kind === 'cos' || c.kind === 'tan') {
      var amp = Math.abs(a);
      var mid = b;
      if (c.kind === 'sin' || c.kind === 'cos') {
        F.push(['标准形式', c.kind === 'sin' ? '正弦型 y = a·sin x + b' : '余弦型 y = a·cos x + b']);
        F.push(['定义域', 'x ∈ ℝ']);
        F.push(['值域', scaleRange(-1, 1, a, b, true, true)]);
        F.push(['振幅', fmtNum(amp)]);
        F.push(['周期', 'T = ' + PI2(deg)]);
        F.push(['奇偶性', b !== 0 ? '非奇非偶' : (c.kind === 'sin' ? '奇函数' : '偶函数')]);
        F.push(['单调性', '随相位在 [' + fmtNum(mid - amp) + ', ' + fmtNum(mid + amp) + '] 间起伏']);
        F.push(['渐近线', '无']);
      } else {
        F.push(['标准形式', '正切型 y = a·tan x + b']);
        F.push(['定义域', 'x ≠ ' + PIH(deg) + ' + k' + (deg ? '·180°' : 'π')]);
        F.push(['值域', 'ℝ']);
        F.push(['周期', 'T = ' + PI(deg)]);
        F.push(['奇偶性', b !== 0 ? '非奇非偶' : '奇函数']);
        F.push(['极值', '无（函数无界）']);
        F.push(['渐近线', 'x = ' + PIH(deg) + ' + k' + (deg ? '·180°' : 'π')]);
      }
      return { title: c.kind === 'tan' ? '正切型' : (c.kind === 'sin' ? '正弦型' : '余弦型'), fields: F };
    }

    if (c.kind === 'asin' || c.kind === 'acos' || c.kind === 'atan') {
      var lo = c.kind === 'asin' ? -1 : 0, hi = c.kind === 'atan' ? Infinity : 1;
      var dom = c.kind === 'atan' ? 'x ∈ ℝ' : 'x ∈ [' + lo + ', ' + hi + ']';
      F.push(['标准形式', '反三角函数']);
      F.push(['定义域', dom]);
      if (c.kind === 'atan') F.push(['值域', scaleRange(-Math.PI / 2, Math.PI / 2, a, b, false, false)]);
      else if (c.kind === 'asin') F.push(['值域', scaleRange(-Math.PI / 2, Math.PI / 2, a, b, true, true)]);
      else F.push(['值域', scaleRange(0, Math.PI, a, b, true, true)]);
      F.push(['奇偶性', '非奇非偶']);
      F.push(['渐近线', c.kind === 'atan' ? 'y = ' + fmtNum(b + a * Math.PI / 2) + ' 与 y = ' + fmtNum(b - a * Math.PI / 2) : '无']);
      return { title: '反三角函数', fields: F };
    }

    if (c.kind === 'exp' || c.kind === 'expower') {
      var base = c.kind === 'exp' ? 'e' : fmtNum(c.base);
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '指数函数 y = ' + fmtNum(a) + '·' + base + 'ˣ + ' + fmtNum(b)]);
      F.push(['定义域', 'x ∈ ℝ']);
      F.push(['值域', a > 0 ? '(' + fmtNum(b) + ', +∞)' : '(-∞, ' + fmtNum(b) + ')']);
      F.push(['单调性', a > 0 ? '单调递增' : '单调递减']);
      F.push(['奇偶性', '非奇非偶']);
      F.push(['渐近线', 'y = ' + fmtNum(b)]);
      return { title: '指数函数', fields: F };
    }

    if (c.kind === 'ln' || c.kind === 'log') {
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '对数函数 y = a·ln x + b']);
      F.push(['定义域', '(0, +∞)']);
      F.push(['值域', 'ℝ']);
      F.push(['单调性', a > 0 ? '单调递增' : '单调递减']);
      F.push(['奇偶性', '非奇非偶']);
      F.push(['零点', 'x = ' + fmtNum(Math.exp(-b / a))]);
      F.push(['渐近线', 'x = 0（沿定义域边界）']);
      return { title: '对数函数', fields: F };
    }

    if (c.kind === 'abs') {
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '绝对值函数 y = a·|x| + b']);
      F.push(['定义域', 'x ∈ ℝ']);
      F.push(['值域', a > 0 ? '[' + fmtNum(b) + ', +∞)' : '(-∞, ' + fmtNum(b) + ']']);
      F.push(['奇偶性', b === 0 ? '偶函数' : '非奇非偶']);
      F.push(['单调性', a > 0 ? '(-∞, 0) 递减，(0, +∞) 递增' : '(-∞, 0) 递增，(0, +∞) 递减']);
      F.push(['极值', 'x = 0 处取' + (a > 0 ? '最小' : '最大') + '值 ' + fmtNum(b)]);
      F.push(['渐近线', '无']);
      return { title: '绝对值函数', fields: F };
    }

    if (c.kind === 'sqrt') {
      if (a === 0) return analyze(fmtNum(b));
      F.push(['标准形式', '平方根函数 y = a·√x + b']);
      F.push(['定义域', '[0, +∞)']);
      F.push(['值域', a > 0 ? '[' + fmtNum(b) + ', +∞)' : '(-∞, ' + fmtNum(b) + ']']);
      F.push(['单调性', a > 0 ? '单调递增' : '单调递减']);
      F.push(['奇偶性', '非奇非偶']);
      F.push(['零点', a > 0 && b <= 0 ? 'x = ' + fmtNum((b / a) * (b / a)) : '无']);
      F.push(['渐近线', '无']);
      return { title: '平方根函数', fields: F };
    }

    return null;
  }

  function sup(n) { return '<sup>' + n + '</sup>'; }
  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  // 轻量数学排版：x^2 → x<sup>2</sup>，其余 Unicode（π √ ℝ ² ³）直接显示
  function mathHtml(s) {
    return esc(s).replace(/\^\{?(-?\d+)\}?/g, sup('$1'));
  }

  // ──────────────────────────── 曲线管理 ────────────────────────────

  function addCurve(text, focus) {
    var idx = colorIndex++ % PALETTE_SIZE;

    var row = document.createElement('div');
    row.className = 'curve-row';

    var dot = document.createElement('span');
    dot.className = 'curve-dot';
    dot.style.background = curveColor(idx);

    var prefix = document.createElement('span');
    prefix.className = 'curve-prefix';
    prefix.textContent = 'y =';

    var box = document.createElement('input');
    box.className = 'win-textbox';
    box.type = 'text';
    box.value = text || '';
    box.placeholder = '例如 sin(x)、x² − 3x + 1、2ˣ';
    box.spellcheck = false;
    box.autocomplete = 'off';
    box.setAttribute('autocapitalize', 'off');
    // ⚠️ 声明成公式输入：中文输入法默认中文态，直接敲 sin(x) 会变成拼音合成
    box.setAttribute('inputmode', 'text');

    var remove = document.createElement('button');
    remove.className = 'win-btn win-btn-icon curve-remove';
    remove.title = '移除这条曲线';
    remove.innerHTML = '<span class="glyph">&#xE74D;</span>';

    var tip = document.createElement('div');
    tip.className = 'curve-tip';
    tip.hidden = true;

    row.appendChild(dot);
    row.appendChild(prefix);
    row.appendChild(box);
    row.appendChild(remove);
    row.appendChild(tip);
    curveList.appendChild(row);

    var entry = { row: row, box: box, dot: dot, tip: tip, color: curveColor(idx), index: idx, fn: null, err: null };
    curves.push(entry);

    box.addEventListener('input', function () { recalc(); });
    box.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { recalc(); }
      e.stopPropagation();
    });
    // WinUI 3 没有 TextBox.HasFocus：靠 focus 记住"最后在编辑哪条"
    box.addEventListener('focus', function () { keypadTarget = box; });

    remove.addEventListener('click', function () { removeCurve(entry); });

    recalc();
    if (focus) box.focus();
    return entry;
  }

  function removeCurve(entry) {
    var i = curves.indexOf(entry);
    if (i < 0) return;
    curves.splice(i, 1);
    entry.row.remove();
    if (keypadTarget === entry.box) keypadTarget = curves.length ? curves[curves.length - 1].box : null;
    recalc();
  }

  // 重算所有曲线 + 重绘 + 刷新分析
  function recalc() {
    var anyOk = false;
    var firstOk = -1;

    for (var i = 0; i < curves.length; i++) {
      var c = curves[i];
      var text = c.box.value.trim();
      c.fn = null; c.err = null;
      if (text) {
        try {
          c.fn = compile(text);
          anyOk = true;
          if (firstOk < 0) firstOk = i;
        } catch (e) {
          c.err = e.message;
        }
      }
      if (c.err) {
        c.tip.textContent = '表达式看不懂：' + c.err;
        c.tip.hidden = false;
      } else {
        c.tip.hidden = true;
      }
    }

    // 目标下拉：多于一条可绘制曲线时才出现（原生 TargetBox 默认收起）
    var okCount = curves.filter(function (c) { return c.fn; }).length;
    if (okCount > 1) {
      var keep = targetBox.value;
      targetBox.hidden = false;
      targetBox.innerHTML = '';
      curves.forEach(function (c, i) {
        if (!c.fn) return;
        var o = document.createElement('option');
        o.value = String(i);
        o.textContent = 'y = ' + c.box.value.trim();
        targetBox.appendChild(o);
      });
      if (keep) targetBox.value = keep;
    } else {
      targetBox.hidden = true;
    }

    renderAnalysis(firstOk);
    paint();
  }

  // ──────────────────────── 分析栏渲染 ────────────────────────

  function renderAnalysis(firstOk) {
    var target = -1;
    if (curves.length) {
      var sel = parseInt(targetBox.value, 10);
      target = (okCountOf(targetBox) > 1 && !isNaN(sel)) ? sel : firstOk;
    }
    var curve = target >= 0 ? curves[target] : null;

    if (!curve || !curve.fn) {
      analysisEmpty.hidden = false;
      analysisEmpty.textContent = curve ? '当前表达式无法解析，暂不能分析。'
                                        : '输入表达式后显示分析结果。';
      analysisMode.textContent = '';
      analysisList.innerHTML = '';
      return;
    }

    var r = analyze(curve.box.value);
    if (!r) {
      analysisMode.textContent = '无解析结论';
      analysisEmpty.hidden = false;
      analysisEmpty.textContent = '该表达式暂无可给出的闭式结论。';
      analysisList.innerHTML = '';
      return;
    }

    analysisMode.textContent = '精确结论';
    analysisEmpty.hidden = true;
    analysisList.innerHTML = '';
    r.fields.forEach(function (f) {
      var row = document.createElement('div');
      row.className = 'ana-field';
      var label = document.createElement('div');
      label.className = 'label';
      label.textContent = f[0];
      var value = document.createElement('div');
      value.className = 'value';
      value.innerHTML = mathHtml(f[1]);
      row.appendChild(label);
      row.appendChild(value);
      analysisList.appendChild(row);
    });
  }

  function okCountOf() {
    return curves.filter(function (c) { return c.fn; }).length;
  }

  // ──────────────────────────── 绘制 ────────────────────────────

  function resize() {
    var r = cv.getBoundingClientRect();
    var dpr = window.devicePixelRatio || 1;
    cv.width = Math.max(1, Math.round(r.width * dpr));
    cv.height = Math.max(1, Math.round(r.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    paint();
  }

  function toPx(x) { return cv.clientWidth / 2 + (x - view.cx) * view.scale; }
  function toPy(y) { return cv.clientHeight / 2 - (y - view.cy) * view.scale; }

  function fmtTick(v) {
    var d = Math.abs(v) < 10 ? 1 : 0;
    var s = Number(v).toFixed(d);
    return s.replace(/\.0+$/, '').replace(/(\.\d*?)0+$/, '$1');
  }

  function paint() {
    var w = cv.clientWidth, h = cv.clientHeight;
    if (!w || !h) return;
    ctx.clearRect(0, 0, w, h);

    // 网格步长：让格子保持在 40~120px 之间
    var step = 1;
    while (step * view.scale < 40) step *= 2;
    while (step * view.scale > 120) step /= 2;

    var x0 = view.cx - w / 2 / view.scale, x1 = view.cx + w / 2 / view.scale;
    var y0 = view.cy - h / 2 / view.scale, y1 = view.cy + h / 2 / view.scale;

    ctx.lineWidth = 1;
    ctx.strokeStyle = themeVars.grid;
    ctx.beginPath();
    for (var x = Math.ceil(x0 / step) * step; x <= x1; x += step) {
      var px = Math.round(toPx(x)) + .5;
      ctx.moveTo(px, 0); ctx.lineTo(px, h);
    }
    for (var y = Math.ceil(y0 / step) * step; y <= y1; y += step) {
      var py = Math.round(toPy(y)) + .5;
      ctx.moveTo(0, py); ctx.lineTo(w, py);
    }
    ctx.stroke();

    // 坐标轴
    ctx.strokeStyle = themeVars.axis;
    ctx.beginPath();
    var ax = Math.round(toPx(0)) + .5, ay = Math.round(toPy(0)) + .5;
    ctx.moveTo(ax, 0); ctx.lineTo(ax, h);
    ctx.moveTo(0, ay); ctx.lineTo(w, ay);
    ctx.stroke();

    // 刻度数字（贴着轴，不越界）
    ctx.fillStyle = themeVars.axisText;
    ctx.font = '11px "Segoe UI", "Microsoft YaHei UI", sans-serif';
    var ty = Math.min(Math.max(ay + 12, 12), h - 4);
    for (var tx = Math.ceil(x0 / step) * step; tx <= x1; tx += step) {
      if (Math.abs(tx) < 1e-9) continue;
      ctx.fillText(fmtTick(tx), Math.min(Math.max(toPx(tx) + 3, 3), w - 26), ty);
    }
    var txx = Math.min(Math.max(ax + 5, 4), w - 26);
    for (var tyy = Math.ceil(y0 / step) * step; tyy <= y1; tyy += step) {
      if (Math.abs(tyy) < 1e-9) continue;
      ctx.fillText(fmtTick(tyy), txx, Math.min(Math.max(toPy(tyy) - 4, 12), h - 4));
    }

    // 曲线
    for (var i = 0; i < curves.length; i++) {
      var c = curves[i];
      if (!c.fn) continue;
      ctx.strokeStyle = curveColors[c.index % PALETTE_SIZE] || '#888888';
      ctx.lineWidth = 2;
      ctx.beginPath();
      var started = false;
      for (var sx = 0; sx <= w; sx += 1) {
        var wx = view.cx + (sx - w / 2) / view.scale;
        var wy = c.fn(wx);
        if (typeof wy !== 'number' || !isFinite(wy)) { started = false; continue; }
        var sy = toPy(wy);
        if (sy < -4000 || sy > 4000) { started = false; continue; }
        if (!started) { ctx.moveTo(sx, sy); started = true; }
        else ctx.lineTo(sx, sy);
      }
      ctx.stroke();
    }
  }

  // ──────────────────────── 视图交互 ────────────────────────

  cv.addEventListener('wheel', function (e) {
    e.preventDefault();
    var r = cv.getBoundingClientRect();
    var mx = e.clientX - r.left, my = e.clientY - r.top;
    var wx = view.cx + (mx - cv.clientWidth / 2) / view.scale;
    var wy = view.cy - (my - cv.clientHeight / 2) / view.scale;
    var k = e.deltaY < 0 ? 1.12 : 1 / 1.12;
    view.scale = Math.min(2000, Math.max(4, view.scale * k));
    view.cx = wx - (mx - cv.clientWidth / 2) / view.scale;
    view.cy = wy + (my - cv.clientHeight / 2) / view.scale;
    paint();
  }, { passive: false });

  var drag = null;
  cv.addEventListener('pointerdown', function (e) {
    drag = { x: e.clientX, y: e.clientY, cx: view.cx, cy: view.cy };
    cv.classList.add('dragging');
    try { cv.setPointerCapture(e.pointerId); } catch (_) { }
  });
  cv.addEventListener('pointermove', function (e) {
    if (!drag) return;
    view.cx = drag.cx - (e.clientX - drag.x) / view.scale;
    view.cy = drag.cy + (e.clientY - drag.y) / view.scale;
    paint();
  });
  function endDrag(e) {
    drag = null;
    cv.classList.remove('dragging');
    try { cv.releasePointerCapture(e.pointerId); } catch (_) { }
  }
  cv.addEventListener('pointerup', endDrag);
  cv.addEventListener('pointercancel', endDrag);

  function zoomAt(factor, cxPx, cyPx) {
    var wx = view.cx + (cxPx - cv.clientWidth / 2) / view.scale;
    var wy = view.cy - (cyPx - cv.clientHeight / 2) / view.scale;
    view.scale = Math.min(2000, Math.max(4, view.scale * factor));
    view.cx = wx - (cxPx - cv.clientWidth / 2) / view.scale;
    view.cy = wy + (cyPx - cv.clientHeight / 2) / view.scale;
    paint();
  }

  document.getElementById('zoomInBtn').addEventListener('click', function () {
    zoomAt(1.2, cv.clientWidth / 2, cv.clientHeight / 2);
  });
  document.getElementById('zoomOutBtn').addEventListener('click', function () {
    zoomAt(1 / 1.2, cv.clientWidth / 2, cv.clientHeight / 2);
  });
  document.getElementById('resetBtn').addEventListener('click', function () {
    view.scale = 40; view.cx = 0; view.cy = 0; paint();
  });

  // ──────────────────────── 数学键盘 ────────────────────────
  // 与原生 KeyCap[] 逐颗对应：展示名、插入内容、是否包裹选区、悬浮说明
  var CAPS = [
    ['sin', 'sin()', 1, 'sin({0})'],
    ['cos', 'cos()', 1, 'cos({0})'],
    ['tan', 'tan()', 1, 'tan({0})'],
    ['7', '7'], ['8', '8'], ['9', '9'],

    ['asin', 'asin()', 1, 'asin({0})', '反正弦'],
    ['acos', 'acos()', 1, 'acos({0})', '反余弦'],
    ['atan', 'atan()', 1, 'atan({0})', '反正切'],
    ['4', '4'], ['5', '5'], ['6', '6'],

    ['ln', 'ln()', 1, 'ln({0})', '自然对数'],
    ['log', 'log()', 1, 'log({0})', '单参数按常用对数算；写 log(x, 2) 是换底'],
    ['√', 'sqrt()', 1, 'sqrt({0})', '算术平方根'],
    ['1', '1'], ['2', '2'], ['3', '3'],

    // ⚠️ 后缀型键的包裹模板必须带 {0}，否则选中一段按下去会把选区吞掉
    ['x²', '²', 0, '({0})²', '选中一段后按此键，给它加平方'],
    ['x³', '³', 0, '({0})³'],
    ['xⁿ', '^', 0, '({0})^', '乘方：底数后按 ^，再输指数'],
    ['0', '0'], ['.', '.'], ['⌫', '', 0, null, '删除光标前一个字符', 1],

    // ⛔ π / e 故意不给包裹模板：常数直接插进光标处，不该顶掉选区
    ['|x|', 'abs()', 1, 'abs({0})', '绝对值'],
    ['π', 'π'],
    ['e', 'e'],
    ['+', '+'], ['−', '−'], ['×', '×'],

    ['(', '(', 0, '({0})'],
    [')', ')'],
    ['x', 'x', 0, null, '自变量'],
    ['÷', '÷'], [',', ','], ['清空', '', 0, null, '清空这一条表达式', 1]
  ];

  function insertCap(box, cap) {
    var text = box.value;
    var s = box.selectionStart == null ? text.length : box.selectionStart;
    var e = box.selectionEnd == null ? text.length : box.selectionEnd;
    var sel = text.slice(s, e);
    var wrap = cap[3];
    var insert = cap[1];
    var next, caret;

    if (cap[5]) {                       // 命令键
      if (cap[0] === '⌫') {
        if (s !== e) { next = text.slice(0, s) + text.slice(e); caret = s; }
        else if (s > 0) { next = text.slice(0, s - 1) + text.slice(s); caret = s - 1; }
        else { next = text; caret = 0; }
      } else {                          // 清空
        next = ''; caret = 0;
      }
    } else if (sel) {
      // ⛔ 必须拼回选区**前后**的文本 —— 只放包裹结果会把后半句吃掉（单测抓到的真 bug）
      var wrapped = wrap ? wrap.replace('{0}', sel) : insert;
      next = text.slice(0, s) + wrapped + text.slice(e);
      caret = s + wrapped.length;
    } else {
      // 无选区：wrap 模板把 {0} 换成空串；光标尽量落进最后一对括号中间（sin() → 落在括号内）
      var ins = wrap ? wrap.replace('{0}', '') : insert;
      next = text.slice(0, s) + ins + text.slice(e);
      caret = s + ins.length;
      if (wrap) {
        var open = ins.lastIndexOf('(');
        if (open >= 0 && ins.charAt(ins.length - 1) === ')') caret = s + open + 1;
      }
    }

    box.value = next;
    box.setSelectionRange(caret, caret);
    box.focus();
    recalc();
  }

  CAPS.forEach(function (cap) {
    var b = document.createElement('button');
    b.className = 'win-btn';
    b.type = 'button';
    b.textContent = cap[0];
    if (cap[4]) b.title = cap[4];
    // ⛔ mousedown 就拦住：否则点一下键盘输入框先失焦，选区/光标位置丢了
    b.addEventListener('mousedown', function (e) { e.preventDefault(); });
    b.addEventListener('click', function () {
      var box = keypadTarget || (curves.length ? curves[curves.length - 1].box : null);
      if (box) insertCap(box, cap);
    });
    keypad.appendChild(b);
  });

  // ──────────────────────── 右栏其他交互 ────────────────────────

  document.getElementById('addBtn').addEventListener('click', function () {
    addCurve('', true);
  });
  document.getElementById('angleBox').addEventListener('change', function () {
    mode = document.getElementById('angleBox').value;
    recalc();
  });
  targetBox.addEventListener('change', recalc);

  // ──────────────────────── 左栏交互 ────────────────────────

  document.getElementById('collapseBtn').addEventListener('click', function () {
    analysisCollapsed = true;
    document.getElementById('analysisPanel').hidden = true;
    document.getElementById('analysisExpand').hidden = false;
  });
  document.getElementById('expandBtn').addEventListener('click', function () {
    analysisCollapsed = false;
    document.getElementById('analysisPanel').hidden = false;
    document.getElementById('analysisExpand').hidden = true;
  });
  document.getElementById('basisBtn').addEventListener('click', function () {
    basisNote.hidden = !basisNote.hidden;
  });

  // ──────────────────────── 布局：窄窗收起分析栏 ────────────────────────
  // 阈值按三栏最小宽度算：272（分析）+ 332（输入键盘）+ 300（画布下限）= 904。
  // ⚠️ 别拍一个整数：设 1000 会在 936px 的常规窗口里把分析栏收掉，而原生那个宽度是显示的。
  function layout() {
    root.classList.toggle('narrow', window.innerWidth < 910);
    resize();
  }
  window.addEventListener('resize', layout);

  // ──────────────────────── 主题 ────────────────────────

  // 宿主写 documentElement.dataset.theme；主题一变，网格/轴/刻度都要重画
  new MutationObserver(function () { refreshTheme(); paint(); }).observe(document.documentElement, {
    attributes: true, attributeFilter: ['data-theme', 'class']
  });

  // ──────────────────────── 启动 ────────────────────────

  // 告诉宿主"我起来了"，宿主会再推一次当前主题
  if (window.chrome && window.chrome.webview) {
    try { window.chrome.webview.postMessage('ready'); } catch (_) { }
  }

  refreshTheme();
  addCurve('sin(x)', false);
  layout();
})();
