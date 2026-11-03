/* 待办小助手 · 手机端
 * 说明：故意只用内存变量保存状态，不依赖 localStorage / sessionStorage
 * （现代浏览器在 http://局域网IP 这种「不安全来源」下可能禁用 Web Storage）。 */
(function () {
  'use strict';

  var $ = function (s) { return document.querySelector(s); };
  var $$ = function (s) { return Array.prototype.slice.call(document.querySelectorAll(s)); };

  var state = {
    data: null,          // 服务器返回的 { now, days:[...], stats:{...} }
    tab: 0,              // 0=今天 1=明天 2=后天 3=大后天 4=5天后
    viewMode: 'list',    // list=列表 schedule=时间表
    lead: 30,            // 默认提前提醒分钟
    dateMode: 'today',
    loading: false,
    syncedAt: 0,
    lastSignature: ''
  };

  var DAY_MS = 86400000;

  // ---------------- 基础工具 ----------------

  function toast(msg) {
    var el = $('#toast');
    el.textContent = msg;
    el.classList.add('on');
    clearTimeout(toast._t);
    toast._t = setTimeout(function () { el.classList.remove('on'); }, 2200);
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }
  function todayStr() { var d = new Date(); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }

  function setSync(text, cls) {
    var el = $('#sync');
    el.textContent = text;
    el.className = 'sync' + (cls ? ' ' + cls : '');
  }

  function api(path, options) {
    options = options || {};
    options.headers = Object.assign({ 'Content-Type': 'application/json' }, options.headers || {});
    options.cache = 'no-store';
    return fetch(path, options).then(function (r) {
      if (r.status === 401) {
        location.replace('/');
        throw new Error('登录已失效');
      }
      return r.json().catch(function () { return {}; }).then(function (j) {
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        return j;
      });
    });
  }

  // ---------------- 渲染 ----------------

  function remainingText(startAt) {
    if (!startAt) return '';
    var diff = new Date(startAt).getTime() - Date.now();
    var mins = Math.round(diff / 60000);
    if (mins <= 0) return '';
    if (mins < 60) return '还有 ' + mins + ' 分钟';
    var h = Math.floor(mins / 60), m = mins % 60;
    return '还有 ' + h + ' 小时' + (m ? ' ' + m + ' 分' : '');
  }

  function isPast(item) {
    if (item.done) return false;
    var end = item.end ? item.date + 'T' + item.end + ':00' : (item.startAt || '');
    if (!end) return false;
    return new Date(end).getTime() < Date.now();
  }

  function isSoon(item) {
    if (item.done || !item.startAt) return false;
    var d = new Date(item.startAt).getTime() - Date.now();
    return d > 0 && d <= 30 * 60000;
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function render() {
    if (!state.data) return;
    var days = state.data.days || [];

    // 顶栏
    var s = state.data.stats || {};
    $('#today-summary').textContent = '今天 ' + (s.totalToday || 0) + ' 项待办 · 未完成 ' + (s.openToday || 0) + ' 项';

    // 标签日期 + 未完成角标
    $$('#day-tabs .tab').forEach(function (btn, i) {
      var d = days[i];
      if (!d) return;
      var parts = d.date.split('-');
      btn.querySelector('.tab-date').textContent = parseInt(parts[1], 10) + '/' + parseInt(parts[2], 10);
      var open = (d.items || []).filter(function (x) { return !x.done; }).length;
      var badge = btn.querySelector('.badge');
      if (open > 0 && i !== state.tab) { badge.hidden = false; badge.textContent = open; }
      else badge.hidden = true;
      btn.classList.toggle('active', i === state.tab);
    });

    var day = days[state.tab];
    var list = $('#list');

    // 列表与时间表互斥显示
    list.hidden = state.viewMode !== 'list';

    if (!day || !day.items || day.items.length === 0) {
      list.innerHTML = '<div class="empty"><div class="big">🗒️</div>' + (state.tab === 0 ? '今天还没有安排，' : '') +
        '点右下角 + 添加一条待办</div>';
      renderSchedule(day);   // 空列表也要渲染时间表的空状态
      return;
    }

    list.innerHTML = day.items.map(function (it) {
      var cls = 'item' + (it.done ? ' done' : '') + (isPast(it) ? ' past' : isSoon(it) ? ' soon' : '');
      var remain = it.done ? '' : remainingText(it.startAt);
      var remainCls = 'remain' + (isSoon(it) ? ' warn' : '');
      var tags = [];
      if (it.repeatText) tags.push('<span class="tag">🔁 ' + esc(it.repeatText) + '</span>');
      tags.push('<span class="tag">⏰ 提前 ' + it.leadMinutes + ' 分钟</span>');
      if (it.hasDeadline) {
        tags.push('<span class="tag ddl' + (it.deadlineOverdue ? ' overdue' : '') + '">⏳ DDL ' + esc(it.deadlineText || '') + '</span>');
      }
      return '<div class="' + cls + '" data-id="' + esc(it.id) + '" data-date="' + esc(it.date) + '">' +
        '<button class="chk' + (it.done ? ' on' : '') + '" data-act="toggle" aria-label="完成"></button>' +
        '<div class="body">' +
          '<div class="row1">' +
            '<span class="time">' + esc(it.start) + (it.end ? ' - ' + esc(it.end) : '') + '</span>' +
            (remain ? '<span class="' + remainCls + '">' + remain + '</span>' : '') +
          '</div>' +
          '<div class="t-title">' + esc(it.title) + '</div>' +
          (it.notes ? '<div class="notes">' + esc(it.notes) + '</div>' : '') +
          '<div class="tags">' + tags.join('') + '</div>' +
        '</div>' +
        '<div class="ops">' +
          '<button class="op" data-act="move">顺延</button>' +
          '<button class="op del" data-act="del">删除</button>' +
        '</div>' +
      '</div>';
    }).join('');

    renderSchedule(day);
  }

  // ---------------- 时间表视图（与电脑端一致：按时间段排布） ----------------

  var SCH_START = 6, SCH_END = 24, SCH_HOUR = 58;

  function toMinutes(hhmm) {
    if (!hhmm) return null;
    var p = String(hhmm).split(':');
    var h = parseInt(p[0], 10), m = parseInt(p[1], 10);
    if (isNaN(h) || isNaN(m)) return null;
    return h * 60 + m;
  }

  function renderSchedule(day) {
    var box = $('#schedule');
    if (state.viewMode !== 'schedule') { box.hidden = true; box.innerHTML = ''; return; }
    box.hidden = false;

    var items = ((day && day.items) || []).filter(function (i) { return !i.done; });
    var totalH = (SCH_END - SCH_START);
    var height = totalH * SCH_HOUR;

    // 时间轴刻度
    var grid = '';
    for (var h = SCH_START; h <= SCH_END; h++) {
      var y = (h - SCH_START) * SCH_HOUR;
      grid += '<div class="sch-line" style="top:' + y + 'px"></div>';
      grid += '<div class="sch-hour" style="top:' + (y - 8) + 'px">' +
        (h < 10 ? '0' + h : h) + ':00</div>';
    }

    // 任务块：计算起止、处理重叠
    var blocks = [];
    items.forEach(function (it, idx) {
      var sm = toMinutes(it.start);
      if (sm === null) return;
      var em = toMinutes(it.end);
      var startMin = sm;
      var endMin = (em !== null && em > sm) ? em : (em !== null && em < sm ? 24 * 60 : sm + 60);
      if (endMin <= SCH_START * 60 || startMin >= SCH_END * 60) return;
      blocks.push({
        it: it,
        index: idx,
        s: Math.max(startMin, SCH_START * 60),
        e: Math.min(endMin, SCH_END * 60)
      });
    });
    blocks.sort(function (a, b) { return a.s - b.s || a.e - b.e; });

    // 车道分配（重叠的并排）
    var laneEnds = [];
    blocks.forEach(function (b) {
      var lane = -1;
      for (var i = 0; i < laneEnds.length; i++) {
        if (laneEnds[i] <= b.s + 1) { lane = i; break; }
      }
      if (lane < 0) { lane = laneEnds.length; laneEnds.push(0); }
      laneEnds[lane] = b.e;
      b.lane = lane;
    });
    var laneCount = Math.max(1, laneEnds.length);

    var colors = ['#5B9BD5', '#4DB6AC', '#9C7BD8', '#E0A458', '#E07B9B', '#64B5F6'];
    var html = grid;
    blocks.forEach(function (b, i) {
      var top = (b.s / 60 - SCH_START) * SCH_HOUR;
      var hgt = Math.max(26, (b.e - b.s) / 60 * SCH_HOUR - 3);
      var color = colors[i % colors.length];
      var laneW = 100 / laneCount;
      var left = (b.lane * laneW) + '%';
      var width = 'calc(' + laneW + '% - 4px)';
      var it = b.it;
      var timeText = it.start + (it.end ? ' - ' + it.end : '');
      html += '<div class="sch-block" data-id="' + esc(it.id) + '" data-date="' + esc(it.date) + '"' +
        ' style="top:' + top + 'px;height:' + hgt + 'px;left:' + left + ';width:' + width + ';' +
        'border-left-color:' + color + ';background:' + hexA(color, 0.16) + '">' +
        '<div class="sch-title">' + esc(it.title) + '</div>' +
        '<div class="sch-time" style="color:' + color + '">' + esc(timeText) + '</div>' +
        (it.notes && hgt > 52 ? '<div class="sch-notes">' + esc(it.notes) + '</div>' : '') +
        (it.hasDeadline && hgt > 68 ? '<div class="sch-ddl">⏳ ' + esc(it.deadlineText || '') + '</div>' : '') +
      '</div>';
    });

    // 当前时间红线（仅今天）
    if (state.tab === 0) {
      var now = new Date();
      var nowMin = now.getHours() * 60 + now.getMinutes();
      if (nowMin >= SCH_START * 60 && nowMin < SCH_END * 60) {
        var ny = (nowMin / 60 - SCH_START) * SCH_HOUR;
        html += '<div class="sch-now" style="top:' + ny + 'px"></div>';
      }
    }

    if (blocks.length === 0) {
      html += '<div class="sch-empty">这一天还没有安排<br>点右下角 + 添加</div>';
    }

    box.innerHTML = '<div class="sch-canvas" style="height:' + height + 'px">' + html + '</div>';

    // 首次切到时间表：滚到当前时间附近
    if (state.tab === 0) {
      var now2 = new Date();
      var target = Math.max(0, (now2.getHours() - SCH_START - 1.5) * SCH_HOUR);
      box.scrollTop = target;
    } else {
      box.scrollTop = 0;
    }
  }

  // 把 #RRGGBB 转成带 alpha 的 rgba
  function hexA(hex, a) {
    var n = parseInt(hex.slice(1), 16);
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')';
  }

  // ---------------- 数据同步 ----------------

  function signatureOf(data) {
    try {
      return (data.days || []).map(function (d) {
        return d.date + ':' + (d.items || []).map(function (i) {
          return i.id + i.done + i.title + i.start + i.end + i.notes + i.repeat + i.leadMinutes + (i.doneAt || '');
        }).join(',');
      }).join('|');
    } catch (e) { return '' + Date.now(); }
  }

  function refresh(force) {
    if (state.loading) return Promise.resolve();
    state.loading = true;
    setSync('同步中…', 'busy');
    return api('/api/today').then(function (data) {
      var sig = signatureOf(data);
      var changed = sig !== state.lastSignature;
      state.lastSignature = sig;
      state.data = data;
      state.syncedAt = Date.now();
      if (changed || force) render();
      setSync('已连接');
    }).catch(function (err) {
      setSync('离线', 'off');
      if (force) toast('连不上电脑：' + err.message);
    }).then(function () {
      state.loading = false;
    });
  }

  // ---------------- 交互 ----------------

  // ---------------- 视图切换 ----------------

  $('#view-switch').addEventListener('click', function (e) {
    var btn = e.target.closest('.vs-btn');
    if (!btn) return;
    state.viewMode = btn.getAttribute('data-view');
    $$('#view-switch .vs-btn').forEach(function (b) {
      b.classList.toggle('active', b === btn);
    });
    render();
  });

  // 点时间表里的任务块：显示详情并可选操作
  $('#schedule').addEventListener('click', function (e) {
    var blk = e.target.closest('.sch-block');
    if (!blk) return;
    var id = blk.getAttribute('data-id');
    var date = blk.getAttribute('data-date');
    var item = null;
    (state.data.days || []).forEach(function (d) {
      (d.items || []).forEach(function (i) { if (i.id === id && i.date === date) item = i; });
    });
    if (!item) return;

    var msg = '「' + item.title + '」\n';
    msg += '时间：' + item.start + (item.end ? ' - ' + item.end : '') + '\n';
    if (item.notes) msg += '备注：' + item.notes + '\n';
    if (item.repeatText) msg += '重复：' + item.repeatText + '\n';
    if (item.hasDeadline) msg += 'DDL：' + (item.deadlineText || '') + '\n';
    msg += '\n点「确定」标记为已完成，点「取消」关闭。';

    if (!confirm(msg)) return;
    api('/api/toggle', { method: 'POST', body: JSON.stringify({ id: id, date: date, done: true }) })
      .then(function () { toast('已完成 ✓'); return refresh(true); })
      .catch(function (err) { toast('操作失败：' + err.message); });
  });

  $('#day-tabs').addEventListener('click', function (e) {
    var btn = e.target.closest('.tab');
    if (!btn) return;
    state.tab = parseInt(btn.getAttribute('data-tab'), 10) || 0;
    render();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  });

  $('#list').addEventListener('click', function (e) {
    var card = e.target.closest('.item');
    if (!card) return;
    var act = e.target.getAttribute('data-act');
    if (!act) return;
    var id = card.getAttribute('data-id');
    var date = card.getAttribute('data-date');
    var item = null;
    (state.data.days || []).forEach(function (d) {
      (d.items || []).forEach(function (i) { if (i.id === id && i.date === date) item = i; });
    });
    if (!item) return;

    if (act === 'toggle') {
      card.classList.toggle('done');
      api('/api/toggle', { method: 'POST', body: JSON.stringify({ id: id, date: date, done: !item.done }) })
        .then(function () { toast(item.done ? '已恢复' : '已完成 ✓'); return refresh(true); })
        .catch(function (err) { toast('操作失败：' + err.message); refresh(true); });
    } else if (act === 'del') {
      if (!confirm('删除「' + item.title + '」？')) return;
      api('/api/delete', { method: 'POST', body: JSON.stringify({ id: id }) })
        .then(function () { toast('已删除'); return refresh(true); })
        .catch(function (err) { toast('删除失败：' + err.message); });
    } else if (act === 'move') {
      var days = state.data.days || [];
      var target = days[state.tab + 1];
      if (!target) {
        // 最后一天再往后：按当前日期 +1 天
        var dd = new Date(date + 'T12:00:00');
        dd.setDate(dd.getDate() + 1);
        target = { date: dd.getFullYear() + '-' + pad(dd.getMonth() + 1) + '-' + pad(dd.getDate()), label: '下一天' };
      }
      api('/api/move', { method: 'POST', body: JSON.stringify({ id: id, date: target.date }) })
        .then(function () { toast('已顺延到 ' + target.date); return refresh(true); })
        .catch(function (err) { toast('操作失败：' + err.message); });
    }
  });

  // ---- 新增面板 ----

  function openSheet() {
    var now = new Date();
    var start = new Date(now.getTime() + 60 * 60000);
    start.setMinutes(Math.ceil(start.getMinutes() / 5) * 5, 0, 0);
    $('#f-start').value = pad(start.getHours()) + ':' + pad(start.getMinutes());
    var end = new Date(start.getTime() + 60 * 60000);
    $('#f-end').value = pad(end.getHours()) + ':' + pad(end.getMinutes());
    $('#f-title').value = '';
    $('#f-notes').value = '';
    $('#f-lead').value = state.lead;
    $('#f-repeat').value = '';
    state.dateMode = ['today', 'tomorrow', 'dayafter', 'day4', 'day5'][state.tab] || 'today';
    $$('#date-seg button').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-date') === state.dateMode); });
    $('#f-date').hidden = true;
    $('#f-date').value = todayStr();
    $('#mask').classList.add('on');
    $('#sheet').classList.add('on');
    setTimeout(function () { $('#f-title').focus(); }, 260);
  }

  function closeSheet() {
    $('#mask').classList.remove('on');
    $('#sheet').classList.remove('on');
  }

  $('#fab').addEventListener('click', openSheet);
  $('#cancel').addEventListener('click', closeSheet);
  $('#mask').addEventListener('click', closeSheet);

  $('#date-seg').addEventListener('click', function (e) {
    var b = e.target.closest('button');
    if (!b) return;
    state.dateMode = b.getAttribute('data-date');
    $$('#date-seg button').forEach(function (x) { x.classList.toggle('on', x === b); });
    $('#f-date').hidden = state.dateMode !== 'custom';
  });

  $('#save').addEventListener('click', function () {
    var title = $('#f-title').value.trim();
    var start = $('#f-start').value;
    if (!title) { toast('请填写待办内容'); $('#f-title').focus(); return; }
    if (!start) { toast('请选择开始时间'); return; }
    var payload = {
      title: title,
      start: start,
      end: $('#f-end').value || '',
      notes: $('#f-notes').value.trim(),
      leadMinutes: parseInt($('#f-lead').value, 10) || 30,
      repeat: $('#f-repeat').value || '',
      dateMode: state.dateMode,
      date: state.dateMode === 'custom' ? $('#f-date').value : ''
    };
    var btn = $('#save');
    btn.disabled = true;
    api('/api/items', { method: 'POST', body: JSON.stringify(payload) })
      .then(function () {
        closeSheet();
        toast('已添加 · ' + payload.start + ' 会提前提醒');
        // 跳到对应日期标签
        var idx = { today: 0, tomorrow: 1, dayafter: 2, day4: 3, day5: 4 }[state.dateMode];
        if (idx !== undefined) state.tab = idx;
        else if (payload.date === state.data.days[0].date) state.tab = 0;
        return refresh(true);
      })
      .catch(function (err) { toast('保存失败：' + err.message); })
      .then(function () { btn.disabled = false; });
  });

  // ---------------- 启动与自动刷新 ----------------

  function applyConfig() {
    return api('/api/config').then(function (c) {
      state.lead = c.leadMinutes || 30;
    }).catch(function () { });
  }

  applyConfig().then(function () {
    setSync('连接中…', 'busy');
    return refresh(true);
  });

  // 电脑端一改，手机端立即跟着变：3 秒轮询（服务器是本地局域网，开销极小）
  setInterval(function () {
    if (document.hidden) {
      // 切到后台时降低频率
      if (Date.now() - state.syncedAt > 60000) refresh(false);
      return;
    }
    refresh(false);
  }, 3000);

  document.addEventListener('visibilitychange', function () {
    if (!document.hidden) { applyConfig().then(function () { refresh(false); }); }
  });

  window.addEventListener('online', function () { refresh(false); });
  window.addEventListener('offline', function () { setSync('离线', 'off'); });

  // 注册 Service Worker（可安装 + 更快打开）
  if ('serviceWorker' in navigator) {
    window.addEventListener('load', function () {
      navigator.serviceWorker.register('/sw.js').catch(function () { });
    });
  }
})();
